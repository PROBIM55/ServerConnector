namespace Connector.Upgrade.ReleaseManifestBuilder;

internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            var values = Parse(args);
            new ReleaseManifestBuilder().Build(new ReleaseBuildRequest(
                values["helper"], values["setup"], values["stage"], values["key"], values["version"],
                values["package-id"], values["setup-version"], values.GetValueOrDefault("publisher"),
                ReleaseManifestBuilder.FindRepositoryRoot(Environment.CurrentDirectory, AppContext.BaseDirectory),
                values.TryGetValue("schema-version", out var schemaText) && int.TryParse(schemaText, out var schema) ? schema : 2,
                values.GetValueOrDefault("caller")));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Release manifest generation failed: {ex.Message}");
            return 1;
        }
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || !args[i].StartsWith("--", StringComparison.Ordinal) ||
                !result.TryAdd(args[i][2..], args[i + 1]))
                throw new ArgumentException("Arguments must be unique --name value pairs.");
        }
        string[] required = ["helper", "setup", "stage", "key", "version", "package-id", "setup-version"];
        if (required.Any(key => !result.ContainsKey(key)) || result.Keys.Any(key => key is not ("helper" or "setup" or "stage" or "key" or "version" or "package-id" or "setup-version" or "publisher" or "schema-version" or "caller")))
            throw new ArgumentException("Required: --helper --setup --stage --key --version --package-id --setup-version; v2 requires --publisher, v3 omits it, v4 requires --caller. Optional: --schema-version 2|3|4.");
        if (result.TryGetValue("schema-version", out var schema) && schema is not ("2" or "3" or "4"))
            throw new ArgumentException("--schema-version must be 2, 3, or 4.");
        if (result.GetValueOrDefault("schema-version", "2") == "2" && string.IsNullOrWhiteSpace(result.GetValueOrDefault("publisher")))
            throw new ArgumentException("Schema v2 requires --publisher.");
        var schemaVersion = result.GetValueOrDefault("schema-version", "2");
        if (schemaVersion == "4" && string.IsNullOrWhiteSpace(result.GetValueOrDefault("caller")))
            throw new ArgumentException("Schema v4 requires --caller.");
        if (schemaVersion != "4" && result.ContainsKey("caller"))
            throw new ArgumentException("--caller is supported only with --schema-version 4.");
        return result;
    }
}
