#nullable enable

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Tekla.Structures.Model;

namespace Platform.Bridge.Desktop.Tekla.ConstructiveRuntime
{
    public sealed class TeklaModelIdentity
    {
        private TeklaModelIdentity(
            string teklaVersion,
            string modelName,
            string modelPath,
            string fingerprint)
        {
            TeklaVersion = teklaVersion;
            ModelName = modelName;
            ModelPath = modelPath;
            Fingerprint = fingerprint;
        }

        public string TeklaVersion { get; }
        public string ModelName { get; }
        public string ModelPath { get; }
        public string Fingerprint { get; }

        public static TeklaModelIdentity Capture(Model model, string teklaVersion)
        {
            if (model is null) throw new ArgumentNullException(nameof(model));
            var info = model.GetInfo();
            var name = (info?.ModelName ?? string.Empty).Trim();
            var path = NormalizePath(info?.ModelPath);
            return new TeklaModelIdentity(
                teklaVersion,
                name,
                path,
                ComputeFingerprint(teklaVersion, name, path));
        }

        public void RequireMatch(
            string expectedFingerprint,
            string expectedTeklaVersion,
            string expectedModelName,
            string expectedModelPath)
        {
            if (!string.Equals(Major(TeklaVersion), Major(expectedTeklaVersion), StringComparison.Ordinal))
            {
                throw new TeklaNativeExecutionException(
                    "TEKLA_PLAN_TEKLA_VERSION_MISMATCH",
                    $"Connected bridge targets Tekla {TeklaVersion}, expected {expectedTeklaVersion}.");
            }

            var normalizedExpectedPath = NormalizePath(expectedModelPath);
            if (!string.Equals(ModelName, expectedModelName.Trim(), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(ModelPath, normalizedExpectedPath, StringComparison.OrdinalIgnoreCase))
            {
                throw new TeklaNativeExecutionException(
                    "TEKLA_PLAN_MODEL_IDENTITY_MISMATCH",
                    $"Connected model is '{ModelName}' at '{ModelPath}', expected " +
                    $"'{expectedModelName}' at '{normalizedExpectedPath}'.");
            }

            var expected = ComputeFingerprint(expectedTeklaVersion, expectedModelName.Trim(), normalizedExpectedPath);
            if (!string.Equals(Fingerprint, expectedFingerprint.Trim(), StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Fingerprint, expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new TeklaNativeExecutionException(
                    "TEKLA_PLAN_MODEL_FINGERPRINT_MISMATCH",
                    "The requested model fingerprint does not match the connected Tekla model.");
            }
        }

        public static string ComputeFingerprint(string teklaVersion, string modelName, string modelPath)
        {
            using var hash = SHA256.Create();
            var payload = Encoding.UTF8.GetBytes(
                Major(teklaVersion) + "\n" +
                modelName.Trim().ToUpperInvariant() + "\n" +
                NormalizePath(modelPath).ToUpperInvariant());
            var digest = hash.ComputeHash(payload);
            var result = new StringBuilder(digest.Length * 2);
            foreach (var item in digest) result.Append(item.ToString("x2"));
            return "TM1:" + result;
        }

        private static string Major(string? value)
        {
            var normalized = (value ?? string.Empty).Trim();
            var separator = normalized.IndexOf('.');
            return separator < 0 ? normalized : normalized.Substring(0, separator);
        }

        private static string NormalizePath(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            try
            {
                return Path.GetFullPath(value!.Trim())
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            catch
            {
                return value!.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }
    }
}
