using System.Text.Json;
using Connector.Desktop.Features.GraphiteWeb;
using Xunit;

namespace Connector.Desktop.Tests;

public sealed class GraphiteRpcSecurityTests
{
    private const string Valid = "{\"schemaVersion\":1,\"id\":\"467971d7-3d19-4e77-9239-b41e049be59b\",\"command\":\"ifc-detect\",\"payload\":{\"mode\":\"structura\"}}";

    [Fact]
    public void RequestPayloadSurvivesParserDocumentDisposal()
    {
        Assert.True(GraphiteRpcProtocol.TryParseRequest(Valid, out var request, out _));
        Assert.Equal("structura", request!.Payload.GetProperty("mode").GetString());
    }

    [Theory]
    [InlineData("\"schemaVersion\":1", "\"schemaVersion\":2")]
    [InlineData("\"command\":\"ifc-detect\"", "\"command\":\"ifc-detect\",\"command\":\"shell\"")]
    [InlineData("\"payload\":{\"mode\":\"structura\"}", "\"payload\":[],\"hostObject\":true")]
    [InlineData("ifc-detect", "powershell.exe")]
    [InlineData("467971d7-3d19-4e77-9239-b41e049be59b", "invalid")]
    public void MalformedEnvelopeDoesNotReachNativeHandler(string from, string to)
        => Assert.False(GraphiteRpcProtocol.TryParseRequest(Valid.Replace(from, to), out _, out _));

    [Fact]
    public void OversizedUtf8RequestRejected()
        => Assert.False(GraphiteRpcProtocol.TryParseRequest(Valid.Replace("structura", new string('я', 34000)), out _, out _));

    [Theory]
    [InlineData("https://connector.local/desktop.html?page=tekla&sub=ifc", true)]
    [InlineData("https://connector.local/desktop.html?page=converters", true)]
    [InlineData("https://connector.local/index.html", false)]
    [InlineData("https://connector.local.evil/desktop.html", false)]
    [InlineData("https://user:password@connector.local/desktop.html", false)]
    [InlineData("http://connector.local/desktop.html", false)]
    [InlineData("https://connector.local:8443/desktop.html", false)]
    [InlineData("file:///C:/desktop.html", false)]
    public void OnlyBundledDocumentCanSendCommands(string url, bool trusted)
        => Assert.Equal(trusted, GraphiteWebSecurity.IsTrustedDocument(url));

    [Fact]
    public void SnapshotUsesFrontendEventContract()
    {
        using var json = JsonDocument.Parse(GraphiteRpcProtocol.SerializeSnapshot(new { status = "Не подключено", reset = true }));
        Assert.Equal("snapshot", json.RootElement.GetProperty("event").GetString());
        Assert.True(json.RootElement.GetProperty("payload").GetProperty("reset").GetBoolean());
    }
}
