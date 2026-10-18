using System.Text;
using System.Text.Json;
using Connector.Access.Smb;
using Connector.Access.Smb.Helper;
using Microsoft.AspNetCore.Http;

namespace Connector.Access.Smb.Tests;

public sealed class SmbHelperRequestReaderTests
{
    [Fact]
    public async Task StreamedJsonWithoutContentLength_IsReadForValidation()
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(JsonSerializer.SerializeToUtf8Bytes(new SmbHelperRequest(
            SmbHelperProtocol.Version, "command-1", "device-1", 1, SmbHelperProtocol.Apply,
            "cnb_0123456789abcdef", null, null, [])));
        context.Request.ContentLength = null;

        var request = await SmbHelperRequestReader.ReadAsync(context.Request, default);

        Assert.NotNull(request);
        Assert.Equal("command-1", request.CommandId);
    }

    [Fact]
    public async Task OversizedStreamWithoutContentLength_IsRejected()
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{\"padding\":\"" + new string('x', 128 * 1024) + "\"}"));
        context.Request.ContentLength = null;

        Assert.Null(await SmbHelperRequestReader.ReadAsync(context.Request, default));
    }

    [Fact]
    public async Task DeclaredOversizeAndEmptyBody_AreRejected()
    {
        var oversized = new DefaultHttpContext();
        oversized.Request.ContentLength = 128 * 1024 + 1;
        oversized.Request.Body = new MemoryStream([1]);
        Assert.Null(await SmbHelperRequestReader.ReadAsync(oversized.Request, default));
        Assert.Equal(0, oversized.Request.Body.Position);

        var empty = new DefaultHttpContext();
        empty.Request.ContentLength = null;
        empty.Request.Body = new MemoryStream();
        Assert.Null(await SmbHelperRequestReader.ReadAsync(empty.Request, default));
    }
}
