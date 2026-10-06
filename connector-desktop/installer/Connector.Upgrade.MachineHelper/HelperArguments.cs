using System.Globalization;
using System.Security;
using System.Security.Principal;
using MutualChannel = Connector.Upgrade.MutualMachineChannel.MutualMachineChannel;

namespace Connector.Upgrade.MachineHelper;

internal sealed record HelperArguments(string PipeName, Guid OperationId, SecurityIdentifier CallerSid, int CallerPid)
{
    internal static HelperArguments Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (args.Count != 8 || args[0] != "--pipe" || args[2] != "--operation" ||
            args[4] != "--caller-sid" || args[6] != "--caller-pid")
            throw new ArgumentException("Expected exactly --pipe, --operation, --caller-sid, and --caller-pid once each.", nameof(args));

        if (!Guid.TryParseExact(args[3], "N", out var operationId) || operationId == Guid.Empty ||
            !string.Equals(args[3], operationId.ToString("N"), StringComparison.Ordinal))
            throw new ArgumentException("Operation id must be a non-empty lowercase N-format GUID.", nameof(args));
        if (!int.TryParse(args[7], NumberStyles.None, CultureInfo.InvariantCulture, out var callerPid) || callerPid <= 0)
            throw new ArgumentException("Caller PID must be a positive decimal process id.", nameof(args));

        SecurityIdentifier callerSid;
        try { callerSid = new SecurityIdentifier(args[5]); }
        catch (ArgumentException exception) { throw new ArgumentException("Caller SID is invalid.", nameof(args), exception); }
        if (!string.Equals(callerSid.Value, args[5], StringComparison.Ordinal))
            throw new ArgumentException("Caller SID must use its canonical string form.", nameof(args));

        var expectedPipe = MutualChannel.GetPipeName("A", operationId, callerSid);
        if (!string.Equals(args[1], expectedPipe, StringComparison.Ordinal))
            throw new SecurityException("Pipe name must be the exact A lane for this operation and caller SID.");
        return new HelperArguments(args[1], operationId, callerSid, callerPid);
    }
}
