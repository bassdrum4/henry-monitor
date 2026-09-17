using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using LibreHardwareMonitor.Hardware;

namespace HenryMonitor;

public sealed class HardwareMonitorService : IDisposable
{
    private readonly Computer? _computer;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _samplingTask;
    private TelemetrySnapshot _latest;
    private string? _lastSensorError;
    private long _sequence;
    private long _previousRx;
    private long _previousTx;
    private DateTimeOffset _previousNetworkAt = DateTimeOffset.UtcNow;
    private PerformanceCounter? _diskReadCounter;
    private PerformanceCounter? _diskWriteCounter;
    private readonly WeatherService _weather = new();

    public HardwareMonitorService()
    {
        try
        {
            var computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsMotherboardEnabled = true,
                IsControllerEnabled = true,
                IsNetworkEnabled = true,
                IsStorageEnabled = true
            };
            computer.Open();
            _computer = computer;
        }
        catch (Exception ex)
        {
            // A driver or unsupported sensor must not take down the desktop
            // companion. Generic Windows metrics continue to work.
            _computer = null;
            _lastSensorError = ex.Message;
            AppRuntime.WriteLog("Hardware sensors could not initialize; using Windows metrics only.", ex);
        }
        (_previousRx, _previousTx) = ReadNetworkTotals();
        // Prime the disk counters so the first real sample already has data.
        SampleDiskRates();
        _latest = EmptySnapshot();
        _samplingTask = Task.Run(SamplingLoop);
    }

    public TelemetrySnapshot Latest
    {
        get { lock (_gate) return _latest; }
    }

    public string? LastSensorError
    {
        get { lock (_gate) return _lastSensorError; }
    }

    private async Task SamplingLoop()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                Sample();
                await timer.WaitForNextTickAsync(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // A single unsupported sensor must never stop the whole monitor.
                lock (_gate) _lastSensorError = ex.Message;
                await Task.Delay(1000, _stop.Token).ConfigureAwait(false);
            }
        }
    }

    private void Sample()
    {
        var allHardware = new List<IHardware>();
        foreach (var hardware in _computer?.Hardware ?? Array.Empty<IHardware>())
        {
            try { VisitHardware(hardware, allHardware); }
            catch (Exception ex)
            {
                // Keep readings from supported devices if one motherboard or
                // controller sensor is unavailable on this specific PC.
                lock (_gate) _lastSensorError = hardware.Name + ": " + ex.Message;
            }
        }

        var cpuHardware = allHardware.Where(h => h.HardwareType == HardwareType.Cpu).ToList();
        // Machines with an integrated GPU plus a discrete card expose two GPU
        // devices. Picking sensors by name across all of them let readings
        // flicker between the cards — e.g. the dGPU's "GPU Core" load one
        // second, the idle iGPU's the next — which read as nonsense on the
        // dial. Lock every GPU metric to one primary card instead.
        var gpuHardware = allHardware.Where(IsGpu).ToList();
        var primaryGpu = PrimaryGpu(gpuHardware);
        var gpuOwners = primaryGpu != null ? new[] { primaryGpu } : Array.Empty<IHardware>();
        var readings = allHardware.SelectMany(h => h.Sensors.Select(s => (Hardware: h, Sensor: s)))
            .Where(x => x.Sensor.Value.HasValue && !float.IsNaN(x.Sensor.Value.Value))
            .ToList();

        var cpu = new ComponentMetric(
            cpuHardware.FirstOrDefault()?.Name ?? "CPU",
            // "Distance to TjMax" sensors are headroom deltas, not degrees, so
            // they must never win the max — on some boards they read hotter
            // than the package sensor and would inflate the displayed CPU temp.
            MaxSensor(readings, cpuHardware, SensorType.Temperature),
            PreferredSensor(readings, cpuHardware, SensorType.Load, "CPU Total"),
            PreferredSensor(readings, cpuHardware, SensorType.Power, "Package"),
            AverageSensor(readings, cpuHardware, SensorType.Clock));

        var gpu = new ComponentMetric(
            primaryGpu?.Name ?? "GPU",
            PreferredSensor(readings, gpuOwners, SensorType.Temperature, "GPU Core")
                ?? MaxSensor(readings, gpuOwners, SensorType.Temperature),
            PreferredSensor(readings, gpuOwners, SensorType.Load, "GPU Core")
                ?? MaxSensor(readings, gpuOwners, SensorType.Load),
            PreferredSensor(readings, gpuOwners, SensorType.Power, "GPU Package")
                ?? MaxSensor(readings, gpuOwners, SensorType.Power),
            PreferredSensor(readings, gpuOwners, SensorType.Clock, "GPU Core"));

        var memoryStatus = ReadMemoryStatus();
        // The dial must agree with the "used / total GB" text, so derive load
        // from the same physical-memory numbers instead of the motherboard's
        // Memory load sensor. On some boards that sensor reports a different
        // basis (or goes stale), which is what made the dial read nearly full
        // while the text correctly showed roughly 13 of 32 GB used.
        var memory = new ComponentMetric(
            "System memory",
            LoadPercent: memoryStatus.LoadPercent,
            UsedGb: memoryStatus.UsedGb,
            TotalGb: memoryStatus.TotalGb);

        var temperatures = readings
            .Where(x => x.Sensor.SensorType == SensorType.Temperature)
            // Headroom deltas are not degrees — listing them as temperature
            // rows would push real sensors off the twelve-row panel.
            .Where(x => !x.Sensor.Name.Contains("Distance to TjMax", StringComparison.OrdinalIgnoreCase))
            .Select(x => new NamedReading(
                FriendlySensorName(x.Sensor.Name, x.Hardware.HardwareType, false),
                Math.Round(x.Sensor.Value!.Value, 1), "°C",
                FriendlySourceName(x.Hardware.HardwareType)))
            .Where(x => x.Value is > -20 and < 150)
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(x => x.Value).First())
            .OrderByDescending(x => x.Value)
            .Take(12)
            .ToList();

        var fans = readings
            .Where(x => x.Sensor.SensorType == SensorType.Fan)
            .Select(x => new NamedReading(
                FriendlySensorName(x.Sensor.Name, x.Hardware.HardwareType, true),
                Math.Round(x.Sensor.Value!.Value), "RPM",
                FriendlySourceName(x.Hardware.HardwareType)))
            .Where(x => x.Value >= 0)
            .GroupBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(x => x.Value).First())
            .OrderByDescending(x => x.Value)
            .Take(10)
            .ToList();

        var (downloadMbps, uploadMbps) = SampleNetworkRates();
        var (diskReadMbS, diskWriteMbS) = SampleDiskRates();
        var snapshot = new TelemetrySnapshot(
            Interlocked.Increment(ref _sequence),
            DateTimeOffset.UtcNow,
            Environment.MachineName,
            RuntimeInformation.OSDescription,
            cpu,
            gpu,
            memory,
            ReadStorageUsedPercent(),
            downloadMbps,
            uploadMbps,
            diskReadMbS,
            diskWriteMbS,
            Environment.TickCount64 / 1000,
            _weather.Latest,
            temperatures,
            fans);

        lock (_gate) _latest = snapshot;
    }

    private static void VisitHardware(IHardware hardware, ICollection<IHardware> output)
    {
        hardware.Update();
        output.Add(hardware);
        foreach (var subHardware in hardware.SubHardware)
            VisitHardware(subHardware, output);
    }

    private static bool IsGpu(IHardware hardware) => hardware.HardwareType is
        HardwareType.GpuAmd or HardwareType.GpuIntel or HardwareType.GpuNvidia;

    /// <summary>
    /// Picks the GPU the dashboard reports when a machine exposes more than
    /// one (an integrated GPU beside a discrete card). Intel hardware is
    /// always integrated, NVIDIA is always discrete, and an AMD entry is
    /// preferred last so an APU's idle iGPU never shadows a Radeon card.
    /// </summary>
    private static IHardware? PrimaryGpu(IReadOnlyList<IHardware> gpus)
    {
        if (gpus.Count <= 1) return gpus.FirstOrDefault();
        return gpus.FirstOrDefault(h => h.HardwareType == HardwareType.GpuNvidia)
            ?? gpus.FirstOrDefault(h => h.HardwareType == HardwareType.GpuAmd)
            ?? gpus[0];
    }

    private static double? PreferredSensor(
        IEnumerable<(IHardware Hardware, ISensor Sensor)> readings,
        IReadOnlyCollection<IHardware> owners,
        SensorType type,
        string name)
    {
        return readings
            .Where(x => owners.Contains(x.Hardware) && x.Sensor.SensorType == type)
            .OrderByDescending(x => x.Sensor.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
            .Select(x => (double?)Math.Round(x.Sensor.Value!.Value, 1))
            .FirstOrDefault();
    }

    private static double? MaxSensor(
        IEnumerable<(IHardware Hardware, ISensor Sensor)> readings,
        IReadOnlyCollection<IHardware> owners,
        SensorType type)
    {
        var values = readings
            .Where(x => owners.Contains(x.Hardware) && x.Sensor.SensorType == type)
            // Intel "Distance to TjMax" sensors are headroom (how far from the
            // thermal limit), not a temperature. Mixing them into a max lets a
            // 63 °C headroom read hotter than a 45 °C package sensor, which is
            // exactly the inflated CPU temperature the dashboard displayed.
            .Where(x => type != SensorType.Temperature ||
                        !x.Sensor.Name.Contains("Distance to TjMax", StringComparison.OrdinalIgnoreCase))
            .Select(x => (double)x.Sensor.Value!.Value)
            .Where(v => type != SensorType.Temperature || v is > -20 and < 150)
            .ToList();
        return values.Count == 0 ? null : Math.Round(values.Max(), 1);
    }

    private static double? AverageSensor(
        IEnumerable<(IHardware Hardware, ISensor Sensor)> readings,
        IReadOnlyCollection<IHardware> owners,
        SensorType type)
    {
        var values = readings
            .Where(x => owners.Contains(x.Hardware) && x.Sensor.SensorType == type)
            .Select(x => (double)x.Sensor.Value!.Value)
            .Where(v => v > 0)
            .ToList();
        return values.Count == 0 ? null : Math.Round(values.Average());
    }

    private (double Download, double Upload) SampleNetworkRates()
    {
        var now = DateTimeOffset.UtcNow;
        var elapsed = Math.Max(0.1, (now - _previousNetworkAt).TotalSeconds);
        var (rx, tx) = ReadNetworkTotals();
        var download = Math.Max(0, rx - _previousRx) * 8d / elapsed / 1_000_000d;
        var upload = Math.Max(0, tx - _previousTx) * 8d / elapsed / 1_000_000d;
        _previousRx = rx;
        _previousTx = tx;
        _previousNetworkAt = now;
        return (Math.Round(download, 2), Math.Round(upload, 2));
    }

    private static (long Rx, long Tx) ReadNetworkTotals()
    {
        long rx = 0;
        long tx = 0;
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up ||
                adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                continue;
            try
            {
                var stats = adapter.GetIPv4Statistics();
                rx += stats.BytesReceived;
                tx += stats.BytesSent;
            }
            catch { }
        }
        return (rx, tx);
    }

    private (double ReadMbS, double WriteMbS) SampleDiskRates()
    {
        try
        {
            if (_diskReadCounter == null || _diskWriteCounter == null)
            {
                _diskReadCounter = new PerformanceCounter("PhysicalDisk", "Disk Read Bytes/sec", "_Total");
                _diskWriteCounter = new PerformanceCounter("PhysicalDisk", "Disk Write Bytes/sec", "_Total");
                // The first NextValue is a baseline, not a rate.
                _diskReadCounter.NextValue();
                _diskWriteCounter.NextValue();
                return (0, 0);
            }
            return (_diskReadCounter.NextValue() / 1_048_576d,
                    _diskWriteCounter.NextValue() / 1_048_576d);
        }
        catch
        {
            // Performance counters can be missing on unusual systems; the
            // dashboard shows zeros rather than failing.
            return (0, 0);
        }
    }

    private static double? ReadStorageUsedPercent()
    {
        try
        {
            var systemRoot = Path.GetPathRoot(Environment.SystemDirectory)!;
            var drive = new DriveInfo(systemRoot);
            if (!drive.IsReady || drive.TotalSize == 0) return null;
            return Math.Round((1d - drive.AvailableFreeSpace / (double)drive.TotalSize) * 100d, 1);
        }
        catch { return null; }
    }

    private static (double LoadPercent, double UsedGb, double TotalGb) ReadMemoryStatus()
    {
        var status = new MemoryStatusEx();
        if (!GlobalMemoryStatusEx(status)) return (0, 0, 0);
        var total = status.TotalPhys / 1_073_741_824d;
        var used = (status.TotalPhys - status.AvailPhys) / 1_073_741_824d;
        return (status.MemoryLoad, Math.Round(used, 1), Math.Round(total, 1));
    }

    private TelemetrySnapshot EmptySnapshot() => new(
        0, DateTimeOffset.UtcNow, Environment.MachineName, RuntimeInformation.OSDescription,
        new ComponentMetric("CPU"), new ComponentMetric("GPU"), new ComponentMetric("System memory"),
        null, 0, 0, 0, 0, Environment.TickCount64 / 1000, null,
        Array.Empty<NamedReading>(), Array.Empty<NamedReading>());

    private static string FriendlySensorName(string value, HardwareType type, bool fan)
    {
        var name = string.Join(' ', value.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (fan)
        {
            if (name.StartsWith("Fan #", StringComparison.OrdinalIgnoreCase))
                return "System Fan " + name[5..];
            if (name.Equals("GPU Fan", StringComparison.OrdinalIgnoreCase)) return "GPU Fan";
            if (name.Contains("CPU", StringComparison.OrdinalIgnoreCase)) return "CPU Fan";
            return name.Replace("#", "", StringComparison.Ordinal).Trim();
        }

        if (type == HardwareType.Cpu)
        {
            if (name.Equals("Core (Tctl/Tdie)", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Core (Tdie)", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Tctl/Tdie", StringComparison.OrdinalIgnoreCase)) return "CPU Die";
            if (name.StartsWith("CCD", StringComparison.OrdinalIgnoreCase))
                return "CPU " + name.Replace("(Tdie)", "", StringComparison.OrdinalIgnoreCase).Trim();
            if (name.StartsWith("Core #", StringComparison.OrdinalIgnoreCase))
                return "CPU Core " + name[6..];
            if (name.Equals("Core Max", StringComparison.OrdinalIgnoreCase)) return "CPU Core Max";
            if (!name.StartsWith("CPU", StringComparison.OrdinalIgnoreCase)) return "CPU " + name;
        }
        if (type is HardwareType.GpuAmd or HardwareType.GpuIntel or HardwareType.GpuNvidia)
        {
            if (name.Equals("GPU Core", StringComparison.OrdinalIgnoreCase)) return "GPU";
            if (name.Contains("Hot Spot", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("Hotspot", StringComparison.OrdinalIgnoreCase)) return "GPU Hotspot";
            if (name.Contains("Memory Junction", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("GPU Memory", StringComparison.OrdinalIgnoreCase)) return "GPU Memory";
            if (!name.StartsWith("GPU", StringComparison.OrdinalIgnoreCase)) return "GPU " + name;
        }
        if (type == HardwareType.Storage &&
            (name.Equals("Composite", StringComparison.OrdinalIgnoreCase) ||
             name.Equals("Temperature", StringComparison.OrdinalIgnoreCase))) return "SSD Temperature";
        if (type == HardwareType.Motherboard)
        {
            if (name.Equals("System", StringComparison.OrdinalIgnoreCase)) return "Motherboard";
            if (name.Equals("CPU", StringComparison.OrdinalIgnoreCase)) return "CPU Socket";
            if (name.StartsWith("Temperature #", StringComparison.OrdinalIgnoreCase))
                return "Board Sensor " + name[13..];
        }
        return name.Replace("#", "", StringComparison.Ordinal).Trim();
    }

    private static string FriendlySourceName(HardwareType type) => type switch
    {
        HardwareType.Cpu => "CPU",
        HardwareType.GpuAmd or HardwareType.GpuIntel or HardwareType.GpuNvidia => "GPU",
        HardwareType.Storage => "Storage",
        HardwareType.Motherboard or HardwareType.SuperIO => "Motherboard",
        _ => "System"
    };

    public void Dispose()
    {
        _stop.Cancel();
        _weather.Dispose();
        try { _samplingTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
        try { _computer?.Close(); } catch { }
        _stop.Dispose();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private sealed class MemoryStatusEx
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);
}
