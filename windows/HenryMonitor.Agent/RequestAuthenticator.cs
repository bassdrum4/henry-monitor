using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace HenryMonitor;

public sealed class RequestAuthenticator
{
    private readonly ConfigStore _config;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seenNonces = new();
    private long _lastCleanupTicks;

    public RequestAuthenticator(ConfigStore config) => _config = config;

    public bool Validate(HttpContext context, string action = "")
    {
        var timestampText = context.Request.Headers["X-HM-Timestamp"].ToString();
        var nonce = context.Request.Headers["X-HM-Nonce"].ToString();
        var suppliedSignature = context.Request.Headers["X-HM-Signature"].ToString();
        if (!long.TryParse(timestampText, NumberStyles.None, CultureInfo.InvariantCulture, out var timestamp) ||
            nonce.Length is < 16 or > 80 || suppliedSignature.Length == 0)
            return false;

        var now = DateTimeOffset.UtcNow;
        var requestTime = DateTimeOffset.FromUnixTimeSeconds(timestamp);
        if ((now - requestTime).Duration() > TimeSpan.FromSeconds(60)) return false;
        try
        {
            var canonical = string.Join('\n',
                context.Request.Method.ToUpperInvariant(),
                context.Request.Path.Value ?? "",
                timestampText,
                nonce,
                action);
            var expected = Convert.FromBase64String(ComputeSignature(_config.Current.AccessToken, canonical));
            var supplied = Convert.FromBase64String(suppliedSignature);
            if (supplied.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(supplied, expected))
                return false;
            if (!_seenNonces.TryAdd(nonce, now)) return false;
            Cleanup(now);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string ComputeSignature(string base64Token, string canonical)
    {
        using var hmac = new HMACSHA256(Convert.FromBase64String(base64Token));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(canonical)));
    }

    private void Cleanup(DateTimeOffset now)
    {
        if (now.UtcTicks - Interlocked.Read(ref _lastCleanupTicks) < TimeSpan.FromMinutes(1).Ticks) return;
        Interlocked.Exchange(ref _lastCleanupTicks, now.UtcTicks);
        foreach (var pair in _seenNonces)
            if (now - pair.Value > TimeSpan.FromMinutes(2)) _seenNonces.TryRemove(pair.Key, out _);
    }
}
