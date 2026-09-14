using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace HenryMonitor;

public sealed class ApiServer : IAsyncDisposable
{
    private readonly HardwareMonitorService _monitor;
    private readonly ConfigStore _config;
    private readonly PairingManager _pairing;
    private readonly WindowsControlService _controls = new();
    private readonly RequestAuthenticator _authenticator;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _pairAttempts = new();
    private WebApplication? _app;
    private DiscoveryService? _discovery;

    public event EventHandler? DevicePaired;
    public event EventHandler<string>? ServerError;

    public ApiServer(HardwareMonitorService monitor, ConfigStore config, PairingManager pairing)
    {
        _monitor = monitor;
        _config = config;
        _pairing = pairing;
        _authenticator = new RequestAuthenticator(config);
    }

    public async Task StartAsync()
    {
        try
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                Args = Array.Empty<string>(),
                ContentRootPath = AppContext.BaseDirectory
            });
            builder.WebHost.UseUrls($"http://0.0.0.0:{_config.Current.Port}");
            builder.Services.ConfigureHttpJsonOptions(options =>
            {
                options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                options.SerializerOptions.WriteIndented = false;
            });
            builder.Logging.ClearProviders();

            _app = builder.Build();
            MapRoutes(_app);
            await _app.StartAsync(_stop.Token);

            _discovery = new DiscoveryService(_config.Current.Port);
            _discovery.Start();
        }
        catch (Exception ex)
        {
            ServerError?.Invoke(this, ex.Message);
            throw;
        }
    }

    private void MapRoutes(WebApplication app)
    {
        app.MapGet("/api/health", () => Results.Ok(new
        {
            product = "Henry System Monitor",
            protocol = 1,
            computerName = Environment.MachineName,
            port = _config.Current.Port,
            ready = _monitor.Latest.Sequence > 0
        }));

        // The dedicated phone may have no SIM and an inaccurate system clock.
        // This endpoint lets it calculate an offset before signing requests.
        app.MapGet("/api/time", () => Results.Ok(new
        {
            unixTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        }));

        app.MapPost("/api/pair", (HttpContext context, PairRequest request) =>
        {
            var client = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            if (!AllowPairAttempt(client))
                return Results.Json(new ApiError("Too many attempts. Wait one minute."), statusCode: 429);
            if (!_pairing.Validate(request.Code ?? ""))
                return Results.Json(new ApiError("That pairing code is incorrect or expired."), statusCode: 401);

            _config.MarkPaired(request.DeviceName ?? "Henry display");
            _pairing.Rotate();
            DevicePaired?.Invoke(this, EventArgs.Empty);
            return Results.Ok(new PairResponse(
                _config.Current.AccessToken,
                Environment.MachineName,
                _config.Current.Port,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
        });

        app.MapGet("/api/status", (HttpContext context) =>
        {
            if (!_authenticator.Validate(context)) return Results.Unauthorized();
            return Results.Ok(_monitor.Latest);
        });

        app.MapPost("/api/control", (HttpContext context, ControlRequest request) =>
        {
            var action = request.Action ?? "";
            if (!_authenticator.Validate(context, action)) return Results.Unauthorized();
            var confirmed = string.Equals(request.Confirmation, "HOLD_CONFIRMED", StringComparison.Ordinal);
            var result = _controls.Execute(action, confirmed);
            return result.Success
                ? Results.Ok(new { message = result.Message })
                : Results.Json(new ApiError(result.Message), statusCode: 400);
        });
    }

    private bool AllowPairAttempt(string client)
    {
        var now = DateTimeOffset.UtcNow;
        var queue = _pairAttempts.GetOrAdd(client, _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() > TimeSpan.FromMinutes(1)) queue.Dequeue();
            if (queue.Count >= 8) return false;
            queue.Enqueue(now);
            return true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        if (_discovery is not null) await _discovery.DisposeAsync();
        if (_app is not null)
        {
            try { await _app.StopAsync(TimeSpan.FromSeconds(3)); } catch { }
            await _app.DisposeAsync();
        }
        _stop.Dispose();
    }
}
