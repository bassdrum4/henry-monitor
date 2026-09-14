using System.Drawing.Imaging;

namespace HenryPhotos;

public sealed class MainForm : Form
{
    private readonly ListView grid;
    private readonly ImageList thumbnails;
    private readonly Button addBtn;
    private readonly Button deleteBtn;
    private readonly Button refreshBtn;
    private readonly Button cleanBtn;
    private readonly Label status;
    private string? adbPath;
    private bool busy;

    public MainForm()
    {
        Text = "Henry Photos — screensaver pictures on the phone";
        ClientSize = new Size(980, 640);
        MinimumSize = new Size(820, 560);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(247, 248, 250);
        Font = new Font("Segoe UI", 10f);

        var header = new Label
        {
            Text = "SCREENSAVER PHOTOS ON HENRY'S PHONE",
            Font = new Font("Segoe UI Semibold", 13f),
            ForeColor = Color.FromArgb(60, 66, 76),
            AutoSize = true,
            Location = new Point(24, 20)
        };

        status = new Label
        {
            Text = "Looking for the phone…",
            ForeColor = Color.FromArgb(110, 116, 126),
            AutoSize = true,
            Location = new Point(26, 52)
        };

        thumbnails = new ImageList
        {
            ImageSize = new Size(112, 84),
            ColorDepth = ColorDepth.Depth32Bit
        };

        grid = new ListView
        {
            View = View.LargeIcon,
            LargeImageList = thumbnails,
            BorderStyle = BorderStyle.None,
            BackColor = Color.White,
            Location = new Point(24, 84),
            Size = new Size(932, 470),
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            MultiSelect = true
        };

        addBtn = AccentButton("ADD PHOTOS…", true);
        addBtn.Location = new Point(560, 572);
        addBtn.Click += (_, _) => AddPhotos();

        deleteBtn = AccentButton("REMOVE SELECTED", false);
        deleteBtn.Location = new Point(742, 572);
        deleteBtn.Click += (_, _) => RemoveSelected();

        refreshBtn = AccentButton("REFRESH", false);
        refreshBtn.Location = new Point(24, 572);
        refreshBtn.Click += (_, _) => RefreshAsync();

        cleanBtn = AccentButton("CLEAN UP", false);
        cleanBtn.Location = new Point(204, 572);
        cleanBtn.Click += (_, _) => CleanUpAsync();

        Controls.AddRange(new Control[] { header, status, grid, addBtn, deleteBtn, refreshBtn, cleanBtn });

        Load += (_, _) => RefreshAsync();
    }

    private static Button AccentButton(string text, bool primary) => new()
    {
        Text = text,
        FlatStyle = FlatStyle.Flat,
        BackColor = primary ? Color.FromArgb(24, 27, 32) : Color.FromArgb(239, 241, 244),
        ForeColor = primary ? Color.White : Color.FromArgb(45, 50, 58),
        Font = new Font("Segoe UI Semibold", 9.5f),
        Size = new Size(170, 44),
        Cursor = Cursors.Hand
    };

    private void SetStatus(string text) =>
        Invoke(() => status.Text = text);

    private void SetBusy(bool value)
    {
        busy = value;
        addBtn.Enabled = !value;
        deleteBtn.Enabled = !value && grid.SelectedIndices.Count > 0;
        refreshBtn.Enabled = !value;
        cleanBtn.Enabled = !value;
    }

