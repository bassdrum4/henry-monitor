using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HenryMonitor;

/// <summary>
/// Automatic self-update for the Windows agent. A published feed on the
/// project's public GitHub releases describes the newest release; the agent
/// downloads it in background, verifies the SHA-256 of the image, swaps the
/// running executable, restarts through the installed startup task, and
/// exits. "releases/latest/download/..." always resolves to the most recent
/// published release, anonymously.
/// </summary>
public sealed class UpdateService
{
    public const string FeedUrl = "https://github.com/bassdrum4/henry-monitor/releases/latest/download/feed.json";
    private const int CheckIntervalHours = 6;
    private const int FirstCheckDelayMinutes = 2;

    private static readonly string AppDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HenryMonitor");
    private static readonly string AgentPath = Path.Combine(AppDirectory, "HenryMonitor.exe");
    private static readonly string BackupPath = AgentPath + ".old";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    private readonly string _stagingDirectory = Path.Combine(AppDirectory, "update-staging");
    private readonly Action _requestExit;
    private readonly object _sync = new();
    private bool _checking;

    public UpdateService(Action requestExit) => _requestExit = requestExit;

    private sealed record UpdatePackage(
        string Version,
        int VersionCode,
        DateTimeOffset PublishedUtc,
        string Path,
        long Size,
        string Sha256,
        string[] Chunks,
        string? ReleaseNotes);

    private sealed record UpdateFeed(string Channel, UpdatePackage? Agent, UpdatePackage? Android);

    /// <summary>Runs the periodic check loop on a background thread.</summary>
    public void Start() => new Thread(CheckLoop) { IsBackground = true, Name = "update-check" }.Start();

    private async void CheckLoop()
    {
        await Task.Delay(TimeSpan.FromMinutes(FirstCheckDelayMinutes)).ConfigureAwait(false);
        while (true)
        {
            try { await CheckOnceAsync().ConfigureAwait(false); }
            catch (Exception ex) { AppRuntime.WriteLog("Update check failed.", ex); }
            await Task.Delay(TimeSpan.FromHours(CheckIntervalHours)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// One manual or scheduled update pass. Returns a short human summary.
    /// Safe to call from any thread; concurrent checks collapse into one.
    /// </summary>
    public string CheckNow()
    {
        lock (_sync)
        {
            if (_checking) return "An update check is already running.";
            _checking = true;
        }
        try
        {
            return CheckOnceAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            AppRuntime.WriteLog("Manual update check failed.", ex);
            return "Update check failed: " + ex.Message;
        }
        finally
        {
            lock (_sync) _checking = false;
        }
    }

    private async Task<string> CheckOnceAsync()
    {
        using var response = await Http.GetAsync(
            FeedUrl + "?t=" + DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        // GitHub serves release assets as application/octet-stream, so parse
        // the body explicitly instead of relying on content-type negotiation.
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var feed = JsonSerializer.Deserialize<UpdateFeed>(body, JsonOptions);
        var package = feed?.Agent;
        if (package is null) return "No agent release is published in the feed.";

        var current = Assembly.GetExecutingAssembly().GetName().Version!;
        var incoming = Version.TryParse(package.Version, out var parsed) ? parsed : null;
        if (incoming is null || incoming <= current)
            return $"Already up to date (agent {current.ToString(3)}).";

        AppRuntime.WriteLog($"Downloading agent update {package.Version} ({package.Size} bytes).");
        var downloaded = await DownloadAsync(package).ConfigureAwait(false);

        ApplySwap(downloaded, package.Version);
        return $"Updated to {package.Version}. The monitor restarts now.";
    }

    /// <summary>Downloads all chunks into staging and returns the assembled file path.</summary>
    private async Task<string> DownloadAsync(UpdatePackage package)
    {
        Directory.CreateDirectory(_stagingDirectory);
        var assembledPath = Path.Combine(_stagingDirectory, "HenryMonitor.exe.new");

        // Chunked layout: the single image is too large for one static-host file.
        var chunks = package.Chunks is { Length: > 0 }
            ? package.Chunks
            : new[] { package.Path };
        var parts = new List<string>(chunks.Length);
        try
        {
            foreach (var chunk in chunks)
            {
                var partPath = Path.Combine(_stagingDirectory,
                    Path.GetFileName(chunk) + ".download");
                var url = FeedUrl.Replace("/feed.json", "/" + chunk);
                var bytes = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
                await File.WriteAllBytesAsync(partPath, bytes).ConfigureAwait(false);
                parts.Add(partPath);
            }

            await using (var assembled = File.Create(assembledPath))
            {
                foreach (var part in parts)
                    await assembled.WriteAsync(await File.ReadAllBytesAsync(part).ConfigureAwait(false))
                        .ConfigureAwait(false);
            }
        }
        finally
        {
            foreach (var part in parts)
            {
                try { File.Delete(part); } catch { }
            }
        }

        var actual = await Sha256Async(assembledPath).ConfigureAwait(false);
        if (!string.Equals(actual, package.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            SafeDelete(assembledPath);
            throw new InvalidOperationException(
                "The downloaded update failed its checksum. Nothing was changed.");
        }
        return assembledPath;
    }

    /// <summary>
    /// Swaps the verified image in for the running one and restarts through
    /// the elevated startup task. Windows allows renaming a running image, so
    /// the current exe is set aside as HenryMonitor.exe.old first.
    /// </summary>
    private void ApplySwap(string downloadedPath, string version)
    {
        if (new FileInfo(downloadedPath).Length < 1_000_000)
            throw new InvalidOperationException("The downloaded update is implausibly small; refusing to install it.");

        // Set the running image aside (Windows permits renaming a running
        // exe), place the verified one, then restart via the startup task.
        // If the new image cannot start, RecoverIfBroken restores the old one.
        try { File.Delete(BackupPath); } catch { }
        try
        {
            File.Move(AgentPath, BackupPath, true);
        }
        catch (Exception ex)
        {
            AppRuntime.WriteLog("Could not set the current image aside for update.", ex);
            throw;
        }
        try
        {
            File.Move(downloadedPath, AgentPath, true);
        }
        catch (Exception ex)
        {
            // Restore the original image rather than leaving nothing in place.
            try { File.Move(BackupPath, AgentPath, true); } catch { }
            AppRuntime.WriteLog("Could not place the downloaded update.", ex);
            throw;
        }

        AppRuntime.WriteLog($"Agent update {version} installed; restarting.");
        // The startup task's --background instance gives up immediately when
        // another process still owns the instance lock, so relaunching right
        // now would lose the agent. Instead a detached helper waits for this
        // process to exit and only then starts the task.
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                Arguments = "/c timeout /t 3 /nobreak >nul & schtasks /Run /TN \"Henry System Monitor\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch (Exception ex)
        {
            AppRuntime.WriteLog("Could not schedule the post-update restart.", ex);
        }
        _requestExit();
    }

    /// <summary>Restores the previous image if a swap left the install broken.</summary>
    public static void RecoverIfBroken()
    {
        try
        {
            if (!File.Exists(BackupPath)) return;
            if (!File.Exists(AgentPath))
                File.Move(BackupPath, AgentPath, true);
            else
                File.Delete(BackupPath);
        }
        catch { }
    }

    public static void CleanStaging()
    {
        try
        {
            var staging = Path.Combine(AppDirectory, "update-staging");
            if (Directory.Exists(staging)) Directory.Delete(staging, true);
        }
        catch { }
    }

    private static void SafeDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private static async Task<string> Sha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }
}
