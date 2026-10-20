namespace Connector.Network;

/// <summary>The single pre-existing public SMB address retained for Brigada.
/// This is an encrypted SMB exception, not an overlay socket guarantee.</summary>
public static class LegacyEncryptedSmbPolicy
{
    public const string ShareUnc = @"\\62.113.36.107\BIM_Models";
    public const string Destination = "62.113.36.107";

    public static IReadOnlyList<string> Validate(IEnumerable<string> shares)
    {
        ArgumentNullException.ThrowIfNull(shares);
        var values = shares.ToArray();
        if (values.Length > 1 || values.Any(value =>
                !string.Equals(value, ShareUnc, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Only the exact legacy encrypted SMB share is supported.", nameof(shares));
        return values.Length == 0 ? Array.Empty<string>() : Array.AsReadOnly(new[] { ShareUnc });
    }
}
