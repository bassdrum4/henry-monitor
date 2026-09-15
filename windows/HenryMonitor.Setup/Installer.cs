using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;

namespace HenryMonitor.Setup;

internal sealed record InstallResult(bool WindowsInstalled, bool PhoneInstalled, string PhoneMessage);

internal static class Installer
{
    private const string AppId = "HenrySystemMonitor";
    private const string PackageName = "com.daniellowe.henrymonitor";
    private const string StartupTaskName = "Henry System Monitor";
    private static readonly string InstallDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HenryMonitor");
    private static readonly string ToolsDirectory = Path.Combine(InstallDirectory, "tools");
    private static readonly string AgentPath = Path.Combine(InstallDirectory, "HenryMonitor.exe");
    private static readonly string HenryPhotosPath = Path.Combine(InstallDirectory, "HenryPhotos.exe");
    private static readonly string SetupPath = Path.Combine(InstallDirectory, "HenryMonitorSetup.exe");
    private static readonly string ShowRequestPath = Path.Combine(InstallDirectory, "show-window.request");
    private static readonly string ReadyPath = Path.Combine(InstallDirectory, "agent.ready");

    public static InstallResult Install(Action<string> progress)
    {
        progress("Preparing secure local components…");
        Directory.CreateDirectory(InstallDirectory);
        Directory.CreateDirectory(ToolsDirectory);
        StopAgent();

        Extract("Payload.HenryMonitor.exe", AgentPath);
        Extract("Payload.HenryPhotos.exe", Path.Combine(InstallDirectory, "HenryPhotos.exe"));
        Extract("Payload.HenryMonitor.apk", Path.Combine(InstallDirectory, "HenryMonitor.apk"));
        Extract("Payload.adb.exe", Path.Combine(ToolsDirectory, "adb.exe"));
        Extract("Payload.AdbWinApi.dll", Path.Combine(ToolsDirectory, "AdbWinApi.dll"));
        Extract("Payload.AdbWinUsbApi.dll", Path.Combine(ToolsDirectory, "AdbWinUsbApi.dll"));
        Extract("Payload.PawnIO_setup.exe", Path.Combine(ToolsDirectory, "PawnIO_setup.exe"));
        Extract("Payload.THIRD_PARTY_NOTICES.txt", Path.Combine(InstallDirectory, "THIRD_PARTY_NOTICES.txt"));
        Extract("Payload.USER_GUIDE.md", Path.Combine(InstallDirectory, "USER_GUIDE.md"));
        if (!Path.GetFullPath(Environment.ProcessPath!).Equals(Path.GetFullPath(SetupPath), StringComparison.OrdinalIgnoreCase))
            File.Copy(Environment.ProcessPath!, SetupPath, true);

        progress("Registering hardware-enabled automatic startup…");
        RegisterElevatedStartup();
        RegisterUninstaller();
        CreateShortcuts();

        progress("Enabling signed hardware-sensor access…");
        InstallPawnIoIfNeeded();

        progress("Opening the private-network firewall port…");
        ConfigureFirewall();

        progress("Starting PC monitoring…");
        DeleteIfExists(ReadyPath);
        RequestAgentWindow();
        StartAgentFromTask();
        WaitForAgentReady();

        progress("Looking for the Galaxy J7…");
        var phone = ConfigurePhone();
        return new InstallResult(true, phone.Success, phone.Message);
    }

    private static (bool Success, string Message) ConfigurePhone()
    {
        var adb = Path.Combine(ToolsDirectory, "adb.exe");
        Run(adb, "start-server", 15_000);
        var devices = Run(adb, "devices", 10_000);
        var connected = devices.Output.Split('\n')
            .Any(line => line.TrimEnd().EndsWith("\tdevice", StringComparison.Ordinal));
        if (!connected)
            return (false, "Windows is ready, but no authorized Android phone was detected. Connect the unlocked J7, approve USB debugging, and run Setup again.");

        var apk = Path.Combine(InstallDirectory, "HenryMonitor.apk");
        var install = Run(adb, $"install -r \"{apk}\"", 90_000);
        if (install.ExitCode != 0 || !install.Output.Contains("Success", StringComparison.OrdinalIgnoreCase))
            return (false, "The phone was detected, but Android rejected the app installation: " + LastUsefulLine(install.Output));

        Run(adb, "shell settings put global stay_on_while_plugged_in 3", 10_000);
        Run(adb, "shell pm grant " + PackageName + " android.permission.READ_EXTERNAL_STORAGE", 10_000);
        Run(adb, "shell settings put system screen_off_timeout 2147483647", 10_000);
        Run(adb, "shell settings put global policy_control immersive.full=" + PackageName, 10_000);
        Run(adb, "shell cmd package set-home-activity " + PackageName + "/.MainActivity", 10_000);
        var kiosk = Run(adb, "shell dpm set-device-owner " + PackageName + "/.MonitorDeviceAdminReceiver", 15_000);
        Run(adb, "shell am start -n " + PackageName + "/.MainActivity", 10_000);
        var fullyManaged = kiosk.ExitCode == 0 && kiosk.Output.Contains("Success", StringComparison.OrdinalIgnoreCase);
        return (true, fullyManaged
            ? "The Galaxy J7 is configured in locked kiosk mode. Enter the pairing code shown on the PC."
            : "The Galaxy J7 is configured in fullscreen Home mode. Android did not permit locked kiosk mode because an account or prior setup remains; this can be enabled after a clean reset. Enter the pairing code shown on the PC.");
    }

