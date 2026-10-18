using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Connector.Desktop.Services;

// The browser sees only a public request ID. The native-held verifier redeems
// the approved identity; enrollment material never enters the WebView or logs.
public sealed class ConnectorBrowserSignIn
{
    private const string Origin="https://bimplatforma.ru";
    private const string Root="/api/platform/auth/connector";
    private readonly SemaphoreSlim _active=new(1,1);
    private readonly HttpClient _http;
    private readonly Action<Uri> _openBrowser;
    private static readonly HttpClient ProductionHttp=new(new HttpClientHandler
        { AllowAutoRedirect=false, CheckCertificateRevocationList=true }) { Timeout=TimeSpan.FromSeconds(30) };

    public ConnectorBrowserSignIn() : this(ProductionHttp, uri =>
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute=true })) { }

    public ConnectorBrowserSignIn(HttpClient http, Action<Uri> openBrowser)
    { _http=http; _openBrowser=openBrowser; }

    public async Task<string> SignInAsync(Action<string> progress, CancellationToken cancellationToken)
    {
        if(!await _active.WaitAsync(0,cancellationToken))
            throw new ConnectorSignInException("Вход уже открыт. Завершите его в браузере.");
        try
        {
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            var verifier=Base64Url(RandomNumberGenerator.GetBytes(32));
            var challenge=Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            progress("Открываем вход через Платформу…");
            var started=await PostAsync<StartResponse>("/requests",new {challenge,deviceName=Environment.MachineName},timeout.Token);
            if(!Guid.TryParseExact(started.RequestId,"N",out _) || started.ExpiresIn is <=0 or >300 ||
                started.AuthorizationUrl!=Origin+Root+"/authorize?id="+started.RequestId)
                throw new ConnectorSignInException("Сервис входа вернул неверный ответ. Повторите вход позже.");
            _openBrowser(new Uri(started.AuthorizationUrl));
            progress("Завершите вход в браузере и подтвердите подключение компьютера.");
            while(true)
            {
                await Task.Delay(TimeSpan.FromSeconds(2),timeout.Token);
                var result=await PostAsync<PollResponse>("/poll",new {requestId=started.RequestId,verifier},timeout.Token);
                if(result.Status=="pending") continue;
                if(result.Status!="approved" || string.IsNullOrWhiteSpace(result.EnrollmentToken) ||
                    result.EnrollmentToken.Length>4096 || !result.EnrollmentToken.StartsWith("cea1.",StringComparison.Ordinal))
                    throw new ConnectorSignInException("Вход не завершён. Нажмите «Войти через Платформу» ещё раз.");
                progress("Настраиваем защищённое подключение…");
                return result.EnrollmentToken;
            }
        }
        catch(OperationCanceledException) when(!cancellationToken.IsCancellationRequested)
        { throw new ConnectorSignInException("Время входа истекло. Нажмите «Войти через Платформу» ещё раз."); }
        catch(HttpRequestException)
        { throw new ConnectorSignInException("Сервис входа недоступен. Проверьте интернет и повторите вход."); }
        catch(JsonException)
        { throw new ConnectorSignInException("Не удалось завершить вход. Повторите попытку позже."); }
        finally { _active.Release(); }
    }

    private async Task<T> PostAsync<T>(string path,object value,CancellationToken cancellationToken)
    {
        using var content=new StringContent(JsonSerializer.Serialize(value),Encoding.UTF8,"application/json");
        using var request=new HttpRequestMessage(HttpMethod.Post,Origin+Root+path) { Content=content };
        using var response=await _http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,cancellationToken);
        if(!response.IsSuccessStatusCode)
            throw new ConnectorSignInException(response.StatusCode is System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized
                ? "Вход не подтверждён или доступ не выдан. Повторите вход либо обратитесь к администратору."
                : "Сервис входа недоступен. Повторите попытку позже.");
        await using var stream=await response.Content.ReadAsStreamAsync(cancellationToken);
        var buffer=new byte[8193]; var count=0;
        while(count<buffer.Length)
        {
            var read=await stream.ReadAsync(buffer.AsMemory(count),cancellationToken);
            if(read==0) break; count+=read;
        }
        if(count>8192) throw new ConnectorSignInException("Сервис входа вернул неверный ответ.");
        return JsonSerializer.Deserialize<T>(buffer.AsSpan(0,count),new JsonSerializerOptions
            { PropertyNameCaseInsensitive=true, MaxDepth=8 }) ?? throw new JsonException("Empty sign-in response.");
    }
    private static string Base64Url(byte[] bytes)=>Convert.ToBase64String(bytes).TrimEnd('=').Replace('+','-').Replace('/','_');
    private sealed record StartResponse(string RequestId,string AuthorizationUrl,int ExpiresIn);
    private sealed record PollResponse(string Status,string? EnrollmentToken);
}

public sealed class ConnectorSignInException(string message) : Exception(message);
