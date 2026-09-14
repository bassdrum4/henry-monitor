using System.Security.Cryptography;
using System.Text.Json;

namespace HenryMonitor;

public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _path;

    public AppConfig Current { get; private set; }

    public ConfigStore(string? baseDirectory = null)
    {
        var directory = baseDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HenryMonitor");
        Directory.CreateDirectory(directory);
        _path = Path.Combine(directory, "config.json");
        Current = Load();

        if (string.IsNullOrWhiteSpace(Current.AccessToken))
        {
            Current.AccessToken = CreateToken();
            Save();
        }
    }

    public void MarkPaired(string deviceName)
    {
        Current.PairedAtUtc = DateTimeOffset.UtcNow;
        Current.PairedDevice = string.IsNullOrWhiteSpace(deviceName) ? "Henry display" : deviceName.Trim();
        Save();
    }

    public void ResetPairing()
    {
        Current.AccessToken = CreateToken();
        Current.PairedAtUtc = null;
        Current.PairedDevice = null;
        Save();
    }

    private AppConfig Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(_path)) ?? new AppConfig()
                : new AppConfig();
        }
        catch
        {
            return new AppConfig();
        }
    }

    private void Save()
    {
        var tempPath = _path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(Current, JsonOptions));
        File.Move(tempPath, _path, true);
    }

    private static string CreateToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}
