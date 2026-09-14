using System.Diagnostics;
using System.Security.Principal;

namespace HenryMonitor;

internal static class AppRuntime
{
    private const string StartupTaskName = "Henry System Monitor";
    private static readonly string AppDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HenryMonitor");
    private static readonly string InstanceLockPath = Path.Combine(AppDirectory, "agent.lock");
    private static readonly string ShowRequestPath = Path.Combine(AppDirectory, "show-window.request");
    private static readonly string ReadyPath = Path.Combine(AppDirectory, "agent.ready");
    private static readonly string LogDirectory = Path.Combine(AppDirectory, "logs");
    private static readonly string LogPath = Path.Combine(LogDirectory, "henry-monitor.log");

    public static bool IsAdministrator()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    public static FileStream? TryAcquireInstanceLock()
    {
        Directory.CreateDirectory(AppDirectory);
        try
        {
            return new FileStream(InstanceLockPath, FileMode.OpenOrCreate,
                FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    public static bool RequestShow()
    {
        try
        {
            Directory.CreateDirectory(AppDirectory);
            var temporary = ShowRequestPath + "." + Environment.ProcessId + ".tmp";
            File.WriteAllText(temporary, DateTimeOffset.UtcNow.ToString("O"));
            File.Move(temporary, ShowRequestPath, true);
            return true;
        }
        catch (Exception ex)
        {
            WriteLog("Could not create the window-open request.", ex);
            return false;
        }
    }

    public static bool WaitForShowRequestHandled(TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (!File.Exists(ShowRequestPath)) return true;
            Thread.Sleep(50);
        }
        return !File.Exists(ShowRequestPath);
    }

    /// <summary>
    /// Waits until another process owns the instance lock (the elevated task
    /// instance is up) or the ready marker names a live agent process. Returns
    /// true when an instance is serving and this normal-privilege launcher must
    /// exit instead of stealing the lock.
    /// </summary>
    public static bool WaitForElevatedInstance(TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            // Cold start of the self-contained executable takes tens of
            // seconds, so probe rather than assume the task died.
            using (var probe = TryAcquireInstanceLock())
            {
                if (probe is null) return true;
            }
            var readyProcessId = ReadReadyMarker();
            if (readyProcessId is not null && IsProcessAlive(readyProcessId.Value)) return true;
            Thread.Sleep(500);
        }
        return false;
    }

    private static int? ReadReadyMarker()
    {
        try
        {
            if (!File.Exists(ReadyPath)) return null;
            var text = File.ReadAllText(ReadyPath);
            var separator = text.IndexOf('|');
            if (separator <= 0 || !int.TryParse(text[..separator], out var processId)) return null;
            return processId;
        }
        catch { return null; }
    }

    private static bool IsProcessAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch { return false; }
    }

    public static bool ConsumeShowRequest()
    {
        try
        {
            if (!File.Exists(ShowRequestPath)) return false;
            File.Delete(ShowRequestPath);
            return true;
        }
        catch { return false; }
    }

    public static bool StartInstalledTask()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                Arguments = $"/Run /TN \"{StartupTaskName}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            });
            return process is not null && process.WaitForExit(10_000) && process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            WriteLog("Could not launch the installed startup task.", ex);
            return false;
        }
    }

    public static void MarkReady()
    {
        try
        {
            Directory.CreateDirectory(AppDirectory);
            File.WriteAllText(ReadyPath, $"{Environment.ProcessId}|{DateTimeOffset.UtcNow:O}");
        }
        catch (Exception ex)
        {
            WriteLog("Could not write the ready marker.", ex);
        }
    }

    public static void ClearReady()
    {
        try { File.Delete(ReadyPath); } catch { }
    }

    public static void WriteLog(string message, Exception? exception = null)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var details = exception is null ? "" : Environment.NewLine + exception;
            File.AppendAllText(LogPath,
                $"[{DateTimeOffset.Now:O}] {message}{details}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { }
    }

    public static string FriendlyLogPath => LogPath;
}
