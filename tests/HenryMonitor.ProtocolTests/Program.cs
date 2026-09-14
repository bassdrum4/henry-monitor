using HenryMonitor;
using Microsoft.AspNetCore.Http;

var failures = new List<string>();

void Check(bool condition, string message)
{
    if (!condition) failures.Add(message);
}

var pairing = new PairingManager();
var firstCode = pairing.Code;
Check(firstCode.Length == 6 && firstCode.All(char.IsDigit), "Pairing code must contain six digits.");
Check(pairing.Validate(firstCode), "Current pairing code should validate.");
pairing.Rotate();
Check(!pairing.Validate(firstCode), "Rotated pairing code must invalidate the previous code.");

var testDirectory = Path.Combine(Path.GetTempPath(), "HenryMonitorProtocolTests-" + Guid.NewGuid().ToString("N"));
var config = new ConfigStore(testDirectory);
Check(Convert.FromBase64String(config.Current.AccessToken).Length == 32, "Access key must contain 256 bits.");
var tokenBeforePairing = config.Current.AccessToken;
config.MarkPaired("test display");
Check(config.Current.AccessToken == tokenBeforePairing,
    "Completing pairing must not invalidate the access key returned to the phone.");
var authenticator = new RequestAuthenticator(config);

var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
var nonce = Guid.NewGuid().ToString();
var canonical = $"GET\n/api/status\n{timestamp}\n{nonce}\n";
var signature = RequestAuthenticator.ComputeSignature(config.Current.AccessToken, canonical);
var context = new DefaultHttpContext();
context.Request.Method = "GET";
context.Request.Path = "/api/status";
context.Request.Headers["X-HM-Timestamp"] = timestamp;
context.Request.Headers["X-HM-Nonce"] = nonce;
context.Request.Headers["X-HM-Signature"] = signature;
Check(authenticator.Validate(context), "A valid signed request should authenticate.");
Check(!authenticator.Validate(context), "A signed request nonce must not be replayable.");

var badContext = new DefaultHttpContext();
badContext.Request.Method = "POST";
badContext.Request.Path = "/api/control";
badContext.Request.Headers["X-HM-Timestamp"] = timestamp;
badContext.Request.Headers["X-HM-Nonce"] = Guid.NewGuid().ToString();
badContext.Request.Headers["X-HM-Signature"] = signature;
Check(!authenticator.Validate(badContext, "shutdown"), "A signature must not authorize a different method, path, or action.");

var staleContext = new DefaultHttpContext();
staleContext.Request.Method = "GET";
staleContext.Request.Path = "/api/status";
staleContext.Request.Headers["X-HM-Timestamp"] = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds().ToString();
staleContext.Request.Headers["X-HM-Nonce"] = Guid.NewGuid().ToString();
staleContext.Request.Headers["X-HM-Signature"] = signature;
Check(!authenticator.Validate(staleContext), "Stale requests must be rejected.");

if (failures.Count > 0)
{
    Console.Error.WriteLine(string.Join(Environment.NewLine, failures.Select(x => "FAIL: " + x)));
    return 1;
}

Console.WriteLine("All protocol and pairing tests passed.");
return 0;
