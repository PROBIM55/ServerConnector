using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Connector.Desktop.Services;
using Xunit;

public sealed class ConnectorBrowserSignInTests
{
    [Fact]
    public async Task ApprovalUsesNativeVerifierAndOnlyReturnsGrantAfterBrowserConfirmation()
    {
        string? challenge=null; var calls=0; Uri? opened=null;
        var id=Guid.NewGuid().ToString("N");
        using var http=new HttpClient(new Handler(async request =>
        {
            using var data=JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("bimplatforma.ru",request.RequestUri!.Host);
            if(++calls==1)
            {
                challenge=data.RootElement.GetProperty("challenge").GetString();
                Assert.False(data.RootElement.TryGetProperty("verifier",out _));
                return Json(new {requestId=id,authorizationUrl="https://bimplatforma.ru/api/platform/auth/connector/authorize?id="+id,expiresIn=300});
            }
            Assert.NotNull(opened);
            var verifier=data.RootElement.GetProperty("verifier").GetString()!;
            Assert.Equal(challenge,Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+','-').Replace('/','_'));
            return Json(new {status="approved",enrollmentToken="cea1.private-native-grant"});
        }));
        var progress=new List<string>();
        var service=new ConnectorBrowserSignIn(http,uri=>opened=uri);
        var grant=await service.SignInAsync(progress.Add,CancellationToken.None);
        Assert.Equal("cea1.private-native-grant",grant);
        Assert.DoesNotContain("verifier",opened!.AbsoluteUri);
        Assert.DoesNotContain(progress,message=>message.Contains("cea1.")||message.Contains("private-native-grant"));
        Assert.Equal(2,calls);
    }

    [Fact]
    public async Task RepeatedClickReopensSamePendingRequestWithoutCreatingAnotherVerifier()
    {
        var id=Guid.NewGuid().ToString("N");
        var requests=0;
        var opened=new List<Uri>();
        var firstOpen=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http=new HttpClient(new Handler(request=>
        {
            Interlocked.Increment(ref requests);
            return Task.FromResult(Json(new {requestId=id,
                authorizationUrl="https://bimplatforma.ru/api/platform/auth/connector/authorize?id="+id,expiresIn=300}));
        }));
        var service=new ConnectorBrowserSignIn(http,uri=>{opened.Add(uri);firstOpen.TrySetResult();});
        using var cancel=new CancellationTokenSource();
        var pending=service.SignInAsync(_=>{},cancel.Token);
        await firstOpen.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(service.IsInProgress);
        Assert.True(service.TryReopenActive());
        Assert.Equal(2,opened.Count);
        Assert.Equal(opened[0],opened[1]);
        Assert.Equal(1,requests);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);
        Assert.False(service.IsInProgress);
        Assert.False(service.TryReopenActive());
    }

    [Theory]
    [InlineData("https://attacker.example/authorize")]
    [InlineData("http://bimplatforma.ru/api/platform/auth/connector/authorize")]
    public async Task UntrustedAuthorizationLocationNeverOpensBrowser(string url)
    {
        var opened=false;
        using var http=new HttpClient(new Handler(_=>Task.FromResult(Json(new
            {requestId=Guid.NewGuid().ToString("N"),authorizationUrl=url,expiresIn=300}))));
        var service=new ConnectorBrowserSignIn(http,_=>opened=true);
        await Assert.ThrowsAsync<ConnectorSignInException>(()=>service.SignInAsync(_=>{},CancellationToken.None));
        Assert.False(opened);
    }

    [Fact]
    public async Task OversizedServerResponseIsRejected()
    {
        using var http=new HttpClient(new Handler(_=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content=new StringContent(new string('x',8193)) })));
        var service=new ConnectorBrowserSignIn(http,_=>throw new Exception("Must not open"));
        await Assert.ThrowsAsync<ConnectorSignInException>(()=>service.SignInAsync(_=>{},CancellationToken.None));
    }

    private static HttpResponseMessage Json(object value)=>new(HttpStatusCode.OK)
        {Content=new StringContent(JsonSerializer.Serialize(value),Encoding.UTF8,"application/json")};
    private sealed class Handler(Func<HttpRequestMessage,Task<HttpResponseMessage>> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>handler(request);
    }
}
