using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HenryMonitor;

public sealed class WindowsControlService
{
    private const byte VkVolumeMute = 0xAD;
    private const byte VkVolumeDown = 0xAE;
    private const byte VkVolumeUp = 0xAF;
    private const uint KeyEventKeyUp = 0x0002;

    public IReadOnlySet<string> SupportedActions { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "lock", "sleep", "hibernate", "mute", "volume_up", "volume_down",
        "restart", "shutdown"
    };

    public (bool Success, string Message) Execute(string action, bool confirmed)
    {
        action = action.Trim().ToLowerInvariant();
        if (!SupportedActions.Contains(action)) return (false, "Unsupported control.");

        if (action is "sleep" or "hibernate" or "restart" or "shutdown" && !confirmed)
            return (false, "This control requires hold confirmation.");

        try
        {
            switch (action)
            {
                case "lock":
                    return (LockWorkStation(), "PC locked.");
                case "sleep":
                    return (SetSuspendState(false, false, false), "PC entering sleep.");
                case "hibernate":
                    return (SetSuspendState(true, false, false), "PC entering hibernation.");
                case "mute":
                    SendMediaKey(VkVolumeMute);
                    return (true, "Mute toggled.");
                case "volume_up":
                    SendMediaKey(VkVolumeUp);
                    return (true, "Volume increased.");
                case "volume_down":
                    SendMediaKey(VkVolumeDown);
                    return (true, "Volume decreased.");
                case "restart":
                    StartShutdown("/r /t 5 /c \"Restart requested from Henry System Monitor\"");
                    return (true, "Restart scheduled in five seconds.");
                case "shutdown":
                    StartShutdown("/s /t 5 /c \"Shutdown requested from Henry System Monitor\"");
                    return (true, "Shutdown scheduled in five seconds.");
                default:
                    return (false, "Unsupported control.");
            }
        }
        catch (Exception ex)
        {
            return (false, $"Windows rejected the control: {ex.Message}");
        }
    }

    private static void StartShutdown(string arguments)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "shutdown.exe"),
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    private static void SendMediaKey(byte key)
    {
        keybd_event(key, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, KeyEventKeyUp, UIntPtr.Zero);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool LockWorkStation();

    [DllImport("powrprof.dll", SetLastError = true)]
    private static extern bool SetSuspendState(bool hibernate, bool forceCritical, bool disableWakeEvent);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
}
