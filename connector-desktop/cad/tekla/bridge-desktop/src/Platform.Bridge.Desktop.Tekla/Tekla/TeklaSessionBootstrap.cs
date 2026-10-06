#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Platform.Bridge.Desktop.Tekla.Logging;

namespace Platform.Bridge.Desktop.Tekla.Tekla
{
    /// <summary>
    /// Tekla 2020 binds Open API remoting channels to the Windows SESSIONNAME.
    /// Some background launchers do not inherit that variable, so discover it
    /// from the versioned model pipe before any Tekla API type is initialized.
    /// </summary>
    public static class TeklaSessionBootstrap
    {
        private const string EnvironmentKey = "SESSIONNAME";
        private const string PipePrefix = "Tekla.Structures.Model-";

        public static string? ResolveAndApply(
            string teklaVersion,
            string? explicitSessionName,
            JsonLineLogger log)
        {
            if (!string.IsNullOrWhiteSpace(explicitSessionName))
            {
                return Apply(explicitSessionName!.Trim(), "cli", teklaVersion, log);
            }

            var assemblyVersion = NormalizeAssemblyVersion(teklaVersion);
            var sessions = FindSessions(assemblyVersion);
            var inherited = Environment.GetEnvironmentVariable(EnvironmentKey)?.Trim();

            if (!string.IsNullOrWhiteSpace(inherited) &&
                (sessions.Count == 0 || sessions.Contains(inherited!, StringComparer.OrdinalIgnoreCase)))
            {
                return Apply(inherited!, "environment", teklaVersion, log);
            }

            if (sessions.Count == 1)
            {
                return Apply(sessions[0], "named-pipe", teklaVersion, log);
            }

            if (sessions.Count > 1)
            {
                log.Warn("tekla.session.ambiguous", new { teklaVersion, sessions });
            }
            else
            {
                log.Warn("tekla.session.not-found", new { teklaVersion, assemblyVersion });
            }

            return inherited;
        }

        private static string Apply(string sessionName, string source, string teklaVersion, JsonLineLogger log)
        {
            Environment.SetEnvironmentVariable(EnvironmentKey, sessionName);
            log.Info("tekla.session.ready", new { teklaVersion, sessionName, source });
            return sessionName;
        }

        private static List<string> FindSessions(string assemblyVersion)
        {
            var suffix = $":{assemblyVersion}";
            try
            {
                return Directory.EnumerateFiles(@"\\.\pipe\")
                    .Select(Path.GetFileName)
                    .Where(name => !string.IsNullOrWhiteSpace(name) &&
                        name!.StartsWith(PipePrefix, StringComparison.OrdinalIgnoreCase) &&
                        name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    .Select(name => name!.Substring(PipePrefix.Length, name.Length - PipePrefix.Length - suffix.Length))
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        private static string NormalizeAssemblyVersion(string teklaVersion)
        {
            if (Version.TryParse(teklaVersion, out var version))
            {
                return $"{version.Major}.{Math.Max(0, version.Minor)}.0.0";
            }
            return teklaVersion;
        }
    }
}
