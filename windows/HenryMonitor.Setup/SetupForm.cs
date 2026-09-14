using System.Drawing.Drawing2D;

namespace HenryMonitor.Setup;

public sealed class SetupForm : Form
{
    private readonly Label _title;
    private readonly Label _body;
    private readonly Label _status;
    private readonly ProgressBar _progress;
    private readonly Button _primary;
    private readonly Button _secondary;
    private int _page;
    private bool _busy;

    public SetupForm()
    {
        Text = "Henry System Monitor Setup";
        ClientSize = new Size(760, 540);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(247, 248, 250);
        Font = new Font("Segoe UI", 10f);

        var accent = new Panel { Dock = DockStyle.Top, Height = 6 };
        accent.Paint += (_, e) =>
        {
            using var brush = new LinearGradientBrush(accent.ClientRectangle,
                Color.FromArgb(244, 82, 119), Color.FromArgb(78, 127, 246), 0f);
            e.Graphics.FillRectangle(brush, accent.ClientRectangle);
        };
        var brand = new Label
        {
            Text = "HENRY  /  SYSTEM MONITOR",
            Font = new Font("Segoe UI Semibold", 12f),
            ForeColor = Color.FromArgb(80, 86, 96),
            AutoSize = true,
            Location = new Point(54, 43)
        };
        _title = new Label
        {
            Font = new Font("Segoe UI Semibold", 27f),
            ForeColor = Color.FromArgb(23, 26, 32),
            AutoSize = false,
            Location = new Point(51, 92),
            Size = new Size(655, 58)
        };
        _body = new Label
        {
            Font = new Font("Segoe UI", 12f),
            ForeColor = Color.FromArgb(86, 92, 102),
            AutoSize = false,
            Location = new Point(55, 169),
            Size = new Size(650, 190)
        };
        _status = new Label
        {
            Font = new Font("Segoe UI Semibold", 10f),
            ForeColor = Color.FromArgb(67, 74, 84),
            AutoSize = false,
            Location = new Point(55, 365),
            Size = new Size(650, 45),
            Visible = false
        };
        _progress = new ProgressBar
        {
            Style = ProgressBarStyle.Marquee,
            MarqueeAnimationSpeed = 28,
            Location = new Point(56, 414),
            Size = new Size(648, 6),
            Visible = false
        };
        _primary = StyledButton("CONTINUE", true);
        _primary.Location = new Point(544, 458);
        _primary.Click += PrimaryClick;
        _secondary = StyledButton("CANCEL", false);
        _secondary.Location = new Point(378, 458);
        _secondary.Click += SecondaryClose;

        Controls.AddRange(new Control[] { accent, brand, _title, _body, _status, _progress, _primary, _secondary });
        ShowPage(0);
    }

    private void ShowPage(int page)
    {
        _page = page;
        if (page == 0)
        {
            _title.Text = "Turn a phone into a PC instrument panel.";
            _body.Text = "Setup installs the private Windows monitoring companion and configures the Galaxy J7 as Henry’s dedicated display.\n\n" +
                         "Before continuing:\n" +
                         "  • Connect both devices to the same private Wi-Fi network.\n" +
                         "  • Connect the unlocked phone by USB.\n" +
                         "  • Approve USB debugging and select ‘Always allow.’";
            _primary.Text = "CONTINUE";
            _secondary.Text = "CANCEL";
        }
        else if (page == 1)
        {
            _title.Text = "Ready to install.";
            _body.Text = "The companion will start automatically with Windows and listen only on the private network. The phone receives live hardware data and can send authenticated PC controls.\n\n" +
                         "Windows may show one administrator prompt to add a private-network firewall rule. This does not expose the service to the internet.";
            _primary.Text = "INSTALL";
            _secondary.Text = "BACK";
            _secondary.Click -= SecondaryClose;
            _secondary.Click += SecondaryBack;
        }
    }

    private void PrimaryClick(object? sender, EventArgs e)
    {
        if (_busy) return;
        if (_page == 0) { ShowPage(1); return; }
        if (_page == 1) BeginInstall();
        else Close();
    }

    private void BeginInstall()
    {
        _busy = true;
        _primary.Enabled = false;
        _secondary.Enabled = false;
        _status.Visible = true;
        _progress.Visible = true;
        _status.Text = "Preparing installation…";
        Task.Run(() =>
        {
            try
            {
                var result = Installer.Install(UpdateStatus);
                BeginInvoke(() => Complete(result));
            }
            catch (Exception ex)
            {
                BeginInvoke(() => Failed(ex));
            }
        });
    }

    private void UpdateStatus(string value)
    {
        if (IsDisposed) return;
        BeginInvoke(() => _status.Text = value);
    }

    private void Complete(InstallResult result)
    {
        _busy = false;
        _page = 2;
        _progress.Visible = false;
        _title.Text = result.PhoneInstalled ? "Henry Monitor is ready." : "Windows is ready. Connect the phone.";
        _body.Text = result.PhoneInstalled
            ? "The Windows companion is running and the Galaxy J7 has opened Henry Monitor.\n\n" +
              result.PhoneMessage + "\n\nAfter pairing, unplug the USB cable if desired; both devices communicate over local Wi-Fi."
            : result.PhoneMessage + "\n\nYou can run this installer again after the phone is connected. Existing Windows settings will be preserved.";
        _status.Text = result.PhoneInstalled ? "PC companion installed  •  Phone configured  •  Private network enabled" : "PC companion installed  •  Phone setup incomplete";
        _status.Visible = true;
        _primary.Text = "FINISH";
        _primary.Enabled = true;
        _secondary.Visible = false;
    }

    private void Failed(Exception ex)
    {
        _busy = false;
        _progress.Visible = false;
        _title.Text = "Setup couldn’t finish.";
        _body.Text = "No account protections were changed. The error was:\n\n" + ex.Message +
                     "\n\nClose other copies of Henry Monitor, confirm the phone is unlocked, and run Setup again.";
        _status.Visible = false;
        _primary.Text = "CLOSE";
        _primary.Enabled = true;
        _page = 2;
        _secondary.Visible = false;
    }

    private static Button StyledButton(string text, bool primary) => new()
    {
        Text = text,
        FlatStyle = FlatStyle.Flat,
        BackColor = primary ? Color.FromArgb(24, 27, 32) : Color.FromArgb(239, 241, 244),
        ForeColor = primary ? Color.White : Color.FromArgb(45, 50, 58),
        Font = new Font("Segoe UI Semibold", 9f),
        Size = new Size(160, 46),
        Cursor = Cursors.Hand
    };

    private void SecondaryClose(object? sender, EventArgs e) => Close();
    private void SecondaryBack(object? sender, EventArgs e)
    {
        _secondary.Click -= SecondaryBack;
        _secondary.Click += SecondaryClose;
        ShowPage(0);
    }
}
