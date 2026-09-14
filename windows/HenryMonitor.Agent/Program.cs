namespace HenryMonitor;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Fatal(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception exception)
                AppRuntime.WriteLog("Unhandled background exception.", exception);
        };

        try
        {
            Run(args);
        }
        catch (Exception ex)
        {
            Fatal(ex);
        }
    }

    private static void Run(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var requestedBackground = args.Any(a =>
            a.Equals("--background", StringComparison.OrdinalIgnoreCase));

        // Desktop and Start-menu shortcuts run at normal privilege. Always ask
        // the installed elevated task to start or activate the monitor. The
        // file signal works in both directions across Windows UAC boundaries.
        if (!requestedBackground && !AppRuntime.IsAdministrator())
        {
            var signaled = AppRuntime.RequestShow();
            var taskStarted = signaled && AppRuntime.StartInstalledTask();
            if (taskStarted)
            {
                // The self-contained executable can take tens of seconds to
                // cold-start. Wait until the elevated instance actually owns
                // the instance lock before giving up on it: exiting early and
                // running unelevated here used to steal both the lock (so the
                // elevated process exited silently) and the window-open
                // request, which left the display with memory-only telemetry
                // and no temperatures.
                if (AppRuntime.WaitForElevatedInstance(TimeSpan.FromSeconds(45)))
                    return;
            }

            // The task may have been removed or cannot start. Continue at
            // normal privilege so the UI still opens and generic Windows
            // metrics remain usable.
            AppRuntime.ConsumeShowRequest();
        }

        using var instanceLock = AppRuntime.TryAcquireInstanceLock();
        if (instanceLock is null)
        {
            if (!requestedBackground) _ = AppRuntime.RequestShow();
            return;
        }

        var showRequested = AppRuntime.ConsumeShowRequest();
        var startHidden = requestedBackground && !showRequested;
        using var monitor = new HardwareMonitorService();
        var config = new ConfigStore();
        var pairing = new PairingManager();
        using var form = new MainForm(config, pairing, monitor, startHidden);
        var api = new ApiServer(monitor, config, pairing);
        api.DevicePaired += (_, _) => form.NotifyPaired();

        // Self-update: background sweep every few hours plus the tray-menu
        // command. When an update lands the service swaps the executable and
        // asks this process to exit so the startup task brings the new one up.
        var updates = new UpdateService(() => form.BeginInvoke(() => ExitApplication(form)));
        UpdateService.RecoverIfBroken();
        UpdateService.CleanStaging();
        updates.Start();
        form.AttachUpdateService(updates);

        api.StartAsync().GetAwaiter().GetResult();
        AppRuntime.MarkReady();
        try
        {
            Application.Run(form);
        }
        finally
        {
            AppRuntime.ClearReady();
            api.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static void ExitApplication(MainForm form)
    {
        AppRuntime.ClearReady();
        form.ForceCloseForUpdate();
    }

    private static void Fatal(Exception exception)
    {
        AppRuntime.ClearReady();
        AppRuntime.WriteLog("Henry System Monitor could not start.", exception);
        MessageBox.Show(
            "Henry System Monitor could not start.\n\n" + exception.Message +
            "\n\nA diagnostic log was saved here:\n" + AppRuntime.FriendlyLogPath,
            "Henry System Monitor", MessageBoxButtons.OK, MessageBoxIcon.Error);
        Application.Exit();
    }
}
