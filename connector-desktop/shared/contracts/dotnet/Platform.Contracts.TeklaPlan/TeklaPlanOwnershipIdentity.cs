#nullable enable

using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Platform.Contracts.TeklaPlan
{
    /// <summary>
    /// Builds the stable native-object identity used for Tekla upsert. Revision,
    /// generation and content hashes are intentionally excluded: regenerating
    /// the same constructive command must address the same native object.
    /// </summary>
    public static class TeklaPlanOwnershipIdentity
    {
        public const string Prefix = "BP1:";

        public static string Create(TeklaPlanDocument plan, TeklaPlanCommand command)
        {
            if (plan is null) throw new ArgumentNullException(nameof(plan));
            if (command is null) throw new ArgumentNullException(nameof(command));

            var canonical = new StringBuilder();
            Append(canonical, command.Ownership?.Namespace ?? TeklaPlanContract.OwnershipNamespace);
            Append(canonical, plan.Source?.Address?.ProjectId);
            Append(canonical, plan.Source?.Address?.ScenarioId);
            Append(canonical, plan.Source?.Address?.ModuleId);
            Append(canonical, plan.Source?.Address?.ModuleVariantId);
            Append(canonical, command.Source?.Kind);
            Append(canonical, command.Source?.StableKey);
            Append(canonical, command.CommandId);

            using var sha256 = SHA256.Create();
            var hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(canonical.ToString()));
            return Prefix + BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static void Append(StringBuilder target, string? value)
        {
            var normalized = value ?? string.Empty;
            target.Append(normalized.Length.ToString(CultureInfo.InvariantCulture));
            target.Append(':');
            target.Append(normalized);
            target.Append('|');
        }
    }
}
