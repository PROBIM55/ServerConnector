using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Connector.Access.Contracts;

namespace Connector.Access.NetBird;

public sealed class NetBirdDeviceAccessGrantProvider : IDeviceAccessGrantProvider, IConnectorVpnBootstrapReader
{
    public const string Name = "vpn";
    private readonly NetBirdManagementClient _management;
    private readonly FileNetBirdProviderStateStore _states;
    private readonly ValidatedNetBirdOptions _options;
    private readonly TimeProvider _timeProvider;

    public NetBirdDeviceAccessGrantProvider(
        HttpClient managementHttpClient,
        NetBirdOptions options,
        Microsoft.AspNetCore.DataProtection.IDataProtectionProvider protectionProvider,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(managementHttpClient);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(protectionProvider);
        _options = options.Validate();
        _management = new NetBirdManagementClient(managementHttpClient, _options);
        _states = new FileNetBirdProviderStateStore(_options, protectionProvider);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string ProviderName => Name;

    public ValueTask<DeviceAccessProviderReceipt> ApplyAsync(
        DeviceAccessProviderCommand command,
        CancellationToken cancellationToken) => ExecuteApplyAsync(Validate(command, DeviceAccessProviderCommandKind.Apply), cancellationToken);

    public ValueTask<DeviceAccessProviderReceipt> RevokeAsync(
        DeviceAccessProviderCommand command,
        CancellationToken cancellationToken) => ExecuteRevokeAsync(Validate(command, DeviceAccessProviderCommandKind.Revoke), cancellationToken);

    public async ValueTask<DeviceAccessResult<ConnectorVpnBootstrap>> GetAsync(
        AuthenticatedDevice device,
        CancellationToken cancellationToken)
    {
        await using var locked = await _states.LockAsync(device.DeviceId, cancellationToken);
        var state = locked.State;
        if (state is null || state.HighestKind != DeviceAccessProviderCommandKind.Apply ||
            state.HighestRevision != device.AccessRevision ||
            !string.Equals(state.UserId, device.UserId, StringComparison.Ordinal) ||
            !string.Equals(state.CompanyId, device.CompanyId, StringComparison.Ordinal))
        {
            return DeviceAccessResult<ConnectorVpnBootstrap>.Fail("vpn_bootstrap_unavailable", "No current VPN bootstrap is available for this device.");
        }
        if (state.PeerId is not null || string.IsNullOrWhiteSpace(state.ProtectedSetupKey) ||
            state.SetupKeyExpiresAtUtc is null || state.SetupKeyExpiresAtUtc <= _timeProvider.GetUtcNow())
        {
            return DeviceAccessResult<ConnectorVpnBootstrap>.Fail("vpn_bootstrap_consumed", "The VPN bootstrap has expired or the peer is already registered.");
        }

        string setupKey;
        try
        {
            setupKey = _states.UnprotectSetupKey(state.ProtectedSetupKey);
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException)
        {
            return DeviceAccessResult<ConnectorVpnBootstrap>.Fail("vpn_bootstrap_protection", "The protected VPN bootstrap cannot be recovered.");
        }
        return DeviceAccessResult<ConnectorVpnBootstrap>.Success(new ConnectorVpnBootstrap(
            setupKey,
            _options.ManagementUri.AbsoluteUri.TrimEnd('/'),
            state.HighestRevision,
            state.SetupKeyExpiresAtUtc.Value));
    }

    public async ValueTask<DeviceAccessResult<ConnectorVpnTransportState>> GetStateAsync(
        AuthenticatedDevice device,
        CancellationToken cancellationToken)
    {
        await using var locked = await _states.LockAsync(device.DeviceId, cancellationToken);
        var state = locked.State;
        if (state is null || state.HighestRevision != device.AccessRevision ||
            !string.Equals(state.UserId, device.UserId, StringComparison.Ordinal) ||
            !string.Equals(state.CompanyId, device.CompanyId, StringComparison.Ordinal))
        {
            return DeviceAccessResult<ConnectorVpnTransportState>.Fail(
                "vpn_state_unavailable", "No current VPN transport state is available for this device.");
        }

        var now = _timeProvider.GetUtcNow();
        if (state.HighestKind == DeviceAccessProviderCommandKind.Revoke)
            return State("revoked", null, [], now);

        if (state.AppliedKind != DeviceAccessProviderCommandKind.Apply ||
            state.AppliedRevision != state.HighestRevision)
        {
            return State("connecting", null, [], now);
        }
        if (string.IsNullOrWhiteSpace(state.BootstrapGroupId) ||
            string.IsNullOrWhiteSpace(state.PeerId) ||
            string.IsNullOrWhiteSpace(state.PolicyId))
        {
            return State("unknown", null, [], now);
        }

        await _management.AssertBootstrapIsolationAsync(cancellationToken);
        var group = await _management.GetGroupAsync(state.BootstrapGroupId, cancellationToken);
        var policy = await _management.GetPolicyAsync(state.PolicyId, cancellationToken);
        var peer = await _management.GetPeerAsync(state.PeerId, cancellationToken);
        if (!IsOwnedReadyState(state, group, policy, peer, now))
            return State("unknown", null, [], now);

        var addresses = new[] { NormalizeAssignedAddress(peer!.Ip), NormalizeAssignedAddress(peer.Ipv6) }
            .Where(address => address is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return State("ready", peer.Id, addresses, now);

        DeviceAccessResult<ConnectorVpnTransportState> State(
            string status,
            string? peerId,
            IReadOnlyList<string> addresses,
            DateTimeOffset observedAtUtc) =>
            DeviceAccessResult<ConnectorVpnTransportState>.Success(new ConnectorVpnTransportState(
                state.DeviceId,
                state.HighestRevision,
                status,
                peerId,
                _options.ManagementUri.AbsoluteUri.TrimEnd('/'),
                addresses,
                observedAtUtc));
    }

    private async ValueTask<DeviceAccessProviderReceipt> ExecuteApplyAsync(
        DeviceAccessProviderCommand command,
        CancellationToken cancellationToken)
    {
        await using var locked = await _states.LockAsync(command.DeviceId, cancellationToken);
        var state = await FenceAsync(locked, command, cancellationToken);
        if (state.HighestKind == DeviceAccessProviderCommandKind.Revoke)
        {
            await ReconcileRevokeAsync(locked, state, cancellationToken);
            throw new NetBirdRevisionRejectedException("A fenced revoke prevents this NetBird apply.");
        }

        var groupName = ResourceName("connector-device", command.DeviceId);
        var group = state.BootstrapGroupId is null
            ? null
            : await _management.GetGroupAsync(state.BootstrapGroupId, cancellationToken);
        if (group is null)
        {
            group = await _management.EnsureDeviceGroupAsync(groupName, cancellationToken);
            state.BootstrapGroupId = group.Id;
            state.PeerId = null;
            await locked.SaveAsync(cancellationToken);
        }


        try
        {
            await _management.AssertBootstrapIsolationAsync(cancellationToken);
            if (_options.DnsDistributionGroupId is { } dnsGroupId)
            {
                if (string.Equals(group.Id, dnsGroupId, StringComparison.Ordinal))
                    throw new InvalidOperationException("The DNS distribution group must be separate from the per-device group.");
                await _management.AssertDnsDistributionGroupIsolationAsync(dnsGroupId, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await ReconcileRevokeAsync(locked, state, cancellationToken);
            throw;
        }

        if (state.PeerId is null)
        {
            group = await _management.GetGroupAsync(group.Id, cancellationToken)
                ?? throw new InvalidOperationException("The device-owned NetBird group disappeared.");
            var peers = group.Peers ?? [];
            if (peers.Count > 1)
                throw new InvalidOperationException("A device-owned NetBird bootstrap group contains more than one peer.");
            if (peers.Count == 0)
            {
                await EnsureStableSetupKeyAsync(locked, state, group.Id, cancellationToken);
                throw new NetBirdPeerNotReadyException("The NetBird setup key exists, but its peer has not registered yet.");
            }
            state.PeerId = peers[0].Id;
            await locked.SaveAsync(cancellationToken);
        }

        var peer = await RequireReadyPeerAsync(state.PeerId, cancellationToken);
        var policyWrite = BuildPolicy(command, group.Id);
        var policy = await _management.UpsertPolicyAsync(state.PolicyId, policyWrite, cancellationToken);
        state.PolicyId = policy.Id;
        await locked.SaveAsync(cancellationToken);

        await ConfirmPolicyAsync(policy.Id, policyWrite, cancellationToken);
        peer = await RequireReadyPeerAsync(state.PeerId, cancellationToken);
        await RevokeSetupKeyAndForgetSecretAsync(locked, state, cancellationToken);

        state.PeerIpv4 = NormalizeAssignedAddress(peer.Ip);
        state.PeerIpv6 = NormalizeAssignedAddress(peer.Ipv6);
        state.AppliedPolicy = policyWrite;
        state.AppliedRevision = command.DesiredRevision;
        state.AppliedKind = DeviceAccessProviderCommandKind.Apply;
        await locked.SaveAsync(cancellationToken);
        return new DeviceAccessProviderReceipt(command.CommandId, command.DesiredRevision, command.Kind);
    }

    private async ValueTask<DeviceAccessProviderReceipt> ExecuteRevokeAsync(
        DeviceAccessProviderCommand command,
        CancellationToken cancellationToken)
    {
        await using var locked = await _states.LockAsync(command.DeviceId, cancellationToken);
        var state = await FenceAsync(locked, command, cancellationToken);
        if (state.HighestRevision != command.DesiredRevision || state.HighestKind != DeviceAccessProviderCommandKind.Revoke)
            throw new NetBirdRevisionRejectedException("A newer NetBird command superseded this revoke.");

        await ReconcileRevokeAsync(locked, state, cancellationToken);
        state.AppliedRevision = command.DesiredRevision;
        state.AppliedKind = DeviceAccessProviderCommandKind.Revoke;
        await locked.SaveAsync(cancellationToken);
        return new DeviceAccessProviderReceipt(command.CommandId, command.DesiredRevision, command.Kind);
    }

    private async ValueTask<NetBirdProviderState> FenceAsync(
        LockedNetBirdState locked,
        DeviceAccessProviderCommand command,
        CancellationToken cancellationToken)
    {
        var state = locked.State;
        if (state is null)
        {
            state = new NetBirdProviderState
            {
                DeviceId = command.DeviceId,
                UserId = command.UserId,
                CompanyId = command.CompanyId,
                HighestRevision = command.DesiredRevision,
                HighestKind = command.Kind,
            };
            locked.State = state;
            await locked.SaveAsync(cancellationToken);
            return state;
        }
        if (!string.Equals(state.UserId, command.UserId, StringComparison.Ordinal) ||
            !string.Equals(state.CompanyId, command.CompanyId, StringComparison.Ordinal))
            throw new NetBirdRevisionRejectedException("NetBird device ownership changed for an existing durable identity.");

        if (command.DesiredRevision < state.HighestRevision ||
            (command.DesiredRevision == state.HighestRevision && command.Kind != state.HighestKind))
        {
            if (state.HighestKind == DeviceAccessProviderCommandKind.Revoke)
                await ReconcileRevokeAsync(locked, state, cancellationToken);
            throw new NetBirdRevisionRejectedException("A stale or conflicting NetBird command was rejected by the durable fence.");
        }
        if (command.DesiredRevision > state.HighestRevision)
        {
            state.HighestRevision = command.DesiredRevision;
            state.HighestKind = command.Kind;
            state.AppliedKind = null;
            state.AppliedPolicy = null;
            await locked.SaveAsync(cancellationToken);
        }
        return state;
    }

    private async ValueTask EnsureStableSetupKeyAsync(
        LockedNetBirdState locked,
        NetBirdProviderState state,
        string groupId,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var autoGroups = GetSetupKeyAutoGroups(groupId);
        if (state.SetupKeyId is not null)
        {
            if (state.ProtectedSetupKey is null || state.SetupKeyExpiresAtUtc is null || state.SetupKeyExpiresAtUtc <= now)
                throw new NetBirdPeerNotReadyException("The original NetBird bootstrap is no longer usable; automatic replacement is denied.");
            var current = await _management.GetSetupKeyAsync(state.SetupKeyId, cancellationToken);
            if (current is null || current.Revoked)
                throw new NetBirdPeerNotReadyException("The original NetBird bootstrap was removed or revoked; automatic replacement is denied.");
            if (!SameGroupSet(current.AutoGroups, autoGroups))
                throw new NetBirdPeerNotReadyException("The original NetBird bootstrap groups differ from the configured enrollment groups; automatic replacement is denied.");
            return;
        }

        var keyName = ResourceName("connector-key", state.DeviceId);
        await _management.RevokeSetupKeysNamedAsync(keyName, cancellationToken);
        var created = await _management.CreateSetupKeyAsync(keyName, autoGroups, cancellationToken);
        if (string.IsNullOrWhiteSpace(created.Key) || created.Revoked || !created.Valid || !SameGroupSet(created.AutoGroups, autoGroups))
            throw new InvalidOperationException("NetBird did not confirm the one-off isolated setup key.");
        state.SetupKeyId = created.Id;
        state.ProtectedSetupKey = _states.ProtectSetupKey(created.Key);
        state.SetupKeyExpiresAtUtc = created.Expires;
        await locked.SaveAsync(cancellationToken);
    }

    private IReadOnlyList<string> GetSetupKeyAutoGroups(string deviceGroupId) =>
        _options.DnsDistributionGroupId is { } dnsGroupId
            ? [deviceGroupId, dnsGroupId]
            : [deviceGroupId];

    private static bool SameGroupSet(IReadOnlyList<string> actual, IReadOnlyList<string> expected) =>
        actual.Count == expected.Count &&
        actual.Distinct(StringComparer.Ordinal).Count() == expected.Count &&
        expected.All(groupId => actual.Contains(groupId, StringComparer.Ordinal));

    private async ValueTask<NetBirdPeer> RequireReadyPeerAsync(string peerId, CancellationToken cancellationToken)
    {
        var peer = await _management.GetPeerAsync(peerId, cancellationToken)
            ?? throw new NetBirdPeerNotReadyException("The expected NetBird peer does not exist.");
        if (!peer.Connected || peer.LastSeen < _timeProvider.GetUtcNow().Subtract(_options.MaximumPeerLastSeenAge) ||
            NormalizeAssignedAddress(peer.Ip) is null && NormalizeAssignedAddress(peer.Ipv6) is null)
            throw new NetBirdPeerNotReadyException("The expected NetBird peer is not connected with a fresh assigned overlay address.");
        return peer;
    }

    private NetBirdPolicyWrite BuildPolicy(DeviceAccessProviderCommand command, string sourceGroupId)
    {
        var matched = _options.AccessBindings.Where(binding => Matches(binding, command)).ToArray();
        foreach (var product in command.Modules.Select(module => module.Product).Distinct())
        {
            if (!matched.Any(binding => binding.Product == product))
                throw new InvalidOperationException($"No NetBird destination binding covers product {product}.");
        }
        if (matched.Length == 0)
            throw new InvalidOperationException("No NetBird access binding matches the current device grants.");

        var rules = matched.Select((binding, index) => new NetBirdPolicyRuleWrite(
            "grant-" + (index + 1),
            "Connector-managed least-privilege destination.",
            true,
            "accept",
            binding.Bidirectional,
            binding.Protocol,
            binding.Ports.Select(port => port.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray(),
            [sourceGroupId],
            [binding.DestinationGroupId])).ToArray();
        var name = ResourceName("connector-policy", command.DeviceId);
        return new NetBirdPolicyWrite(name, "Connector-managed device access; revision " + command.DesiredRevision, true, [], rules);
    }

    private static bool Matches(ValidatedNetBirdAccessBinding binding, DeviceAccessProviderCommand command)
    {
        var moduleMatch = command.Modules.Any(module =>
            (binding.Product is null || module.Product == binding.Product) &&
            (binding.ModuleId is null || string.Equals(module.ModuleId, binding.ModuleId, StringComparison.Ordinal)));
        var selectsModule = binding.Product is not null || binding.ModuleId is not null;
        var resourceMatch = command.Resources.Any(resource =>
            (binding.ResourceKind is null || string.Equals(resource.ResourceKind, binding.ResourceKind, StringComparison.Ordinal)) &&
            (binding.ResourceId is null || string.Equals(resource.ResourceId, binding.ResourceId, StringComparison.Ordinal)));
        var selectsResource = binding.ResourceKind is not null || binding.ResourceId is not null;
        return (!selectsModule || moduleMatch) && (!selectsResource || resourceMatch);
    }

    private async ValueTask ConfirmPolicyAsync(string policyId, NetBirdPolicyWrite expected, CancellationToken cancellationToken)
    {
        var actual = await _management.GetPolicyAsync(policyId, cancellationToken)
            ?? throw new InvalidOperationException("NetBird did not retain the device policy.");
        if (!PolicyMatches(actual, expected))
            throw new InvalidOperationException("NetBird policy confirmation does not match the requested access.");
    }

    private bool IsOwnedReadyState(
        NetBirdProviderState state,
        NetBirdGroup? group,
        NetBirdPolicy? policy,
        NetBirdPeer? peer,
        DateTimeOffset now)
    {
        var currentIpv4 = peer is null ? null : NormalizeAssignedAddress(peer.Ip);
        var currentIpv6 = peer is null ? null : NormalizeAssignedAddress(peer.Ipv6);
        if (group is null || policy is null || peer is null ||
            !string.Equals(group.Name, ResourceName("connector-device", state.DeviceId), StringComparison.Ordinal) ||
            group.Peers is null || group.Peers.Count != 1 ||
            !string.Equals(group.Peers[0].Id, state.PeerId, StringComparison.Ordinal) ||
            !string.Equals(peer.Id, state.PeerId, StringComparison.Ordinal) ||
            state.AppliedPolicy is null || !PolicyMatches(policy, state.AppliedPolicy) ||
            !peer.Connected || peer.LastSeen < now.Subtract(_options.MaximumPeerLastSeenAge) ||
            (currentIpv4 is null && currentIpv6 is null) ||
            !string.Equals(currentIpv4, state.PeerIpv4, StringComparison.Ordinal) ||
            !string.Equals(currentIpv6, state.PeerIpv6, StringComparison.Ordinal))
            return false;

        return state.AppliedPolicy.Rules.Count > 0 && state.AppliedPolicy.Rules.All(rule =>
            rule.Enabled && string.Equals(rule.Action, "accept", StringComparison.Ordinal) &&
            rule.Sources.Count == 1 && string.Equals(rule.Sources[0], group.Id, StringComparison.Ordinal) &&
            rule.Destinations.Count > 0);
    }

    private static bool PolicyMatches(NetBirdPolicy actual, NetBirdPolicyWrite expected)
    {
        if (!actual.Enabled || !string.Equals(actual.Name, expected.Name, StringComparison.Ordinal) ||
            !string.Equals(actual.Description ?? string.Empty, expected.Description, StringComparison.Ordinal) ||
            !(actual.SourcePostureChecks ?? []).Order(StringComparer.Ordinal)
                .SequenceEqual(expected.SourcePostureChecks.Order(StringComparer.Ordinal)) ||
            actual.Rules.Count != expected.Rules.Count)
            return false;

        var actualRules = actual.Rules.OrderBy(rule => rule.Name, StringComparer.Ordinal).ToArray();
        var expectedRules = expected.Rules.OrderBy(rule => rule.Name, StringComparer.Ordinal).ToArray();
        for (var index = 0; index < expectedRules.Length; index++)
        {
            var observed = actualRules[index];
            var wanted = expectedRules[index];
            if (!string.Equals(observed.Name, wanted.Name, StringComparison.Ordinal) ||
                observed.Enabled != wanted.Enabled ||
                !string.Equals(observed.Action, wanted.Action, StringComparison.Ordinal) ||
                observed.Bidirectional != wanted.Bidirectional ||
                !string.Equals(observed.Protocol, wanted.Protocol, StringComparison.Ordinal) ||
                !SameReferences(observed.Sources, wanted.Sources) ||
                !SameReferences(observed.Destinations, wanted.Destinations) ||
                !(observed.Ports ?? []).Order(StringComparer.Ordinal)
                    .SequenceEqual(wanted.Ports.Order(StringComparer.Ordinal)))
                return false;
        }
        return true;
    }

    private async ValueTask ReconcileRevokeAsync(
        LockedNetBirdState locked,
        NetBirdProviderState state,
        CancellationToken cancellationToken)
    {
        var policy = state.PolicyId is null
            ? await _management.FindPolicyByNameAsync(ResourceName("connector-policy", state.DeviceId), cancellationToken)
            : await _management.GetPolicyAsync(state.PolicyId, cancellationToken);
        if (policy is not null) await _management.DeletePolicyAsync(policy.Id, cancellationToken);

        if (state.SetupKeyId is not null)
            await _management.RevokeSetupKeyAsync(state.SetupKeyId, cancellationToken);
        await _management.RevokeSetupKeysNamedAsync(ResourceName("connector-key", state.DeviceId), cancellationToken);

        var group = state.BootstrapGroupId is null
            ? await _management.FindGroupByNameAsync(ResourceName("connector-device", state.DeviceId), cancellationToken)
            : await _management.GetGroupAsync(state.BootstrapGroupId, cancellationToken);
        var peerIds = new HashSet<string>(StringComparer.Ordinal);
        if (state.PeerId is not null) peerIds.Add(state.PeerId);
        foreach (var peer in group?.Peers ?? []) peerIds.Add(peer.Id);
        foreach (var peerId in peerIds) await _management.DeletePeerAsync(peerId, cancellationToken);
        if (group is not null) await _management.DeleteGroupAsync(group.Id, cancellationToken);

        if (policy is not null && await _management.GetPolicyAsync(policy.Id, cancellationToken) is not null)
            throw new InvalidOperationException("NetBird policy revoke was not confirmed.");
        foreach (var peerId in peerIds)
        {
            if (await _management.GetPeerAsync(peerId, cancellationToken) is not null)
                throw new InvalidOperationException("NetBird peer revoke was not confirmed.");
        }
        if (group is not null && await _management.GetGroupAsync(group.Id, cancellationToken) is not null)
            throw new InvalidOperationException("NetBird device group deletion was not confirmed.");

        state.PolicyId = null;
        state.AppliedPolicy = null;
        state.PeerId = null;
        state.PeerIpv4 = null;
        state.PeerIpv6 = null;
        state.BootstrapGroupId = null;
        state.SetupKeyId = null;
        state.ProtectedSetupKey = null;
        state.SetupKeyExpiresAtUtc = null;
        await locked.SaveAsync(cancellationToken);
    }

    private async ValueTask RevokeSetupKeyAndForgetSecretAsync(
        LockedNetBirdState locked,
        NetBirdProviderState state,
        CancellationToken cancellationToken)
    {
        if (state.SetupKeyId is not null)
            await _management.RevokeSetupKeyAsync(state.SetupKeyId, cancellationToken);
        state.ProtectedSetupKey = null;
        state.SetupKeyExpiresAtUtc = null;
        await locked.SaveAsync(cancellationToken);
    }

    private static DeviceAccessProviderCommand Validate(DeviceAccessProviderCommand command, DeviceAccessProviderCommandKind expected)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Kind != expected || !string.Equals(command.ProviderName, Name, StringComparison.OrdinalIgnoreCase) ||
            command.DesiredRevision < 1 || string.IsNullOrWhiteSpace(command.CommandId) ||
            string.IsNullOrWhiteSpace(command.DeviceId) || string.IsNullOrWhiteSpace(command.UserId) || string.IsNullOrWhiteSpace(command.CompanyId))
            throw new ArgumentException("The NetBird provider command is invalid or targets another provider.", nameof(command));
        return command;
    }

    private static string ResourceName(string prefix, string deviceId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(deviceId));
        return prefix + "-" + Convert.ToHexString(digest.AsSpan(0, 10)).ToLowerInvariant();
    }

    private static string? NormalizeAssignedAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var addressText = value.Split('/', 2)[0];
        return IPAddress.TryParse(addressText, out var address) && !IPAddress.IsLoopback(address) &&
               !address.Equals(IPAddress.Any) && !address.Equals(IPAddress.IPv6Any)
            ? address.ToString()
            : null;
    }

    private static bool SameReferences(IReadOnlyList<JsonElement>? actual, IReadOnlyList<string> expected)
    {
        var ids = (actual ?? []).Select(element => element.ValueKind switch
        {
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Object when element.TryGetProperty("id", out var id) => id.GetString(),
            _ => null,
        }).Where(id => id is not null).Cast<string>().Order().ToArray();
        return ids.SequenceEqual(expected.Order());
    }
}
