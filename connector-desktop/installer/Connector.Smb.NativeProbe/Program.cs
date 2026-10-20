using System.Security.Principal;
using System.Text.Json;
using Connector.SmbAccess;

if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true" ||
    Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted")
    throw new InvalidOperationException("Disposable hosted Windows only.");
using var identity = WindowsIdentity.GetCurrent();
if (new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
    throw new InvalidOperationException("Ordinary-user probe required.");
var input = JsonSerializer.Deserialize<Settings>(Console.ReadLine() ?? "") ?? throw new InvalidDataException();
var native = new WindowsSmbNativeApi();
const string drive = "Y:";
if (native.GetTarget(drive) is not null) throw new InvalidOperationException("Fixture drive is occupied.");
void Step(string step) { Console.WriteLine(JsonSerializer.Serialize(new { step, ok = true })); Console.Out.Flush(); }
void Mount(bool encrypted)
{
    var result = encrypted ? native.AddEncryptedConnection(drive, input.Share, input.User, input.Password)
        : native.AddConnection(drive, input.Share, input.User, input.Password);
    if (result != 0) throw new InvalidOperationException("Native mapping refused: " + result);
}
void Unmount()
{
    if (native.CancelConnection(drive) != 0 || native.GetTarget(drive) is not null)
        throw new InvalidOperationException("Exact fixture drive removal failed.");
}
try
{
    Mount(false);
    if (native.ReadEncryptedIntegrity(drive, input.Share, allowLoopback: true))
        throw new InvalidOperationException("Fixture plain mapping unexpectedly encrypted; negative proof is inconclusive.");
    if (File.ReadAllText(@"Y:\proof.txt") != input.Proof) throw new InvalidDataException("Fixture proof differs.");
    Unmount();
    Step("plain-mapping-rejected-by-crypto-readback");
    Mount(true);
    if (!native.ReadEncryptedIntegrity(drive, input.Share, allowLoopback: true))
        throw new InvalidOperationException("Required SMB3 encryption was not observed.");
    if (File.ReadAllText(@"Y:\proof.txt") != input.Proof) throw new InvalidDataException("Encrypted proof differs.");
    if (!native.ReadEncryptedIntegrity(drive, input.Share, allowLoopback: true))
        throw new InvalidOperationException("Encrypted reuse changed security.");
    Unmount();
    Step("encrypted-native-read-and-reuse");
    if (Console.ReadLine() != "revoked") throw new InvalidDataException("Expected exact revoke barrier.");
    var denied = native.AddEncryptedConnection(drive, input.Share, input.User, input.Password);
    if (denied == 0) { Unmount(); throw new InvalidOperationException("Revoked account regained SMB access."); }
    if (native.GetTarget(drive) is not null) throw new InvalidOperationException("Revoked mapping remains.");
    Step("revoked-account-denied");
}
finally
{
    if (string.Equals(native.GetTarget(drive), input.Share, StringComparison.OrdinalIgnoreCase)) Unmount();
}
sealed record Settings(string Share, string User, string Password, string Proof);
