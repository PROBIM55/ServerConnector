namespace Connector.Access;

using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Connector.Access.Contracts;

public sealed class DeviceAccessAdministrationService
{
    private readonly DbDeviceAccessRepository _repository;
    private readonly DbProviderOutboxRepository _outbox;
    private readonly IPlatformAccessDirectory _directory;
    private readonly DeviceAccessOptions _options;
    private readonly TimeProvider _timeProvider;

    internal DeviceAccessAdministrationService(
        DbDeviceAccessRepository repository,
        DbProviderOutboxRepository outbox,
        IPlatformAccessDirectory directory,
        Microsoft.Extensions.Options.IOptions<DeviceAccessOptions> options,
        TimeProvider timeProvider)
    {
        _repository = repository;
        _outbox = outbox;
        _directory = directory;
        _options = options.Value;
        _timeProvider = timeProvider;
    }

    public async ValueTask<DeviceAccessResult<bool>> RevokeAsync(
        RevokeDeviceCommand command,
        CancellationToken cancellationToken = default)
    {
        var device = await _repository.FindByDeviceIdAsync(command.DeviceId, cancellationToken);
        if (device is null || device.CompanyId != command.CompanyId)
        {
            return DeviceAccessResult<bool>.Fail("device_not_found", "The device is not available in this company.");
        }

        var access = await _directory.AuthorizeAsync(
            command.ActorUserId,
            device.UserId,
            command.CompanyId,
            PlatformAccessOperation.RevokeDevice,
            cancellationToken);
        if (access is null || access.UserId != device.UserId || access.CompanyId != device.CompanyId ||
            !access.UserIsActive || !access.MembershipIsActive || !access.CompanyIsActive || !access.IsAuthorized)
        {
            return DeviceAccessResult<bool>.Fail("access_denied", "Current platform permissions deny device revocation.");
        }

        return await _outbox.RevokeAndEnqueueAsync(
                device,
                _options.RequiredAccessProviders.ToArray(),
                _timeProvider.GetUtcNow(),
                cancellationToken)
            ? DeviceAccessResult<bool>.Success(true)
            : DeviceAccessResult<bool>.Fail("already_revoked", "The device is already revoked.");
    }

    public async ValueTask<DeviceAccessResult<IReadOnlyList<DeviceAccessAdminDevice>>> ListAsync(
        string actorUserId,
        string companyId,
        CancellationToken cancellationToken = default)
    {
        var access = await _directory.AuthorizeAsync(
            actorUserId, actorUserId, companyId, PlatformAccessOperation.ListDevices, cancellationToken);
        if (access is null || !access.UserIsActive || !access.MembershipIsActive ||
            !access.CompanyIsActive || !access.IsAuthorized || access.CompanyId != companyId)
        {
            return DeviceAccessResult<IReadOnlyList<DeviceAccessAdminDevice>>.Fail(
                "access_denied", "Current platform permissions deny device listing.");
        }

        var stored = await _repository.ListByCompanyAsync(companyId, cancellationToken);
        var result = new List<DeviceAccessAdminDevice>(stored.Count);
        foreach (var row in stored)
        {
            var states = await _outbox.ReadStatesAsync(row.Device.DeviceId, cancellationToken);
            DateTimeOffset? notBefore = null;
            if (!string.IsNullOrWhiteSpace(row.Device.CertificatePem))
            {
                try
                {
                    using var certificate = X509Certificate2.CreateFromPem(row.Device.CertificatePem);
                    notBefore = new DateTimeOffset(certificate.NotBefore.ToUniversalTime());
                }
                catch (CryptographicException) { }
            }

            result.Add(new DeviceAccessAdminDevice(
                row.Device.DeviceId,
                row.Device.UserId,
                row.Device.CompanyId,
                row.DisplayName,
                row.Device.EnrollmentStatus,
                row.Device.DesiredRevision,
                row.Device.AppliedRevision,
                states.Select(state => new DeviceAccessProviderStatus(
                    state.ProviderName, state.DesiredRevision, state.AppliedRevision,
                    state.DesiredAction, state.Status)).ToArray(),
                notBefore,
                row.Device.CertificateExpiresAtUtc,
                null,
                row.CreatedAtUtc,
                row.UpdatedAtUtc));
        }

        return DeviceAccessResult<IReadOnlyList<DeviceAccessAdminDevice>>.Success(result);
    }
}