    private static void Extract(string resourceName, string destination)
    {
        using var input = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("Setup payload is incomplete: " + resourceName);
        using var output = File.Create(destination);
        input.CopyTo(output);
    }

    private static void RegisterElevatedStartup()
    {
        // Hardware temperature/fan access is unreliable in a normal user
        // process on many PCs. A highest-privilege logon task avoids a UAC
        // prompt on every boot while keeping the app scoped to this user.
        var taskCommand = $"\"{AgentPath}\" --background";
        var arguments = $"/Create /TN \"{StartupTaskName}\" /TR \"{taskCommand.Replace("\"", "\\\"")}\" " +
                        "/SC ONLOGON /RL HIGHEST /F";
        var exitCode = RunElevated(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), arguments, 60_000);
        if (exitCode != 0)
            throw new InvalidOperationException(
                "Hardware monitoring needs the administrator prompt that creates its secure startup task. Run Setup again and approve that prompt.");

        // Remove the older normal-privilege startup entry when updating 1.0.0.
        using var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        run.DeleteValue("HenryMonitor", false);
    }

    private static void StartAgentFromTask()
    {
        var schtasks = Path.Combine(Environment.SystemDirectory, "schtasks.exe");
        var result = Run(schtasks, $"/Run /TN \"{StartupTaskName}\"", 15_000);
        if (result.ExitCode != 0)
            throw new InvalidOperationException("Windows could not start the hardware-monitoring task: " + LastUsefulLine(result.Output));
    }

    private static void RequestAgentWindow()
    {
        var temporary = ShowRequestPath + ".setup.tmp";
        File.WriteAllText(temporary, DateTimeOffset.UtcNow.ToString("O"));
        File.Move(temporary, ShowRequestPath, true);
    }

    private static void WaitForAgentReady()
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (File.Exists(ReadyPath)) return;
            Thread.Sleep(200);
        }
        throw new InvalidOperationException(
            "The Windows companion did not finish starting. Open %LOCALAPPDATA%\\HenryMonitor\\logs\\henry-monitor.log for the exact error, then run Setup again.");
    }

    private static void DeleteIfExists(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private static void RegisterUninstaller()
    {
        using var key = Registry.CurrentUser.CreateSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppId);
        key.SetValue("DisplayName", "Henry System Monitor");
        key.SetValue("DisplayVersion", "1.3.7");
        key.SetValue("Publisher", "Daniel Lowe");
        key.SetValue("InstallLocation", InstallDirectory);
        key.SetValue("DisplayIcon", AgentPath);
        key.SetValue("UninstallString", $"\"{SetupPath}\" /uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    private static void CreateShortcuts()
    {
        var desktopDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var startMenuDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "Henry System Monitor");
        Directory.CreateDirectory(startMenuDirectory);
        CreateShortcut(Path.Combine(desktopDirectory, "Henry System Monitor.lnk"), AgentPath);
        CreateShortcut(Path.Combine(desktopDirectory, "Henry Photos.lnk"), HenryPhotosPath);
        CreateShortcut(Path.Combine(startMenuDirectory, "Henry System Monitor.lnk"), AgentPath);
        CreateShortcut(Path.Combine(startMenuDirectory, "Henry Photos.lnk"), HenryPhotosPath);
        CreateShortcut(Path.Combine(startMenuDirectory, "Uninstall Henry System Monitor.lnk"), SetupPath, "/uninstall");
    }

    private static void CreateShortcut(string shortcutPath, string targetPath, string arguments = "")
    {
        var script = "$w=New-Object -ComObject WScript.Shell;" +
                     "$s=$w.CreateShortcut($args[0]);$s.TargetPath=$args[1];" +
                     "$s.Arguments=$args[2];$s.WorkingDirectory=$args[3];$s.Save()";
        var info = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-Command");
        info.ArgumentList.Add(script);
        info.ArgumentList.Add(shortcutPath);
        info.ArgumentList.Add(targetPath);
        info.ArgumentList.Add(arguments);
        info.ArgumentList.Add(InstallDirectory);
        using var process = Process.Start(info);
        process?.WaitForExit(15_000);
    }

    private static void ConfigureFirewall()
    {
        var script =
            "advfirewall firewall delete rule name=\"Henry System Monitor\" & " +
            "advfirewall firewall delete rule name=\"Henry Monitor API\" & " +
            "advfirewall firewall delete rule name=\"Henry Monitor Discovery\" & " +
            $"advfirewall firewall add rule name=\"Henry Monitor API\" dir=in action=allow program=\"{AgentPath}\" " +
            "protocol=TCP localport=47831 remoteip=localsubnet profile=any edge=no & " +
            $"advfirewall firewall add rule name=\"Henry Monitor Discovery\" dir=in action=allow program=\"{AgentPath}\" " +
            "protocol=UDP localport=47832 remoteip=localsubnet profile=any edge=no";
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"),
                Arguments = "/c " + script,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });
            process?.WaitForExit(30_000);
        }
        catch
        {
            // Windows will still prompt for network access when the agent first starts.
        }
    }

    private static void InstallPawnIoIfNeeded()
    {
        using var installed64 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64)
            .OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
        using var installed32 = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32)
            .OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO");
        if (installed64 is not null || installed32 is not null) return;
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(ToolsDirectory, "PawnIO_setup.exe"),
                Arguments = "-install -silent",
                UseShellExecute = true,
                Verb = "runas"
            });
            if (process is null || !process.WaitForExit(60_000) || process.ExitCode != 0)
                throw new InvalidOperationException("The signed PawnIO sensor driver was not installed.");
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Hardware-sensor access needs the administrator prompt for the signed PawnIO driver. " +
                "Run Setup again and approve that prompt.", ex);
        }
    }

    private static (int ExitCode, string Output) Run(string file, string arguments, int timeoutMs)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = file,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var process = Process.Start(info)!;
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(true); } catch { }
                return (-1, "Operation timed out.");
            }
            Task.WaitAll(new Task[] { outputTask, errorTask }, 5000);
            return (process.ExitCode, outputTask.Result + "\n" + errorTask.Result);
        }
        catch (Exception ex) { return (-1, ex.Message); }
    }

    private static int RunElevated(string file, string arguments, int timeoutMs)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = file,
                Arguments = arguments,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            });
            if (process is null || !process.WaitForExit(timeoutMs)) return -1;
            return process.ExitCode;
        }
        catch { return -1; }
    }

    private static string LastUsefulLine(string value) => value.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "Unknown error";

    private static void StopAgent()
    {
        foreach (var process in Process.GetProcessesByName("HenryMonitor"))
        {
            try { process.Kill(); process.WaitForExit(5000); } catch { }
        }
    }

    public static void UninstallInteractive()
    {
        if (MessageBox.Show("Remove Henry System Monitor from this PC?\n\nThe Android app will remain installed unless the phone is connected.",
                "Uninstall Henry System Monitor", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        StopAgent();
        using (var run = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
            run.DeleteValue("HenryMonitor", false);
        try
        {
            RunElevated(Path.Combine(Environment.SystemDirectory, "schtasks.exe"),
                $"/Delete /TN \"{StartupTaskName}\" /F", 30_000);
        }
        catch { }
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + AppId, false);

        var adb = Path.Combine(ToolsDirectory, "adb.exe");
        if (File.Exists(adb)) Run(adb, "uninstall " + PackageName, 30_000);
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "netsh.exe"),
                Arguments = "advfirewall firewall delete rule name=\"Henry System Monitor\"",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden
            })?.WaitForExit(30_000);
        }
        catch { }

        DeleteShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Henry System Monitor.lnk"));
        var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs", "Henry System Monitor");
        try { if (Directory.Exists(startMenu)) Directory.Delete(startMenu, true); } catch { }

        var cleanup = Path.Combine(Path.GetTempPath(), "HenryMonitorCleanup.cmd");
        File.WriteAllText(cleanup, "@echo off\r\ntimeout /t 2 /nobreak >nul\r\nrmdir /s /q \"" + InstallDirectory + "\"\r\ndel /q \"%~f0\"\r\n");
        Process.Start(new ProcessStartInfo(cleanup) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden });
        MessageBox.Show("Henry System Monitor has been removed.", "Uninstall complete", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private static void DeleteShortcut(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
