// Shared-secret store для X-Bridge-Token. Хранится в %LOCALAPPDATA%\Platform\Bridge\token.dat,
// шифруется DPAPI (CurrentUser scope) — его может прочитать только тот же
// пользователь Windows на том же ПК. См. plan §10.2.
//
// Если token.dat нет — Bridge.Desktop при старте создаёт случайный 256-bit
// токен, шифрует, пишет на диск. Connector на этом же ПК тоже читает этот
// файл (CurrentUser DPAPI работает потому что они оба под тем же логином).

#nullable enable

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Platform.Bridge.Desktop.Tekla.Auth
{
    public sealed class BridgeTokenStore
    {
        public static string DefaultDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                         "Platform", "Bridge");

        public static string DefaultPath => Path.Combine(DefaultDir, "token.dat");

        /// <summary>Получить токен; создать если файла нет.</summary>
        public static string LoadOrCreate(string? path = null)
        {
            path ??= DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            if (File.Exists(path))
            {
                var enc = File.ReadAllBytes(path);
                if (enc.Length > 0)
                {
                    var dec = ProtectedData.Unprotect(enc, optionalEntropy: null, DataProtectionScope.CurrentUser);
                    return Encoding.UTF8.GetString(dec);
                }
            }

            // Generate fresh token.
            var bytes = new byte[32];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            var token = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

            var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(path, protectedBytes);
            return token;
        }

        /// <summary>Сравнить пришедший токен с сохранённым; constant-time.</summary>
        public static bool Verify(string presented, string expected)
        {
            if (presented is null || expected is null) return false;
            if (presented.Length != expected.Length) return false;
            int diff = 0;
            for (int i = 0; i < presented.Length; i++) diff |= presented[i] ^ expected[i];
            return diff == 0;
        }
    }
}
