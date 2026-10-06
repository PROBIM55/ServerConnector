namespace Connector.Access.Api;

public sealed class ApiAccessOptions
{
    public required string StateDirectory { get; init; }

    internal string Validate()
    {
        if (string.IsNullOrWhiteSpace(StateDirectory) || !Path.IsPathFullyQualified(StateDirectory))
            throw new ArgumentException("API access StateDirectory must be an absolute path.");
        return Path.GetFullPath(StateDirectory);
    }
}
