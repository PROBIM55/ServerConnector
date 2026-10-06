using Connector.Access.Client;
using Connector.Network;
using Connector.Upgrade.Core;
using Connector.Upgrade.HelperReleaseTrust;
using Connector.Upgrade.MachineCommandChannel;
using Connector.Upgrade.MachineDispatcher;
using Connector.Upgrade.MachineIpc;
using Connector.Upgrade.MachineJournal;
using Connector.Upgrade.NetBirdPackageStage;
using Connector.Upgrade.NetBirdWindowsState;
using Connector.Upgrade.OriginalUserCaller;
using Connector.Upgrade.OriginalUserMachineAdapter;
using Connector.Upgrade.OriginalUserSession;
using Connector.Upgrade.PlatformAccess;
using Connector.Upgrade.Velopack;
using Connector.Upgrade.VelopackPayload;
using Connector.Upgrade.WindowsEnvironment;
using Connector.Upgrade.WindowsHost;
using Connector.Upgrade.WindowsUserStage;
using Platform.Connector.Core;
using System.Security.Principal;
using VelopackApplicationSetupPin = Connector.Upgrade.Velopack.VelopackSetupPin;

namespace Connector.Upgrade.WindowsComposition;

public static class WindowsUpgradeCompositionFactory
{
    private const string PeerAttestationServiceId = "peer-attestation";
    public static async ValueTask<WindowsUpgradeCompositionPreparation> PrepareForCurrentUserAsync(
        WindowsUpgradeCompositionOptions options,
        WindowsUpgradeRuntimeBindings bindings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Host);
        ArgumentNullException.ThrowIfNull(bindings);
        ArgumentNullException.ThrowIfNull(bindings.CommonAccessClient);
        ArgumentNullException.ThrowIfNull(bindings.Overlay);
        ArgumentNullException.ThrowIfNull(bindings.ProtectedNetworkGate);
        ArgumentNullException.ThrowIfNull(bindings.RequestTransport);
        ArgumentNullException.ThrowIfNull(bindings.SmbDestinations);

        options.Host.Session.Validate();
        var structuralBlockers = InspectStructuralBlockers(bindings);
        if (structuralBlockers.Count != 0)
            return WindowsUpgradeCompositionPreparation.Blocked(structuralBlockers.ToArray());
        if (!OperatingSystem.IsWindows())
            return WindowsUpgradeCompositionPreparation.Blocked(new WindowsUpgradeCompositionBlocker(
                WindowsUpgradeCompositionBlockerCode.WindowsHostRequired, "original-user-windows-token-unavailable"));

        string currentSid;
        try
        {
            using var identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            currentSid = identity.User?.Value ?? string.Empty;
        }
        catch (Exception)
        {
            return WindowsUpgradeCompositionPreparation.Blocked(new WindowsUpgradeCompositionBlocker(
                WindowsUpgradeCompositionBlockerCode.WindowsHostRequired, "original-user-token-unavailable"));
        }
        if (string.IsNullOrWhiteSpace(currentSid) ||
            !StringComparer.OrdinalIgnoreCase.Equals(currentSid, options.Host.Session.InitiatingUserSid))
            return WindowsUpgradeCompositionPreparation.Blocked(new WindowsUpgradeCompositionBlocker(
                WindowsUpgradeCompositionBlockerCode.MachineBoundaryNotReady, "original-user-session-sid-mismatch"));
        if (options.Host.MutationTimeout <= TimeSpan.Zero)
            return WindowsUpgradeCompositionPreparation.Blocked(new WindowsUpgradeCompositionBlocker(
                WindowsUpgradeCompositionBlockerCode.MachineBoundaryNotReady, "invalid-machine-command-timeout"));

