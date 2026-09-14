using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace HenryMonitor;

public sealed class DiscoveryService : IAsyncDisposable
{
    public const int DiscoveryPort = 47832;
    public const string DiscoveryMessage = "HENRY_MONITOR_DISCOVER_V1";
    private readonly int _apiPort;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private UdpClient? _udp;

    public DiscoveryService(int apiPort) => _apiPort = apiPort;

    public void Start()
    {
        _udp = new UdpClient(new IPEndPoint(IPAddress.Any, DiscoveryPort));
        _udp.EnableBroadcast = true;
        _loop = Task.Run(ListenLoop);
    }

    private async Task ListenLoop()
    {
        if (_udp is null) return;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var result = await _udp.ReceiveAsync(_stop.Token);
                if (!Encoding.UTF8.GetString(result.Buffer).Equals(DiscoveryMessage, StringComparison.Ordinal))
                    continue;

                var response = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    protocol = 1,
                    computerName = Environment.MachineName,
                    port = _apiPort,
                    pairingRequired = true
                });
                await _udp.SendAsync(response, result.RemoteEndPoint, _stop.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (SocketException) when (_stop.IsCancellationRequested) { break; }
            catch { await Task.Delay(500, _stop.Token); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _udp?.Dispose();
        if (_loop is not null)
        {
            try { await _loop; } catch { }
        }
        _stop.Dispose();
    }
}
