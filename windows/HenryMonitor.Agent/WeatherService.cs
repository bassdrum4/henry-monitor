using System.Globalization;
using System.Text.Json;

namespace HenryMonitor;

/// <summary>
/// Pulls local weather from Open-Meteo (free, no API key). The location comes
/// from config.json ("latitude"/"longitude"/"city") when present, otherwise
/// from the machine's public IP. Refreshes every 30 minutes; on failure the
/// last reading is kept and a retry happens sooner. A missing reading never
/// blocks telemetry — the phone simply hides the weather strip.
/// </summary>
public sealed class WeatherService : IDisposable
{
    private static readonly HttpClient Http = CreateClient();
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private WeatherInfo? _latest;
    private (double Lat, double Lon, string City)? _place;

    public WeatherService()
    {
        _place = LoadConfiguredLocation();
        _loop = Task.Run(() => RunAsync(_stop.Token));
    }

    public WeatherInfo? Latest { get { lock (_gate) return _latest; } }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(30)
        })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("HenrySystemMonitor/1.0");
        return client;
    }

    private async Task RunAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            var delay = TimeSpan.FromMinutes(30);
            try
            {
                var info = await FetchAsync(token).ConfigureAwait(false);
                if (info is not null)
                {
                    lock (_gate) _latest = info;
                }
                else
                {
                    delay = TimeSpan.FromMinutes(5);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Offline, blocked, or an API change: keep serving the last
                // reading and try again shortly.
                delay = TimeSpan.FromMinutes(5);
            }

            try { await Task.Delay(delay, token).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task<WeatherInfo?> FetchAsync(CancellationToken token)
    {
        var place = _place ?? await LocateAsync(token).ConfigureAwait(false);
        if (place is null) return null;

        var url = string.Format(CultureInfo.InvariantCulture,
            "https://api.open-meteo.com/v1/forecast?latitude={0}&longitude={1}" +
            "&current=temperature_2m,apparent_temperature,relative_humidity_2m,weather_code,wind_speed_10m,is_day",
            place.Value.Lat, place.Value.Lon);

        using var response = await Http.GetAsync(url, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
        var current = doc.RootElement.GetProperty("current");
        var info = new WeatherInfo(
            current.GetProperty("temperature_2m").GetDouble(),
            current.GetProperty("apparent_temperature").GetDouble(),
            current.GetProperty("relative_humidity_2m").GetInt32(),
            current.GetProperty("wind_speed_10m").GetDouble(),
            DescribeWeather(current.GetProperty("weather_code").GetInt32()),
            current.GetProperty("is_day").GetInt32() == 1,
            place.Value.City);

        _place = place;
        return info;
    }

    private async Task<(double Lat, double Lon, string City)?> LocateAsync(CancellationToken token)
    {
        // ipwho.is serves HTTPS without a key; ip-api.com is the fallback.
        foreach (var url in new[] { "https://ipwho.is/", "http://ip-api.com/json/" })
        {
            try
            {
                using var response = await Http.GetAsync(url, token).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) continue;
                using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
                var root = doc.RootElement;
                if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False) continue;
                if (!root.TryGetProperty("latitude", out var latEl) && !root.TryGetProperty("lat", out latEl)) continue;
                if (!root.TryGetProperty("longitude", out var lonEl) && !root.TryGetProperty("lon", out lonEl)) continue;
                var lat = latEl.GetDouble();
                var lon = lonEl.GetDouble();
                if (double.IsNaN(lat) || double.IsNaN(lon) || (lat == 0 && lon == 0)) continue;
                // IP geolocation typically resolves to the ISP's regional hub
                // rather than the actual town, so only its coordinates are
                // trusted; the city label is reverse-geocoded from them.
                var city = await ReverseGeocodeCityAsync(lat, lon, token).ConfigureAwait(false);
                return (lat, lon, city);
            }
            catch
            {
                // Try the next provider.
            }
        }
        return null;
    }

    private static async Task<string> ReverseGeocodeCityAsync(double lat, double lon, CancellationToken token)
    {
        try
        {
            var url = string.Format(CultureInfo.InvariantCulture,
                "https://geocoding-api.open-meteo.com/v1/reverse?latitude={0}&longitude={1}&count=1&language=en&format=json",
                lat, lon);
            using var response = await Http.GetAsync(url, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return "";
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false));
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0) return "";
            var first = results[0];
            // Prefer the city/town/village over whatever generic name is set.
            foreach (var key in new[] { "city", "town", "village", "name" })
            {
                if (first.TryGetProperty(key, out var el) && el.GetString() is { Length: > 0 } value)
                    return value;
            }
        }
        catch
        {
            // A missing city label only means the dashboard shows a blank
            // location name; the coordinates still drive the forecast.
        }
        return "";
    }

    /// <summary>
    /// Reads optional "latitude"/"longitude"/"city" keys from config.json so
    /// the weather can be pinned to a real place when IP geolocation is
    /// wrong. Unknown keys are tolerated; invalid values fall back to IP
    /// lookup.
    /// </summary>
    private static (double Lat, double Lon, string City)? LoadConfiguredLocation()
    {
        try
        {
            var path = ConfigStore.DefaultConfigPath();
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            if (!root.TryGetProperty("latitude", out var latEl) ||
                !root.TryGetProperty("longitude", out var lonEl) ||
                latEl.ValueKind != JsonValueKind.Number || lonEl.ValueKind != JsonValueKind.Number)
                return null;
            var lat = latEl.GetDouble();
            var lon = lonEl.GetDouble();
            if (double.IsNaN(lat) || double.IsNaN(lon) || (lat == 0 && lon == 0)) return null;
            var city = root.TryGetProperty("city", out var cityEl) && cityEl.ValueKind == JsonValueKind.String
                ? cityEl.GetString() ?? ""
                : "";
            return (lat, lon, city);
        }
        catch
        {
            return null;
        }
    }

    private static string DescribeWeather(int code) => code switch
    {
        0 => "Clear sky",
        1 => "Mainly clear",
        2 => "Partly cloudy",
        3 => "Overcast",
        45 or 48 => "Fog",
        51 or 53 or 55 => "Drizzle",
        56 or 57 => "Freezing drizzle",
        61 => "Light rain",
        63 => "Rain",
        65 => "Heavy rain",
        66 or 67 => "Freezing rain",
        71 => "Light snow",
        73 => "Snow",
        75 => "Heavy snow",
        77 => "Snow grains",
        80 or 81 or 82 => "Rain showers",
        85 or 86 => "Snow showers",
        95 => "Thunderstorm",
        96 or 99 => "Thunderstorm with hail",
        _ => "Unsettled"
    };

    public void Dispose()
    {
        _stop.Cancel();
        try { _loop.Wait(TimeSpan.FromSeconds(3)); } catch { }
        _stop.Dispose();
    }
}