    /// <summary>
    /// One-click tidy: removes duplicate photos (identical content compared
    /// by MD5 hashed on the phone) and converts HEIC/HEIF files to JPEG so
    /// the phone can actually display them.
    /// </summary>
    private async void CleanUpAsync()
    {
        if (busy) return;
        SetBusy(true);
        try
        {
            if (!await Task.Run(Adb.PhoneConnected))
            {
                SetStatus("No phone detected. Connect it by USB and unlock it.");
                return;
            }

            // 1) HEIC → JPEG (the phone cannot decode HEIC).
            SetStatus("Looking for HEIC photos to convert…");
            int converted = await Task.Run(ConvertHeicFilesAsync);

            // 2) Duplicates: hash everything on the phone, keep the first
            //    name in each identical-content group.
            SetStatus("Comparing photos for duplicates…");
            var groups = await Task.Run(Adb.RemoteHashGroups);
            var duplicates = new List<string>();
            foreach (var group in groups.Values)
            {
                if (group.Count < 2) continue;
                foreach (string name in group.Skip(1)) duplicates.Add(name);
            }
            int removed = 0;
            if (duplicates.Count > 0)
            {
                string listing = string.Join(Environment.NewLine, duplicates.Take(12));
                if (duplicates.Count > 12) listing += Environment.NewLine + "…";
                var choice = MessageBox.Show(this,
                    $"{duplicates.Count} duplicate photo(s) found:\n\n{listing}\n\nRemove the duplicates? (One copy of each is kept.)",
                    "Remove duplicates", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (choice == DialogResult.Yes)
                {
                    SetStatus("Removing duplicates…");
                    foreach (string name in duplicates)
                    {
                        string local = name;
                        await Task.Run(() => Adb.DeleteRemote(local));
                        removed++;
                    }
                }
            }

            SetStatus($"Clean-up done — {converted} HEIC converted, {removed} duplicate(s) removed.");
            RefreshAsync();
        }
        catch (Exception ex)
        {
            SetStatus("Error: " + ex.Message);
            SetBusy(false);
        }
    }

    /// <summary>Converts every HEIC/HEIF on the phone to a .jpg sibling.</summary>
    private static async Task<int> ConvertHeicFilesAsync()
    {
        var heicFiles = await Task.Run(() =>
            Adb.ListAllRemoteImages().Where(IsHeic).ToList());
        int converted = 0;
        foreach (string name in heicFiles)
        {
            string jpgName = Path.ChangeExtension(name, ".jpg");
            // If an identically named JPEG already exists, the conversion
            // was done before — just drop the HEIC original.
            var existing = await Task.Run(() => Adb.ListRemotePhotos());
            if (existing.Contains(jpgName, StringComparer.OrdinalIgnoreCase))
            {
                await Task.Run(() => Adb.DeleteRemote(name));
                converted++;
                continue;
            }
            string? local = await Task.Run(() => Adb.PullToTemp(name));
            if (local == null) continue;
            try
            {
                string convertedPath = Path.Combine(Path.GetTempPath(),
                    "HenryPhotos-" + Guid.NewGuid().ToString("N") + ".jpg");
                try
                {
                    bool ok;
                    using (var image = TryGdiPlus(local))
                    {
                        if (image != null)
                        {
                            using var resized = Resize(image);
                            resized.Save(convertedPath, ImageFormat.Jpeg);
                            ok = true;
                        }
                        else
                        {
                            ok = ConvertWithWindowsCodec(local, convertedPath);
                        }
                    }
                    if (!ok) continue;
                    await Task.Run(() => Adb.Push(convertedPath, jpgName));
                    await Task.Run(() => Adb.DeleteRemote(name));
                    converted++;
                }
                finally
                {
                    try { File.Delete(convertedPath); } catch { }
                }
            }
            finally
            {
                try { File.Delete(local); } catch { }
            }
        }
        return converted;
    }

    private static bool IsHeic(string name) =>
        Adb.HeicExtensions.Contains(Path.GetExtension(name).ToLowerInvariant());

    private async void RefreshAsync()
    {
        if (busy) return;
        SetBusy(true);
        try
        {
            adbPath ??= Adb.EnsureAdb();
            if (adbPath == null)
            {
                SetStatus("Could not set up adb.exe on this PC.");
                return;
            }
            if (!await Task.Run(Adb.PhoneConnected))
            {
                SetStatus("No phone detected. Connect it by USB and unlock it.");
                grid.Items.Clear();
                thumbnails.Images.Clear();
                return;
            }
            SetStatus("Loading photos from the phone…");
            var names = await Task.Run(Adb.ListRemotePhotos);
            await PopulateThumbnailsAsync(names);
            SetStatus($"{names.Count} photos on the phone  •  connect more anytime, changes appear on the screensaver within ~20 seconds");
        }
        catch (Exception ex)
        {
            SetStatus("Error: " + ex.Message);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async Task PopulateThumbnailsAsync(List<string> names)
    {
        grid.BeginUpdate();
        thumbnails.Images.Clear();
        grid.Items.Clear();
        foreach (var name in names)
        {
            string? local = await Task.Run(() => Adb.PullToTemp(name));
            if (local == null) continue;
            try
            {
                using var full = new Bitmap(local);
                var thumb = new Bitmap(full, new Size(112, 84));
                thumbnails.Images.Add(name, thumb);
                grid.Items.Add(name, name);
            }
            catch { /* skip images the PC cannot decode */ }
            finally
            {
                try { File.Delete(local); } catch { }
            }
        }
        grid.EndUpdate();
    }

    private async void AddPhotos()
    {
        if (busy) return;
        using var dialog = new OpenFileDialog
        {
            Title = "Choose pictures for the screensaver",
            Filter = "Pictures|*.jpg;*.jpeg;*.png;*.webp;*.gif;*.heic;*.heif|All files|*.*",
            Multiselect = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        SetBusy(true);
        int added = 0;
        try
        {
            SetStatus("Checking what's already on the phone…");
            var remoteGroups = await Task.Run(Adb.RemoteHashGroups);
            var knownDigests = remoteGroups.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var localNames = remoteGroups.Values.SelectMany(v => v).ToHashSet(StringComparer.OrdinalIgnoreCase);

            SetStatus("Copying to the phone…");
            int skipped = 0;
            foreach (var file in dialog.FileNames)
            {
                // Skip files whose exact content is already on the phone.
                if (knownDigests.Contains(Digest(file))) { skipped++; continue; }
                string target = await Task.Run(() => CopyCompatibleAsync(file));
                if (target != null)
                {
                    added++;
                    if (localNames.Contains(target)) skipped++; // same-name different-file case
                }
            }
            SetStatus(skipped > 0
                ? $"{added} photo(s) added — {skipped} duplicate(s) skipped."
                : $"{added} photo(s) added.");
            await Task.Run(Adb.ListRemotePhotos); // give the media scanner a beat
            RefreshAsync();
        }
        catch (Exception ex)
        {
            SetStatus("Error: " + ex.Message);
            SetBusy(false);
        }
    }

    /// <summary>
    /// Converts HEIC/HEIF and other formats the phone cannot display into
    /// JPEG before copying, so every added photo actually shows up.
    /// </summary>
    private static string? CopyCompatibleAsync(string file)
    {
        string ext = Path.GetExtension(file).ToLowerInvariant();
        bool phoneFriendly = ext is ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif";
        if (phoneFriendly && new FileInfo(file).Length < 12_000_000)
        {
            Adb.Push(file, Path.GetFileName(file));
            return file;
        }

        // Anything else (HEIC, huge files) gets re-encoded as JPEG. First
        // try GDI+, then Windows' built-in HEIF codec via PowerShell.
        string converted = Path.Combine(Path.GetTempPath(), "HenryPhotos-" + Guid.NewGuid().ToString("N") + ".jpg");
        try
        {
            Image? decoded = TryGdiPlus(file);
            if (decoded == null && !ConvertWithWindowsCodec(file, converted))
                return null;
            if (decoded != null)
            {
                using (decoded)
                using (var resized = Resize(decoded))
                    resized.Save(converted, ImageFormat.Jpeg);
            }
            string remoteName = Path.ChangeExtension(Path.GetFileName(file), ".jpg");
            Adb.Push(converted, remoteName);
            return remoteName;
        }
        catch
        {
            return null;
        }
        finally
        {
            try { File.Delete(converted); } catch { }
        }
    }

    private static Image? TryGdiPlus(string file)
    {
        try { return System.Drawing.Image.FromFile(file); }
        catch { return null; }
    }

    /// <summary>MD5 of a local file, for duplicate detection before copying.</summary>
    private static string Digest(string file)
    {
        using var stream = File.OpenRead(file);
        using var md5 = System.Security.Cryptography.MD5.Create();
        return Convert.ToHexString(md5.ComputeHash(stream));
    }

    private static Bitmap Resize(Image source)
    {
        double scale = Math.Min(1.0, 2400.0 / Math.Max(source.Width, source.Height));
        return new Bitmap(source, Math.Max(1, (int)(source.Width * scale)), Math.Max(1, (int)(source.Height * scale)));
    }

    /// <summary>Uses Windows' built-in HEIF/HEIC codec (WIC) through PowerShell.</summary>
    private static bool ConvertWithWindowsCodec(string source, string target)
    {
        if (!File.Exists(target)) File.Create(target).Dispose();
        string script = Path.Combine(Path.GetTempPath(), "HenryPhotos-convert.ps1");
        File.WriteAllText(script,
            "param($src, $dst)\n" +
            "Add-Type -AssemblyName System.Runtime.WindowsRuntime\n" +
            "[Windows.Graphics.Imaging.BitmapDecoder,Windows.Graphics.Imaging,ContentType=WindowsRuntime] | Out-Null\n" +
            "[Windows.Graphics.Imaging.BitmapEncoder,Windows.Graphics.Imaging,ContentType=WindowsRuntime] | Out-Null\n" +
            "[Windows.Storage.StorageFile,Windows.Storage,ContentType=WindowsRuntime] | Out-Null\n" +
            "function Await($winrtTask, $resultType) { $asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]; $netTask = $asTask.MakeGenericMethod($resultType).Invoke($null, @($winrtTask)); $netTask.Wait(20000) | Out-Null; $netTask.Result }\n" +
            "$inFile = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($src)) ([Windows.Storage.StorageFile])\n" +
            "$inStream = Await ($inFile.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])\n" +
            "$decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($inStream)) ([Windows.Graphics.Imaging.BitmapDecoder])\n" +
            "$pixelData = Await ($decoder.GetPixelDataAsync()) ([Windows.Graphics.Imaging.PixelDataProvider])\n" +
            "$outFile = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($dst)) ([Windows.Storage.StorageFile])\n" +
            "$outStream = Await ($outFile.OpenAsync([Windows.Storage.FileAccessMode]::ReadWrite)) ([Windows.Storage.Streams.IRandomAccessStream])\n" +
            "$encoder = Await ([Windows.Graphics.Imaging.BitmapEncoder]::CreateAsync([Windows.Graphics.Imaging.BitmapEncoder.JpegEncoderId], $outStream)) ([Windows.Graphics.Imaging.BitmapEncoder])\n" +
            "$encoder.SetPixelData([Windows.Graphics.Imaging.BitmapPixelFormat]::Rgba8, [Windows.Graphics.Imaging.BitmapAlphaMode]::Ignore, $decoder.OrientedPixelWidth, $decoder.OrientedPixelHeight, 96.0, 96.0, $pixelData.DetachPixelData())\n" +
            "Await ($encoder.FlushAsync()) ([Object])\n" +
            "$outStream.Dispose()\n$inStream.Dispose()\n");
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\" -src \"{source}\" -dst \"{target}\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = System.Diagnostics.Process.Start(psi);
            process!.WaitForExit(30_000);
            return process.ExitCode == 0 && new FileInfo(target).Length > 0;
        }
        catch { return false; }
        finally { try { File.Delete(script); } catch { } }
    }

    private async void RemoveSelected()
    {
        if (busy || grid.SelectedIndices.Count == 0) return;
        var names = grid.SelectedItems.Cast<ListViewItem>().Select(item => item.Text).ToList();
        if (MessageBox.Show(this, $"Remove {names.Count} photo(s) from the phone?",
                "Remove photos", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return;
        SetBusy(true);
        try
        {
            SetStatus("Removing from the phone…");
            foreach (var name in names)
                await Task.Run(() => Adb.DeleteRemote(name));
            SetStatus($"{names.Count} photo(s) removed.");
            RefreshAsync();
        }
        catch (Exception ex)
        {
            SetStatus("Error: " + ex.Message);
            SetBusy(false);
        }
    }
}
