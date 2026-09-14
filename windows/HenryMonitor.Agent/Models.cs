namespace HenryMonitor;

public sealed record ComponentMetric(
    string Name,
    double? TemperatureC = null,
    double? LoadPercent = null,
    double? PowerWatts = null,
    double? ClockMhz = null,
    double? UsedGb = null,
    double? TotalGb = null);

public sealed record NamedReading(string Name, double Value, string Unit, string Source);

public sealed record WeatherInfo(
    double TemperatureC,
    double FeelsLikeC,
    int HumidityPercent,
    double WindKmh,
    string Description,
    bool IsDay,
    string City);

public sealed record TelemetrySnapshot(
    long Sequence,
    DateTimeOffset TimestampUtc,
    string ComputerName,
    string OperatingSystem,
    ComponentMetric Cpu,
    ComponentMetric Gpu,
    ComponentMetric Memory,
    double? StorageUsedPercent,
    double DownloadMbps,
    double UploadMbps,
    double DiskReadMbS,
    double DiskWriteMbS,
    long UptimeSeconds,
    WeatherInfo? Weather,
    IReadOnlyList<NamedReading> Temperatures,
    IReadOnlyList<NamedReading> Fans);

public sealed record PairRequest(string Code, string DeviceName);
public sealed record PairResponse(string Token, string ComputerName, int Port, long ServerTimeUnix);
public sealed record ControlRequest(string Action, string? Confirmation);
public sealed record ApiError(string Error);

public sealed class AppConfig
{
    public string AccessToken { get; set; } = "";
    public int Port { get; set; } = 47831;
    public DateTimeOffset? PairedAtUtc { get; set; }
    public string? PairedDevice { get; set; }
}
