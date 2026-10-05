using Connector.Upgrade.Core;
using Connector.Upgrade.NetBirdMachine;

namespace Connector.Upgrade.NetBirdPackageStage;

/// <summary>Windows implementation for staging a pinned official NetBird MSI. It never elevates or runs msiexec.</summary>
public static class WindowsVerifiedNetBirdPackageStagerFactory
{
    public static IVerifiedNetBirdPackageStager Create(string? machineStagingRoot = null) =>
        new WindowsVerifiedNetBirdPackageStager(machineStagingRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "StructuraConnectorInstaller", "NetBirdPackages"));
}

internal sealed record NetBirdMsiProperties(
    Guid ProductCode,
    Guid UpgradeCode,
    string ProductVersion,
    string Manufacturer,
    string ProductName);

internal sealed record AuthenticodeSigner(string Subject, string Thumbprint);

internal interface INetBirdMsiPropertyReader
{
    NetBirdMsiProperties Read(string path);
}

internal interface IAuthenticodeSignerVerifier
{
    AuthenticodeSigner VerifyTrusted(string path);
}

internal interface INetBirdMachineStagingSecurity
{
    string PrepareRoot(string requestedRoot);
    void CreateProtectedDirectory(string path);
    void ProtectAndValidateFile(string path);
    void ValidateProtectedDirectory(string path);
    void ValidateProtectedFile(string path);
}

internal sealed record VerifiedNetBirdPackageInspection(
    NetBirdMsiPackageIdentity Package,
    long SizeBytes,
    string Sha256,
    string SignerSubject,
    string SignerThumbprint);
