using System.Text.Json;

namespace Connector.Desktop.Features.GraphiteWeb;

/// <summary>
/// A validated command received from the local Graphite document.
/// The payload is always a detached JSON object and is safe to retain after the message callback returns.
/// DroppedPaths are the local paths of files the user dropped on the window (WebView2 file objects); they are a
/// user selection only and are validated by the command exactly like a picker result.
/// </summary>
public sealed record GraphiteRpcRequest(
    int SchemaVersion,
    Guid Id,
    string Command,
    JsonElement Payload,
    IReadOnlyList<string>? DroppedPaths = null);
