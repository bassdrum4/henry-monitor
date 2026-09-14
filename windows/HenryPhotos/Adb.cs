using System.Diagnostics;

namespace HenryPhotos;

/// <summary>
/// Thin wrapper around the adb binary bundled with Henry Monitor. All phone
/// file operations go through adb so nothing extra has to be installed.
/// </summary>
internal static class Adb
{
    public static readonly string[] CandidatePaths =
    {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HenryMonitor", "tools", "adb.exe"),
        BundledToolsDirectory(),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk", "platform-tools", "adb.exe"),
        "adb.exe"
    };

    /// <summary>Directory where the exe unpacks its bundled adb on first run.</summary>
    private static string BundledToolsDirectory()
    {
        string dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HenryPhotos");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, "adb.exe");
    }

    public static string? ResolvePath()
    {
        foreach (var candidate in CandidatePaths)
        {
            try
            {
                if (candidate == "adb.exe" || File.Exists(candidate))
                {
                    var probe = Run("--version", 10_000, candidate);
                    if (probe.exitCode == 0) return candidate;
                }
            }
            catch { }
        }
        return null;
    }

    private static string? cachedPath;

    /// <summary>
    /// Ensures adb exists: prefers an existing install, otherwise extracts
    /// the copy bundled inside this executable. Returns null on failure.
    /// </summary>
    public static string? EnsureAdb()
    {
        if (cachedPath != null) return cachedPath;
        string? existing = ResolvePath();
        if (existing != null)
        {
            cachedPath = existing;
            return existing;
        }

        try
        {
            string target = BundledToolsDirectory();
            ExtractResource("HenryPhotos.adb.exe", target);
            ExtractResource("HenryPhotos.AdbWinApi.dll", Path.Combine(Path.GetDirectoryName(target)!, "AdbWinApi.dll"));
            ExtractResource("HenryPhotos.AdbWinUsbApi.dll", Path.Combine(Path.GetDirectoryName(target)!, "AdbWinUsbApi.dll"));
            cachedPath = target;
            return target;
        }
        catch
        {
            return null;
        }
    }

    private static void ExtractResource(string resourceName, string targetPath)
    {
        var assembly = typeof(Adb).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("Missing embedded resource: " + resourceName);
        using var output = File.Create(targetPath);
        stream.CopyTo(output);
    }

    public static string RemoteRoot => "/sdcard/Download/Photos";

    public static (int exitCode, string output) Run(string arguments, int timeoutMs, string? adbPath = null)
    {
        var start = new ProcessStartInfo
        {
            FileName = adbPath ?? ResolveRequired(),
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8
        };
        using var process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(timeoutMs);
        return (process.ExitCode, output.Trim());
    }

    public static string ResolveRequired() =>
        EnsureAdb() ?? throw new InvalidOperationException(
            "adb.exe could not be set up on this PC.");

    public static bool PhoneConnected()
    {
        var (code, output) = Run("devices", 10_000);
        if (code != 0) return false;
        foreach (var line in output.Split('\n'))
        {
            var parts = line.Split('\t');
            if (parts.Length == 2 && parts[1].Trim() == "device") return true;
        }
        return false;
    }

    public static readonly string[] PhoneFriendlyExtensions = { ".jpg", ".jpeg", ".png", ".webp", ".gif" };
    public static readonly string[] HeicExtensions = { ".heic", ".heif" };

    public static List<string> ListRemotePhotos()
    {
        var (_, output) = Run($"shell ls {RemoteRoot}", 15_000);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().Replace("\r", ""))
            .Where(name => PhoneFriendlyExtensions.Any(name.ToLowerInvariant().EndsWith))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Everything in the folder, including HEIC files the phone skips.</summary>
    public static List<string> ListAllRemoteImages()
    {
        var (_, output) = Run($"shell ls {RemoteRoot}", 15_000);
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().Replace("\r", ""))
            .Where(name => PhoneFriendlyExtensions.Concat(HeicExtensions).Any(name.ToLowerInvariant().EndsWith))
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// MD5 of every photo file, hashed on the phone itself — one adb call,
    /// nothing is downloaded. Returns digest → file names with that content.
    /// </summary>
    public static Dictionary<string, List<string>> RemoteHashGroups()
    {
        var groups = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var (_, output) = Run($"shell md5sum {RemoteRoot}/*", 120_000);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            // "<32 hex chars>  /sdcard/Download/Photos/<name>"
            int split = line.IndexOf("  ", StringComparison.Ordinal);
            if (split != 32) continue;
            string digest = line.Substring(0, split);
            string path = line.Substring(split + 2).Trim().Replace("\r", "");
            string name = path.Substring(path.LastIndexOf('/') + 1);
            string lower = name.ToLowerInvariant();
            if (!PhoneFriendlyExtensions.Concat(HeicExtensions).Any(lower.EndsWith)) continue;
            if (!groups.TryGetValue(digest, out var list)) groups[digest] = list = new List<string>();
            list.Add(name);
        }
        return groups;
    }

    public static void Push(string localFile, string remoteName) =>
        Run($"push \"{localFile}\" \"{RemoteRoot}/{remoteName}\"", 120_000);

    public static void DeleteRemote(string remoteName) =>
        Run($"shell rm \"{RemoteRoot}/{remoteName}\"", 15_000);

    /// <summary>Downloads a photo to a temp file and returns the path, or null.</summary>
    public static string? PullToTemp(string remoteName)
    {
        string local = Path.Combine(Path.GetTempPath(), "HenryPhotos-" + Guid.NewGuid().ToString("N") + Path.GetExtension(remoteName));
        var (code, _) = Run($"pull \"{RemoteRoot}/{remoteName}\" \"{local}\"", 60_000);
        return code == 0 && File.Exists(local) ? local : null;
    }
}
