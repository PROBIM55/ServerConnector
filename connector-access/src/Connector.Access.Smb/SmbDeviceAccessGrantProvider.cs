using System.Security.Cryptography;
using System.Text;
using Connector.Access;
using Connector.Access.Contracts;
using Microsoft.AspNetCore.DataProtection;

namespace Connector.Access.Smb;

public sealed class SmbDeviceAccessGrantProvider : IDeviceAccessGrantProvider, IConnectorSmbAccessReader
{
    public const string Name = "smb";
    private readonly ValidatedSmbProviderOptions _options;
    private readonly FileSmbProviderStateStore _states;
    private readonly SmbHelperClient _helper;

    public SmbDeviceAccessGrantProvider(
        HttpClient helperHttpClient,
        SmbProviderOptions options,
        IDataProtectionProvider protectionProvider)
    {
        ArgumentNullException.ThrowIfNull(helperHttpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(protectionProvider);
        _options = options.Validate(requireCertificate: false);
        _states = new FileSmbProviderStateStore(_options, protectionProvider);
        _helper = new SmbHelperClient(helperHttpClient, _options);
    }

    public string ProviderName => Name;

    public ValueTask<DeviceAccessProviderReceipt> ApplyAsync(DeviceAccessProviderCommand command, CancellationToken cancellationToken) =>
        ExecuteAsync(Validate(command, DeviceAccessProviderCommandKind.Apply), cancellationToken);

    public ValueTask<DeviceAccessProviderReceipt> RevokeAsync(DeviceAccessProviderCommand command, CancellationToken cancellationToken) =>
        ExecuteAsync(Validate(command, DeviceAccessProviderCommandKind.Revoke), cancellationToken);

    private async ValueTask<DeviceAccessProviderReceipt> ExecuteAsync(
        DeviceAccessProviderCommand command,
        CancellationToken cancellationToken)
    {
        var desired = command.Kind == DeviceAccessProviderCommandKind.Apply ? MapGrants(command.Resources) : [];
        await using var locked = await _states.LockAsync(command.DeviceId, cancellationToken);
        var state = await FenceAsync(locked, command, desired, cancellationToken);

        string? password = null;
        if (command.Kind == DeviceAccessProviderCommandKind.Apply && desired.Count > 0)
        {
            if (state.ProtectedPassword is null)
            {
                password = GeneratePassword();
                state.ProtectedPassword = _states.Protect(password);
                await locked.SaveAsync(cancellationToken);
            }
            else
            {
                try
                {
                    password = _states.Unprotect(state.ProtectedPassword);
                }
                catch (CryptographicException exception)
                {
                    throw new SmbHelperProtocolException("The protected SMB credential cannot be recovered.", exception);
                }
            }
        }

        try
        {
            var action = command.Kind == DeviceAccessProviderCommandKind.Revoke ? SmbHelperProtocol.Revoke : SmbHelperProtocol.Apply;
            var request = new SmbHelperRequest(
                SmbHelperProtocol.Version, command.CommandId, command.DeviceId, command.DesiredRevision,
                action, state.LocalUserName, state.LocalUserSid, password, desired);
            var response = await _helper.ReconcileAsync(request, cancellationToken);
            Confirm(response, request);
            state.LocalAccountAuthority = response.LocalAccountAuthority;
            if (!string.IsNullOrWhiteSpace(response.LocalUserSid)) state.LocalUserSid = response.LocalUserSid;
            state.AppliedRevision = command.DesiredRevision;
            state.AppliedKind = command.Kind;
            state.AppliedResources = response.Resources.OrderBy(resource => resource.ResourceId, StringComparer.Ordinal).ToArray();
            if (command.Kind == DeviceAccessProviderCommandKind.Revoke || desired.Count == 0)
                state.ProtectedPassword = null;
            await locked.SaveAsync(cancellationToken);
            return new DeviceAccessProviderReceipt(command.CommandId, command.DesiredRevision, command.Kind);
        }
        finally
        {
            if (password is not null)
            {
                // Strings cannot be zeroed; keep the credential scoped to this call and never persist/log it in plaintext.
                password = null;
            }
        }
    }

    public async ValueTask<DeviceAccessResult<ConnectorSmbAccess>> GetAsync(
        AuthenticatedDevice device,
        DeviceAccessProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(profile);
        var now = DateTimeOffset.UtcNow;
        if (profile.ExpiresAtUtc <= now || profile.DeviceId != device.DeviceId || profile.UserId != device.UserId ||
            profile.CompanyId != device.CompanyId || profile.AppliedRevision != profile.DesiredRevision ||
            profile.AppliedRevision != device.AccessRevision)
            return DeviceAccessResult<ConnectorSmbAccess>.Fail("smb_access_denied", "The current device profile is not eligible for SMB access.");

        try
        {
            await using var locked = await _states.LockAsync(device.DeviceId, cancellationToken);
            var state = locked.State;
            var desired = MapGrants(profile.Resources);
            if (state is null || state.UserId != device.UserId || state.CompanyId != device.CompanyId ||
                state.HighestRevision != profile.AppliedRevision || state.AppliedRevision != profile.AppliedRevision ||
                state.HighestKind != DeviceAccessProviderCommandKind.Apply || state.AppliedKind != DeviceAccessProviderCommandKind.Apply ||
                !SameGrants(desired, state.DesiredGrants) || state.AppliedResources.Count != _options.ResourceBindings.Count)
                return DeviceAccessResult<ConnectorSmbAccess>.Fail("smb_access_pending", "SMB access is not confirmed for the current profile.");

            string? password = null;
            try
            {
                if (desired.Count > 0)
                {
                    if (string.IsNullOrWhiteSpace(state.ProtectedPassword) || string.IsNullOrWhiteSpace(state.LocalUserSid) ||
                        string.IsNullOrWhiteSpace(state.LocalAccountAuthority))
                        return DeviceAccessResult<ConnectorSmbAccess>.Fail("smb_access_pending", "SMB credential state is incomplete.");
                    password = _states.Unprotect(state.ProtectedPassword);
                }

                var request = new SmbHelperRequest(
                    SmbHelperProtocol.Version, state.HighestCommandId, state.DeviceId, state.HighestRevision,
                    SmbHelperProtocol.Apply, state.LocalUserName, state.LocalUserSid, password, state.DesiredGrants);
                var response = await _helper.ReconcileAsync(request, cancellationToken);
                Confirm(response, request);
                if (!SameObservations(state.AppliedResources, response.Resources) ||
                    !string.Equals(state.LocalAccountAuthority, response.LocalAccountAuthority, StringComparison.OrdinalIgnoreCase) ||
                    desired.Count > 0 && !string.Equals(state.LocalUserSid, response.LocalUserSid, StringComparison.OrdinalIgnoreCase))
                    return DeviceAccessResult<ConnectorSmbAccess>.Fail("smb_access_pending", "Fresh SMB readback did not match the applied receipt.");

                var resources = profile.Resources.Select(grant =>
                {
                    var binding = _options.ResourceBindings.SingleOrDefault(candidate =>
                        candidate.ResourceId == grant.ResourceId && candidate.ResourceKind == grant.ResourceKind);
                    return binding is null ? null : new ConnectorSmbResourceAccess(
                        grant.ResourceId, grant.ResourceKind, grant.ProjectId, grant.Permissions, binding.ClientShareUnc);
                }).Where(resource => resource is not null).Cast<ConnectorSmbResourceAccess>()
                  .OrderBy(resource => resource.ResourceKind, StringComparer.Ordinal)
                  .ThenBy(resource => resource.ResourceId, StringComparer.Ordinal).ToArray();
                var expires = new[] { profile.ExpiresAtUtc, now.Add(_options.AccessReceiptLifetime) }.Min();
                return DeviceAccessResult<ConnectorSmbAccess>.Success(new ConnectorSmbAccess(
                    DeviceAccessProtocol.Version, device.DeviceId, profile.AppliedRevision, expires,
                    desired.Count == 0 ? null : $"{state.LocalAccountAuthority}\\{state.LocalUserName}",
                    desired.Count == 0 ? null : password,
                    resources));
            }
            finally
            {
                password = null;
            }
        }
        catch (CryptographicException)
        {
            return DeviceAccessResult<ConnectorSmbAccess>.Fail("smb_access_unavailable", "The protected SMB credential is unavailable.");
        }
        catch (SmbProviderBusyException)
        {
            return DeviceAccessResult<ConnectorSmbAccess>.Fail("smb_access_unavailable", "SMB state is busy.");
        }
        catch (SmbHelperProtocolException)
        {
            return DeviceAccessResult<ConnectorSmbAccess>.Fail("smb_access_unavailable", "SMB helper readback failed.");
        }
    }

    private async ValueTask<SmbProviderState> FenceAsync(
        FileSmbProviderStateStore.LockedSmbState locked,
        DeviceAccessProviderCommand command,
        IReadOnlyList<SmbHelperGrant> desired,
        CancellationToken cancellationToken)
    {
        var state = locked.State;
        if (state is null)
        {
            state = new SmbProviderState
            {
                DeviceId = command.DeviceId,
                UserId = command.UserId,
                CompanyId = command.CompanyId,
                LocalUserName = LocalUserName(command.DeviceId),
                HighestCommandId = command.CommandId,
                HighestRevision = command.DesiredRevision,
                HighestKind = command.Kind,
                DesiredGrants = desired,
            };
            locked.State = state;
            await locked.SaveAsync(cancellationToken);
            return state;
        }
        if (!string.Equals(state.UserId, command.UserId, StringComparison.Ordinal) ||
            !string.Equals(state.CompanyId, command.CompanyId, StringComparison.Ordinal))
            throw new SmbRevisionRejectedException("SMB device ownership changed for a durable identity.");
        if (command.DesiredRevision < state.HighestRevision ||
            command.DesiredRevision == state.HighestRevision &&
            (command.Kind != state.HighestKind || !string.Equals(command.CommandId, state.HighestCommandId, StringComparison.Ordinal) ||
             !SameGrants(desired, state.DesiredGrants)))
            throw new SmbRevisionRejectedException("A stale or conflicting SMB command was rejected by the durable fence.");
        if (command.DesiredRevision > state.HighestRevision)
        {
            state.HighestRevision = command.DesiredRevision;
            state.HighestKind = command.Kind;
            state.HighestCommandId = command.CommandId;
            state.DesiredGrants = desired;
            state.AppliedKind = null;
            await locked.SaveAsync(cancellationToken);
        }
        return state;
    }

    private IReadOnlyList<SmbHelperGrant> MapGrants(IReadOnlyList<ResourceGrant> grants)
    {
        var mapped = new List<SmbHelperGrant>();
        foreach (var grant in grants)
        {
            var binding = _options.ResourceBindings.SingleOrDefault(candidate =>
                string.Equals(candidate.ResourceId, grant.ResourceId, StringComparison.Ordinal) &&
                string.Equals(candidate.ResourceKind, grant.ResourceKind, StringComparison.Ordinal));
            if (binding is null)
            {
                if (_options.ResourceBindings.Any(candidate => string.Equals(candidate.ResourceKind, grant.ResourceKind, StringComparison.Ordinal)))
                    throw new ArgumentException("An SMB resource grant is not present in the fixed resource mapping.");
                continue;
            }
            if (grant.Permissions.Count == 0 || grant.Permissions.Contains(ConnectorPermission.Execute))
                throw new ArgumentException("SMB resource grants contain an unsupported permission.");
            var permission = grant.Permissions.Any(permission => permission is ConnectorPermission.Publish or ConnectorPermission.Manage)
                ? SmbHelperProtocol.Change
                : grant.Permissions.All(permission => permission == ConnectorPermission.Read)
                    ? SmbHelperProtocol.Read
                    : throw new ArgumentException("SMB resource grants contain an unsupported permission.");
            mapped.Add(new SmbHelperGrant(binding.HelperResourceId, permission));
        }
        if (mapped.Select(grant => grant.ResourceId).Distinct(StringComparer.Ordinal).Count() != mapped.Count)
            throw new ArgumentException("SMB resource grants contain duplicate mapped resources.");
        return mapped.OrderBy(grant => grant.ResourceId, StringComparer.Ordinal).ToArray();
    }

    private void Confirm(SmbHelperResponse response, SmbHelperRequest request)
    {
        if (response.SchemaVersion != SmbHelperProtocol.Version || response.CommandId != request.CommandId ||
            response.DeviceId != request.DeviceId || response.Revision != request.Revision || response.Action != request.Action ||
            response.LocalUserName != request.LocalUserName || !ValidAuthority(response.LocalAccountAuthority) || response.Resources is null ||
            response.Resources.Select(resource => resource.ResourceId).Distinct(StringComparer.Ordinal).Count() != response.Resources.Count)
            throw new SmbHelperProtocolException("SMB helper response identity does not match the command.");
        if (!string.IsNullOrWhiteSpace(request.ExpectedLocalUserSid) &&
            !string.Equals(response.LocalUserSid, request.ExpectedLocalUserSid, StringComparison.OrdinalIgnoreCase))
            throw new SmbHelperProtocolException("SMB helper local-user SID does not match the durable provider state.");

        var expected = _options.ResourceBindings.ToDictionary(
            binding => binding.HelperResourceId,
            binding => request.Grants.SingleOrDefault(grant => grant.ResourceId == binding.HelperResourceId)?.Permission ?? SmbHelperProtocol.None,
            StringComparer.Ordinal);
        if (response.Resources.Count != expected.Count || response.Resources.Any(resource =>
                !expected.TryGetValue(resource.ResourceId, out var permission) ||
                resource.ShareAccess != permission || resource.NtfsAccess != permission ||
                string.IsNullOrWhiteSpace(resource.ShareName) || string.IsNullOrWhiteSpace(resource.CanonicalRootPath)))
            throw new SmbHelperProtocolException("SMB helper did not confirm the exact resource ACL state.");
        foreach (var binding in _options.ResourceBindings)
        {
            var observed = response.Resources.Single(resource => resource.ResourceId == binding.HelperResourceId);
            if (!string.Equals(observed.ShareName, binding.ShareName, StringComparison.OrdinalIgnoreCase))
                throw new SmbHelperProtocolException("SMB helper share name does not match the fixed client UNC binding.");
        }

        var shouldEnable = request.Action == SmbHelperProtocol.Apply && request.Grants.Count > 0;
        if (response.AccountEnabled != shouldEnable || response.ActiveSessionCount != 0 && !shouldEnable ||
            shouldEnable && string.IsNullOrWhiteSpace(response.LocalUserSid))
            throw new SmbHelperProtocolException("SMB helper did not confirm the expected local-user state.");
    }

    private static DeviceAccessProviderCommand Validate(DeviceAccessProviderCommand command, DeviceAccessProviderCommandKind kind)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Kind != kind || !string.Equals(command.ProviderName, Name, StringComparison.OrdinalIgnoreCase) ||
            command.DesiredRevision < 1 || string.IsNullOrWhiteSpace(command.CommandId) || string.IsNullOrWhiteSpace(command.DeviceId) ||
            string.IsNullOrWhiteSpace(command.UserId) || string.IsNullOrWhiteSpace(command.CompanyId))
            throw new ArgumentException("The SMB provider command is invalid.", nameof(command));
        return command;
    }

    private static bool SameGrants(IReadOnlyList<SmbHelperGrant> left, IReadOnlyList<SmbHelperGrant> right) =>
        left.OrderBy(value => value.ResourceId, StringComparer.Ordinal)
            .SequenceEqual(right.OrderBy(value => value.ResourceId, StringComparer.Ordinal));

    private static bool SameObservations(
        IReadOnlyList<SmbHelperResourceObservation> left,
        IReadOnlyList<SmbHelperResourceObservation> right) =>
        left.OrderBy(value => value.ResourceId, StringComparer.Ordinal)
            .Zip(right.OrderBy(value => value.ResourceId, StringComparer.Ordinal))
            .All(pair => pair.First.ResourceId == pair.Second.ResourceId &&
                         string.Equals(pair.First.ShareName, pair.Second.ShareName, StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(Path.GetFullPath(pair.First.CanonicalRootPath), Path.GetFullPath(pair.Second.CanonicalRootPath), StringComparison.OrdinalIgnoreCase) &&
                         pair.First.ShareAccess == pair.Second.ShareAccess && pair.First.NtfsAccess == pair.Second.NtfsAccess);

    private static string LocalUserName(string deviceId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(deviceId));
        return "cnb_" + Convert.ToHexString(digest.AsSpan(0, 8)).ToLowerInvariant();
    }

    private static bool ValidAuthority(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 15 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');

    private static string GeneratePassword()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#$%_-";
        Span<byte> random = stackalloc byte[32];
        RandomNumberGenerator.Fill(random);
        var chars = new char[random.Length + 4];
        chars[0] = 'A'; chars[1] = 'a'; chars[2] = '2'; chars[3] = '!';
        for (var index = 0; index < random.Length; index++) chars[index + 4] = alphabet[random[index] % alphabet.Length];
        return new string(chars);
    }
}