        var httpClient = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            Timeout = TimeSpan.FromSeconds(10),
        };
        MachineCommandClient? machineClient = null;
        WindowsOriginalUserUpgradeSession? userSession = null;
        try
        {
            var schema = new HttpPlatformAccessSchemaProbe(httpClient, options.PlatformProfileUri);
            bool schemaAvailable;
            try { schemaAvailable = await schema.IsAvailableAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                return WindowsUpgradeCompositionPreparation.Blocked(new WindowsUpgradeCompositionBlocker(
                    WindowsUpgradeCompositionBlockerCode.PlatformAccessSchemaUnavailable,
                    "exact-platform-profile-probe-failed"));
            }
            if (!schemaAvailable)
                return WindowsUpgradeCompositionPreparation.Blocked(new WindowsUpgradeCompositionBlocker(
                    WindowsUpgradeCompositionBlockerCode.PlatformAccessSchemaUnavailable,
                    "exact-platform-profile-schema-unavailable"));

            // This read-only check is deliberately before the helper launch/UAC and before any token use.
            var revocation = new HttpExactDeviceRevocationPort((IConnectorExactDeviceRevocationClient)bindings.CommonAccessClient);
            ExactDeviceRevocationReadiness revokeReadiness;
            try { revokeReadiness = await revocation.InspectAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                return WindowsUpgradeCompositionPreparation.Blocked(new WindowsUpgradeCompositionBlocker(
                    WindowsUpgradeCompositionBlockerCode.ExactDeviceSelfRevocationUnavailable,
                    "device-self-revoke-probe-failed"));
            }
            if (!revokeReadiness.Available || string.IsNullOrWhiteSpace(revokeReadiness.EvidenceId))
                return WindowsUpgradeCompositionPreparation.Blocked(new WindowsUpgradeCompositionBlocker(
                    WindowsUpgradeCompositionBlockerCode.ExactDeviceSelfRevocationUnavailable,
                    "device-self-revoke-not-ready"));

            // Do not use the caller-provided source, signature policy, or Host setup pin as trust.
            VelopackApplicationSetupPin setupPin;
            VelopackSetupSignaturePolicy? signaturePolicy;
            try
            {
                var releaseSetup = new HelperReleasePinSource().GetVelopackSetupPin();
                if (!string.Equals(releaseSetup.PackageId, UnifiedVelopackApplication.PackId, StringComparison.Ordinal))
                    return WindowsUpgradeCompositionPreparation.Blocked(new WindowsUpgradeCompositionBlocker(
                        WindowsUpgradeCompositionBlockerCode.SignedReleasePinUnavailable,
                        "signed-setup-package-id-mismatch"));
                var trustMode = releaseSetup.TrustMode switch
                {
                    Connector.Upgrade.HelperLauncher.ReleaseArtifactTrustMode.Authenticode =>
                        VelopackSetupTrustMode.Authenticode,
                    Connector.Upgrade.HelperLauncher.ReleaseArtifactTrustMode.SignedManifestHash =>
                        VelopackSetupTrustMode.SignedManifestHash,
                    _ => throw new InvalidDataException("Unsupported signed release Setup trust mode."),
                };
                setupPin = new VelopackApplicationSetupPin(
                    releaseSetup.PackageId, releaseSetup.Version, releaseSetup.Size, releaseSetup.Sha256,
                    trustMode, releaseSetup.SignerThumbprint);
                signaturePolicy = trustMode == VelopackSetupTrustMode.Authenticode
                    ? new VelopackSetupSignaturePolicy(new HashSet<string>(
                        [NormalizeThumbprint(releaseSetup.SignerThumbprint!)], StringComparer.OrdinalIgnoreCase))
                    : null;
            }
            catch (Exception)
            {
                return WindowsUpgradeCompositionPreparation.Blocked(new WindowsUpgradeCompositionBlocker(
                    WindowsUpgradeCompositionBlockerCode.SignedReleasePinUnavailable,
                    "signed-release-setup-pin-unavailable"));
            }
            var setupStager = WindowsVelopackSetupStagerFactory.OpenExistingForOriginalUser(
                setupPin, signaturePolicy, currentSid);

            var operationId = Guid.ParseExact(options.Host.Session.OperationId, "N");
            var commandTimeout = options.Host.MutationTimeout > MachineCommandClient.MaximumTimeout
                ? MachineCommandClient.MaximumTimeout
                : options.Host.MutationTimeout;
            var coordinator = new CommonConnectorConnectionCoordinator(
                bindings.CommonAccessClient, bindings.Overlay, bindings.ProtectedNetworkGate);
            var accessSession = new CommonConnectorPlatformAccessSession(coordinator);
            var enrollment = new CompensatablePlatformEnrollmentPort(
                schema,
                revocation,
                bindings.CommonAccessClient,
                accessSession,
                options.ExpectedNetBirdManagementUri,
                options.DeviceDisplayName);
            var access = new CommonConnectorLiveAccessVerificationPort(
                bindings.CommonAccessClient,
                bindings.Overlay,
                accessSession,
                new NetBirdExactPeerBindingProbe(
                    (NetBirdCliClient)bindings.Overlay,
                    coordinator,
                    (HttpConnectorEnrollmentClient)bindings.CommonAccessClient),
                new WindowsSmbAccessProbe(
                    bindings.RequestTransport, bindings.SmbDestinations.Contains, bindings.Overlay));
            var environment = new WindowsHostEnvironmentPort(
                currentSid, schema, new SystemLegacyProcessProbe());
            machineClient = await new OriginalUserCallerTransport()
                .OpenAsync(operationId, commandTimeout, cancellationToken).ConfigureAwait(false);
            var readOnlyNetBird = new ReadOnlyWindowsHostNetBirdPort(
                token => InspectMachineOwnershipAsync(machineClient, commandTimeout, token));
            var hostOptions = options.Host with { VelopackSetupPin = setupPin };
            var userPorts = WindowsHostUpgradePorts.CreateForOriginalUser(
                hostOptions,
                environment,
                enrollment,
                access,
                readOnlyNetBird,
                setupStager,
                new WindowsUserStatePort(captureFromPrecreatedStage: true));
            var ports = new MachineCommandUpgradePorts(
                operationId, currentSid, userPorts, machineClient, commandTimeout);
            userSession = WindowsOriginalUserUpgradeSessionFactory.Create(options.Host.Session, ports);

            var composition = new WindowsUpgradeComposition(userSession, machineClient, httpClient);
            userSession = null;
            machineClient = null;
            httpClient = null!;
            return WindowsUpgradeCompositionPreparation.Ready(composition);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return WindowsUpgradeCompositionPreparation.Blocked(new WindowsUpgradeCompositionBlocker(
                WindowsUpgradeCompositionBlockerCode.MachineBoundaryNotReady,
                "authenticated-original-user-composition-unavailable"));
        }
        finally
        {
            userSession?.Dispose();
            machineClient?.Dispose();
            httpClient?.Dispose();
        }
    }

    private static async ValueTask<NetBirdOwnership> InspectMachineOwnershipAsync(
        MachineCommandClient client, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var request = new MachineIpcRequest(MachineIpcFrameCodec.CurrentVersion, Guid.NewGuid(), MachineIpcOperation.Inspect);
        var result = await client.SendAsync(request, timeout, cancellationToken).ConfigureAwait(false);
        if (result.Version != request.Version || result.CorrelationId != request.CorrelationId ||
            result.Operation != request.Operation || result.Status != MachineDispatcherStatus.Completed ||
            result.Code != MachineDispatcherCode.None || result.State != MachineUpgradeState.InProgress ||
            result.Phase != MachineUpgradePhase.AssessNetBird || result.NetBirdOwnership is not { } ownership ||
            !Enum.IsDefined(ownership))
            throw new InvalidDataException("Authenticated machine inspection returned no valid ownership-only assessment.");
        return ownership;
    }

    private static string NormalizeThumbprint(string value) =>
        string.Concat(value.Where(Uri.IsHexDigit)).ToUpperInvariant();

    private static IReadOnlyList<WindowsUpgradeCompositionBlocker> InspectStructuralBlockers(
        WindowsUpgradeRuntimeBindings bindings)
    {
        var blockers = new List<WindowsUpgradeCompositionBlocker>();
        if (bindings.Overlay is not NetBirdCliClient ||
            bindings.CommonAccessClient is not HttpConnectorEnrollmentClient ||
            bindings.ProtectedNetworkGate is not ProtectedServiceNetworkGate protectedGate ||
            !protectedGate.HasProtectedService(PeerAttestationServiceId))
        {
            blockers.Add(new WindowsUpgradeCompositionBlocker(
                WindowsUpgradeCompositionBlockerCode.ExactVpnPeerBindingUnavailable,
                "peer-id-to-local-wireguard-key-proof-unavailable"));
        }

        if (bindings.CommonAccessClient is not IConnectorDeviceAccessClient)
        {
            blockers.Add(new WindowsUpgradeCompositionBlocker(
                WindowsUpgradeCompositionBlockerCode.CommonAccessCredentialContractIncomplete,
                "common-access-client-missing-device-access"));
        }

        if (bindings.CommonAccessClient is not IConnectorExactDeviceRevocationClient)
        {
            blockers.Add(new WindowsUpgradeCompositionBlocker(
                WindowsUpgradeCompositionBlockerCode.ExactDeviceSelfRevocationUnavailable,
                "device-self-revoke-client-unavailable"));
        }

        return blockers.AsReadOnly();
    }

}
