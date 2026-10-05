using System.Text.Json;

namespace Connector.Desktop.Features.GraphiteWeb;

/// <summary>
/// A validated command received from the local Graphite document.
/// The payload is always a detached JSON object and is safe to retain after the message callback returns.
/// </summary>
public sealed record GraphiteRpcRequest(
    int SchemaVersion,
    Guid Id,
    string Command,
    JsonElement Payload);
