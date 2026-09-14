using System.Security.Cryptography;

namespace HenryMonitor;

public sealed class PairingManager
{
    private readonly object _gate = new();
    private string _code = NewCode();
    private DateTimeOffset _expiresAt = DateTimeOffset.UtcNow.AddMinutes(15);

    public event EventHandler? Changed;

    public string Code
    {
        get
        {
            lock (_gate)
            {
                EnsureFresh();
                return _code;
            }
        }
    }

    public DateTimeOffset ExpiresAt
    {
        get { lock (_gate) return _expiresAt; }
    }

    public bool Validate(string candidate)
    {
        lock (_gate)
        {
            EnsureFresh();
            return candidate.Length == 6 && CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(candidate),
                System.Text.Encoding.UTF8.GetBytes(_code));
        }
    }

    public void Rotate()
    {
        lock (_gate)
        {
            _code = NewCode();
            _expiresAt = DateTimeOffset.UtcNow.AddMinutes(15);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void EnsureFresh()
    {
        if (DateTimeOffset.UtcNow < _expiresAt) return;
        _code = NewCode();
        _expiresAt = DateTimeOffset.UtcNow.AddMinutes(15);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static string NewCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
}
