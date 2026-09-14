using System.Drawing.Drawing2D;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace HenryMonitor;

public sealed class MainForm : Form
{
    private readonly ConfigStore _config;
    private readonly PairingManager _pairing;
    private readonly HardwareMonitorService _monitor;
    private readonly Label _codeLabel;
    private readonly Label _pairingLabel;
    private readonly Label _telemetryLabel;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly System.Windows.Forms.Timer _activationTimer;
    private readonly NotifyIcon _tray;
    private UpdateService? _updates;
    private bool _reallyClose;

    public MainForm(ConfigStore config, PairingManager pairing, HardwareMonitorService monitor,
        bool startHidden)
    {
        _config = config;
        _pairing = pairing;
        _monitor = monitor;

        Text = "Henry System Monitor";
        ClientSize = new Size(700, 500);
        MinimumSize = new Size(650, 470);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(247, 248, 250);
        ForeColor = Color.FromArgb(24, 27, 31);
        Font = new Font("Segoe UI", 10f);
        Icon = BuildIcon();

        var header = new Label
        {
            Text = "HENRY  /  SYSTEM MONITOR",
            Font = new Font("Segoe UI Semibold", 19f),
            AutoSize = true,
            Location = new Point(36, 30)
        };
        var subtitle = new Label
        {
            Text = "Private PC telemetry and controls — available only on your local network",
            ForeColor = Color.FromArgb(98, 105, 116),
            AutoSize = true,
            Location = new Point(39, 69)
        };

        var card = new Panel
        {
            Location = new Point(36, 106),
            Size = new Size(628, 214),
            BackColor = Color.White
        };
        card.Paint += (_, e) =>
        {
            using var pen = new Pen(Color.FromArgb(226, 229, 234));
            e.Graphics.DrawRectangle(pen, 0, 0, card.Width - 1, card.Height - 1);
            using var brush = new LinearGradientBrush(new Rectangle(0, 0, card.Width, 4),
                Color.FromArgb(255, 73, 117), Color.FromArgb(87, 121, 255), 0f);
            e.Graphics.FillRectangle(brush, 0, 0, card.Width, 4);
        };

        var codeTitle = new Label
        {
            Text = "PAIRING CODE",
            Font = new Font("Segoe UI Semibold", 9f),
            ForeColor = Color.FromArgb(105, 111, 122),
            AutoSize = true,
            Location = new Point(28, 27)
        };
        _codeLabel = new Label
        {
            Text = FormatCode(_pairing.Code),
            Font = new Font("Consolas", 35f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(23, 48)
        };
        _pairingLabel = new Label
        {
            Text = PairingStatusText(),
            AutoSize = true,
            ForeColor = Color.FromArgb(65, 72, 82),
            Location = new Point(31, 116)
        };
        var instructions = new Label
        {
            Text = "Open Henry Monitor on the phone, select this PC, then enter the six-digit code.",
            AutoSize = true,
            ForeColor = Color.FromArgb(88, 94, 104),
            Location = new Point(31, 146)
        };
        var photosLink = new LinkLabel
        {
            Text = "Manage screensaver photos →",
            AutoSize = true,
            Location = new Point(31, 172),
            Font = new Font("Segoe UI Semibold", 9.5f),
            LinkColor = Color.FromArgb(87, 121, 255),
            ActiveLinkColor = Color.FromArgb(255, 73, 117),
            LinkBehavior = LinkBehavior.HoverUnderline,
            Cursor = Cursors.Hand
        };
        photosLink.LinkClicked += (_, _) => LaunchPhotosTool();

        var newCode = Button("NEW CODE", 480, 34, 116);
        newCode.Click += (_, _) => _pairing.Rotate();
        var reset = Button("RESET PAIRING", 454, 82, 142);
        reset.Click += (_, _) => ResetPairing();
        card.Controls.AddRange(new Control[] { codeTitle, _codeLabel, _pairingLabel, instructions, photosLink, newCode, reset });

        _telemetryLabel = new Label
        {
            AutoSize = false,
            Location = new Point(39, 344),
            Size = new Size(625, 48),
            ForeColor = Color.FromArgb(74, 80, 90)
        };

        var startup = new Label
        {
            Text = "✓  Starts automatically with Windows with hardware-sensor access",
            AutoSize = true,
            Location = new Point(39, 407),
            ForeColor = Color.FromArgb(65, 111, 87)
        };
        var minimize = Button("MINIMIZE TO TRAY", 495, 397, 169);
        minimize.Click += (_, _) => HideToTray();

        Controls.AddRange(new Control[] { header, subtitle, card, _telemetryLabel, startup, minimize });

        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Henry Monitor", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Check for updates", null, (_, _) => CheckForUpdates());
        menu.Items.Add("Exit", null, (_, _) => ExitApplication());
        _tray = new NotifyIcon
        {
            Text = "Henry System Monitor",
            Icon = Icon,
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => RestoreFromTray();

        _pairing.Changed += (_, _) => SafeRefreshPairing();
        _timer = new System.Windows.Forms.Timer { Interval = 1000 };
        _timer.Tick += (_, _) => RefreshTelemetry();
        _timer.Start();
        _activationTimer = new System.Windows.Forms.Timer { Interval = 150 };
        _activationTimer.Tick += (_, _) =>
        {
            if (AppRuntime.ConsumeShowRequest()) RestoreFromTray();
        };
        _activationTimer.Start();
        RefreshTelemetry();

        FormClosing += OnFormClosing;
        Shown += (_, _) => { if (startHidden) HideToTray(); };
    }

    public void AttachUpdateService(UpdateService updates) => _updates = updates;

    private void CheckForUpdates()
    {
        if (_updates is null) return;
        Task.Run(() =>
        {
            var message = _updates.CheckNow();
            if (IsDisposed) return;
            BeginInvoke(() => MessageBox.Show(this, message, "Henry System Monitor updates",
                MessageBoxButtons.OK, MessageBoxIcon.Information));
        });
    }

    /// <summary>Closes for an update swap; bypasses the minimize-to-tray guard.</summary>
    public void ForceCloseForUpdate()
    {
        _reallyClose = true;
        _tray.Visible = false;
        Close();
    }

    public void NotifyPaired() => BeginInvoke(SafeRefreshPairing);

    private void SafeRefreshPairing()
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(SafeRefreshPairing); return; }
        _codeLabel.Text = FormatCode(_pairing.Code);
        _pairingLabel.Text = PairingStatusText();
    }

    private void RefreshTelemetry()
    {
        var snapshot = _monitor.Latest;
        var ips = GetLocalAddresses();
        var cpu = snapshot.Cpu.TemperatureC.HasValue ? $"CPU {snapshot.Cpu.TemperatureC.Value * 9d / 5d + 32:0.#}°F" : "CPU warming up";
        var gpu = snapshot.Gpu.TemperatureC.HasValue ? $"GPU {snapshot.Gpu.TemperatureC.Value * 9d / 5d + 32:0.#}°F" : "GPU sensor unavailable";
        var noHardwareReadings = !snapshot.Cpu.TemperatureC.HasValue &&
                                 !snapshot.Cpu.LoadPercent.HasValue &&
                                 !snapshot.Gpu.TemperatureC.HasValue &&
                                 !snapshot.Gpu.LoadPercent.HasValue;
        // A CPU that reports load but never temperature is the signature of a
        // normal-privilege instance (or a failed sensor driver) — call it out
        // here so the fix does not depend on someone noticing the phone panel.
        var temperaturesMissing = !snapshot.Cpu.TemperatureC.HasValue &&
                                  snapshot.Cpu.LoadPercent.HasValue &&
                                  snapshot.Sequence > 12;
        _telemetryLabel.ForeColor = temperaturesMissing
            ? Color.FromArgb(184, 76, 45)
            : noHardwareReadings
                ? Color.FromArgb(184, 76, 45)
                : Color.FromArgb(74, 80, 90);
        _telemetryLabel.Text = noHardwareReadings
            ? "Hardware sensors are not responding. Re-run Setup and approve the administrator prompt.\n" +
              $"API online at {ips}:{_config.Current.Port}   •   Memory and network data remain available"
            : temperaturesMissing
            ? "No temperature sensors — this instance lacks hardware-sensor access. Close it and let the\n" +
              "Henry System Monitor startup task (administrator) serve the display, then re-run Setup if it persists.\n" +
              $"API online at {ips}:{_config.Current.Port}   •   Memory, load, and network data remain available"
            : $"{cpu}   •   {gpu}   •   API online at {ips}:{_config.Current.Port}\n" +
              $"Computer: {Environment.MachineName}   •   Data stays on this network";
    }

    private string PairingStatusText() => _config.Current.PairedAtUtc.HasValue
        ? $"Paired with {_config.Current.PairedDevice} • {_config.Current.PairedAtUtc.Value.ToLocalTime():g}"
        : "Waiting for Henry’s display";

    private void ResetPairing()
    {
        if (MessageBox.Show(
                "This disconnects the phone and creates a new private access key. Continue?",
                "Reset pairing", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        _config.ResetPairing();
        _pairing.Rotate();
        SafeRefreshPairing();
    }

    private void LaunchPhotosTool()
    {
        var photosExe = Path.Combine(AppContext.BaseDirectory, "HenryPhotos.exe");
        if (!File.Exists(photosExe))
        {
            MessageBox.Show(
                "Henry Photos was not found next to HenryMonitor.exe. Re-run Setup to install it.",
                "Henry Photos", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = photosExe,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(photosExe)!
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not start Henry Photos:\n{ex.Message}",
                "Henry Photos", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static Button Button(string text, int x, int y, int width) => new()
    {
        Text = text,
        FlatStyle = FlatStyle.Flat,
        BackColor = Color.FromArgb(28, 31, 36),
        ForeColor = Color.White,
        Font = new Font("Segoe UI Semibold", 8.5f),
        Location = new Point(x, y),
        Size = new Size(width, 36),
        Cursor = Cursors.Hand
    };

    private static string GetLocalAddresses()
    {
        try
        {
            return string.Join(" / ", Dns.GetHostAddresses(Dns.GetHostName())
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                .Select(a => a.ToString())
                .Distinct());
        }
        catch { return "local network"; }
    }

    private static string FormatCode(string code) => code.Length == 6 ? $"{code[..3]}  {code[3..]}" : code;

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_reallyClose) return;
        e.Cancel = true;
        HideToTray();
    }

    private void HideToTray()
    {
        Hide();
        ShowInTaskbar = false;
    }

    private void RestoreFromTray()
    {
        ShowInTaskbar = true;
        WindowState = FormWindowState.Normal;
        Show();
        BringToFront();
        Activate();
    }

    private void ExitApplication()
    {
        _reallyClose = true;
        _tray.Visible = false;
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _activationTimer.Dispose();
            _tray.Dispose();
        }
        base.Dispose(disposing);
    }

    private static Icon BuildIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.FromArgb(24, 27, 31));
        using var pen = new Pen(Color.FromArgb(100, 159, 255), 4f);
        graphics.DrawArc(pen, 6, 6, 20, 20, -80, 290);
        using var dot = new SolidBrush(Color.FromArgb(255, 92, 133));
        graphics.FillEllipse(dot, 13, 13, 6, 6);
        return Icon.FromHandle(bitmap.GetHicon());
    }
}
