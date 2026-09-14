namespace HenryMonitor.Setup;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        if (args.Any(x => x.Equals("/uninstall", StringComparison.OrdinalIgnoreCase)))
        {
            Installer.UninstallInteractive();
            return;
        }

        Application.Run(new SetupForm());
    }
}
