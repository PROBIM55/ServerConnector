using System.Security.Cryptography;

namespace Connector.Access.Client;

public sealed partial class HttpConnectorEnrollmentClient
{
    private Guid? _confirmedRevokedRequestId;

    public async ValueTask<ConnectorSignOutIdentity?> GetSignOutIdentityAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        StoredEnrollmentState? state = null;
        try
        {
            state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (state is null) return null;
            ValidateBinding(state);
            if (state.Status == StoredEnrollmentStatus.Pending)
            {
                try
                {
                    var recovered = await ResumeStateAsync(state, cancellationToken).ConfigureAwait(false);
                    return new ConnectorSignOutIdentity(state.RequestId, recovered.DeviceId);
                }
                catch (ConnectorEnrollmentProtocolException exception) when (EnrollmentIsExplicitlyAbsent(exception))
                {
                    // The server proved this exact pending request never issued a device.
                    _confirmedRevokedRequestId = state.RequestId;
                    return new ConnectorSignOutIdentity(state.RequestId, null);
                }
            }
            return new ConnectorSignOutIdentity(state.RequestId, state.Response!.DeviceId);
        }
        finally
        {
            if (state is not null) CryptographicOperations.ZeroMemory(state.PrivateKeyPkcs8);
            _operationGate.Release();
        }
    }

    public async ValueTask<bool> ForgetRejectedEnrollmentAsync(CancellationToken cancellationToken = default)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        StoredEnrollmentState? state = null;
        try
        {
            state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (state is null) return true;
            ValidateBinding(state);
            // A pending request keeps its CSR for the next browser sign-in.
            if (state.Status != StoredEnrollmentStatus.Completed || state.Response is null) return false;
            using (var key = EnrollmentCryptography.LoadAndValidateKey(state))
            {
                if (await ProveEnrollmentKeyAsync(state, key, cancellationToken).ConfigureAwait(false) == KeyProof.Accepted)
                    return false;
                // One rejection can come from a proxy that briefly stopped passing
                // client certificates. Only a repeated rejection forgets the device.
                await Task.Delay(_rejectionConfirmationDelay, _options.TimeProvider, cancellationToken).ConfigureAwait(false);
                if (await ProveEnrollmentKeyAsync(state, key, cancellationToken).ConfigureAwait(false) == KeyProof.Accepted)
                    return false;
            }
            // Kept beside the store (not deleted) so a wrong server answer stays recoverable.
            await _store.ArchiveRejectedAsync(_options.TimeProvider.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            _confirmedRevokedRequestId = null;
            return true;
        }
        finally
        {
            if (state is not null) CryptographicOperations.ZeroMemory(state.PrivateKeyPkcs8);
            _operationGate.Release();
        }
    }

    public async ValueTask ClearRevokedSessionAsync(ConnectorSignOutIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        StoredEnrollmentState? state = null;
        try
        {
            state = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (state is null) return;
            ValidateBinding(state);
            if (_confirmedRevokedRequestId != identity.RequestId || state.RequestId != identity.RequestId ||
                !string.Equals(state.Response?.DeviceId, identity.DeviceId, StringComparison.Ordinal))
                throw new ConnectorEnrollmentStateException("The exact session has not been confirmed revoked.");
            await _store.DeleteAsync(cancellationToken).ConfigureAwait(false);
            _confirmedRevokedRequestId = null;
        }
        finally
        {
            if (state is not null) CryptographicOperations.ZeroMemory(state.PrivateKeyPkcs8);
            _operationGate.Release();
        }
    }
}
