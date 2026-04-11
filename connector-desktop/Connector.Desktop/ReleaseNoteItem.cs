namespace Connector.Desktop;

public sealed class ReleaseNoteItem
{
    public string Version { get; init; } = "";
    public string PublishedAt { get; init; } = "";
    public string Title { get; init; } = "";
    public IReadOnlyList<string> Changes { get; init; } = Array.Empty<string>();
}
