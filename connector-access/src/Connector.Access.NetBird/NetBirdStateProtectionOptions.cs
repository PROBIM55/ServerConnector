namespace Connector.Access.NetBird;

public sealed class NetBirdStateProtectionOptions
{
    public required string DataProtectionKeyDirectory { get; init; }
    public required string DataProtectionCertificatePfxPath { get; init; }
    public required string DataProtectionCertificatePasswordEnvironmentVariable { get; init; }

    internal ValidatedNetBirdStateProtectionOptions Validate()
    {
        if (string.IsNullOrWhiteSpace(DataProtectionKeyDirectory) ||
            !Path.IsPathFullyQualified(DataProtectionKeyDirectory))
        {
            throw new ArgumentException("NetBird Data Protection key directory must be an absolute path.");
        }

        if (string.IsNullOrWhiteSpace(DataProtectionCertificatePfxPath) ||
            !Path.IsPathFullyQualified(DataProtectionCertificatePfxPath))
        {
            throw new ArgumentException("NetBird Data Protection certificate PFX path must be absolute.");
        }

        if (string.IsNullOrWhiteSpace(DataProtectionCertificatePasswordEnvironmentVariable) ||
            DataProtectionCertificatePasswordEnvironmentVariable.IndexOfAny(['=', '\0']) >= 0)
        {
            throw new ArgumentException("NetBird Data Protection certificate password environment variable name is invalid.");
        }

        return new ValidatedNetBirdStateProtectionOptions(
            Path.GetFullPath(DataProtectionKeyDirectory),
            Path.GetFullPath(DataProtectionCertificatePfxPath),
            DataProtectionCertificatePasswordEnvironmentVariable);
    }
}

internal sealed record ValidatedNetBirdStateProtectionOptions(
    string DataProtectionKeyDirectory,
    string DataProtectionCertificatePfxPath,
    string DataProtectionCertificatePasswordEnvironmentVariable);
