using System.Security.Cryptography;

namespace Connector.Access;

internal readonly record struct ParsedEnrollmentToken(Guid TokenId, byte[] Hash);

internal static class EnrollmentTokenCodec
{
    private const string Prefix = "cea1";

    public static (Guid TokenId, string Token, byte[] Hash) Create()
    {
        var tokenId = Guid.NewGuid();
        var secret = RandomNumberGenerator.GetBytes(32);
        var encodedSecret = Base64UrlEncode(secret);
        var token = $"{Prefix}.{tokenId:N}.{encodedSecret}";
        return (tokenId, token, SHA256.HashData(secret));
    }

    public static bool TryParse(string? token, out ParsedEnrollmentToken parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var parts = token.Split('.', StringSplitOptions.None);
        if (parts.Length != 3 || parts[0] != Prefix ||
            !Guid.TryParseExact(parts[1], "N", out var tokenId))
        {
            return false;
        }

        try
        {
            var secret = Base64UrlDecode(parts[2]);
            if (secret.Length != 32)
            {
                return false;
            }

            parsed = new ParsedEnrollmentToken(tokenId, SHA256.HashData(secret));
            CryptographicOperations.ZeroMemory(secret);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized += (normalized.Length % 4) switch { 2 => "==", 3 => "=", _ => string.Empty };
        return Convert.FromBase64String(normalized);
    }
}
