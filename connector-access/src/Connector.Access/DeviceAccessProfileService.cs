using Connector.Access.Contracts;
using Microsoft.Extensions.Options;

namespace Connector.Access;

public sealed class DeviceAccessProfileService
{
    private readonly DbDeviceAccessRepository _repository;
    private readonly DbProviderOutboxRepository _outbox;
    private readonly IPlatformAccessDirectory _directory;
    private readonly DeviceAccessOptions _options;
    private readonly TimeProvider _timeProvider;

    internal DeviceAccessProfileService(
        DbDeviceAccessRepository repository,
        DbProviderOutboxRepository outbox,
        IPlatformAccessDirectory directory,
        IOptions<DeviceAccessOptions> options,
        TimeProvider timeProvider)
    {
        _repository = repository;
        _outbox = outbox;
        _directory = directory;
        _options = options.Value;
        _timeProvider = timeProvider;
    }

    public async ValueTask<DeviceAccessResult<DeviceAccessProfile>> GetProfileAsync(
        AuthenticatedDevice authenticatedDevice,
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        var stored = await _repository.FindByDeviceIdAsync(authenticatedDevice.DeviceId, cancellationToken);
        if (stored is null || stored.UserId != authenticatedDevice.UserId ||
            stored.CompanyId != authenticatedDevice.CompanyId || stored.RevokedAtUtc is not null ||
            stored.CertificateExpiresAtUtc is null || stored.CertificateExpiresAtUtc <= now ||
            stored.CertificateSha256 != authenticatedDevice.CertificateSha256)
        {
            return DeviceAccessResult<DeviceAccessProfile>.Fail("device_not_active", "The device is not active.");
        }

        var access = await _directory.AuthorizeAsync(
            stored.UserId,
            stored.UserId,
            stored.CompanyId,
            PlatformAccessOperation.ReadAccessProfile,
            cancellationToken);
        if (access is null || access.UserId != stored.UserId || access.CompanyId != stored.CompanyId ||
            !access.UserIsActive || !access.MembershipIsActive || !access.CompanyIsActive || !access.IsAuthorized)
        {
            return DeviceAccessResult<DeviceAccessProfile>.Fail("access_denied", "Current platform grants deny access.");
        }

        if (_options.RequiredAccessProviders.Count == 0 ||
            _options.RequiredAccessProviders.Any(string.IsNullOrWhiteSpace))
        {
            return DeviceAccessResult<DeviceAccessProfile>.Fail(
                "provider_configuration",
                "At least one named access provider is required before grants can be returned.");
        }

        if (access.AccessRevision < stored.DesiredRevision)
        {
            return DeviceAccessResult<DeviceAccessProfile>.Fail(
                "access_revision_regressed",
                "Platform access revision moved backwards; grants remain blocked.");
        }

        var durableStates = await _outbox.EnsureApplyDesiredAsync(
            stored,
            access,
            _options.RequiredAccessProviders.ToArray(),
            now,
            cancellationToken);
        var states = durableStates.ToDictionary(state => state.ProviderName, StringComparer.OrdinalIgnoreCase);
        var requiredStates = _options.RequiredAccessProviders
            .Where(states.ContainsKey)
            .Select(name => states[name])
            .ToArray();

        var appliedRevision = requiredStates.Length == 0 ? 0 : requiredStates.Min(state => state.AppliedRevision);
        if (requiredStates.Length != _options.RequiredAccessProviders.Count || requiredStates.Any(
                state => state.DesiredAction != "apply" || state.Status != "applied" ||
                         state.DesiredRevision != access.AccessRevision || state.AppliedRevision < access.AccessRevision))
        {
            return DeviceAccessResult<DeviceAccessProfile>.Fail("access_pending", "Access grants are not confirmed by every required provider.");
        }

        await _repository.SetAccessRevisionsAsync(
            stored.DeviceId,
            access.AccessRevision,
            appliedRevision,
            now,
            cancellationToken);

        return DeviceAccessResult<DeviceAccessProfile>.Success(
            new DeviceAccessProfile(
                DeviceAccessProtocol.Version,
                stored.DeviceId,
                stored.UserId,
                stored.CompanyId,
                access.AccessRevision,
                appliedRevision,
                new[] { stored.CertificateExpiresAtUtc.Value, now.Add(_options.AccessProfileLifetime) }.Min(),
                access.Modules,
                access.Resources));
    }
}
