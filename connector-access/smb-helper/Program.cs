using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Connector.Access.Smb;
using Connector.Access.Smb.Helper;
using Microsoft.AspNetCore.Server.Kestrel.Https;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(options => options.ServiceName = "Structura Connector SMB Helper");
var options = builder.Configuration.GetSection("SmbHelper").Get<SmbHelperHostOptions>() ?? new SmbHelperHostOptions();
if (!options.Enabled) return;

if (!options.ListenUri.IsAbsoluteUri || options.ListenUri.Scheme != Uri.UriSchemeHttps ||
    !IPAddress.TryParse(options.ListenUri.Host, out var listenAddress) || options.ListenUri.AbsolutePath != "/" ||
    !string.IsNullOrEmpty(options.ListenUri.UserInfo) || !string.IsNullOrEmpty(options.ListenUri.Query) || !string.IsNullOrEmpty(options.ListenUri.Fragment) ||
    options.AllowedBackendCertificateSha256.Count == 0 ||
    options.AllowedBackendCertificateSha256.Any(value => !TrySha256(value, out _)) ||
    !TrySha256(options.RunnerScriptSha256, out _) ||
    string.IsNullOrWhiteSpace(options.ServerCertificatePfxPath) || !Path.IsPathFullyQualified(options.ServerCertificatePfxPath) ||
    string.IsNullOrWhiteSpace(options.StateDirectory) || !Path.IsPathFullyQualified(options.StateDirectory) ||
    options.RunnerTimeout <= TimeSpan.Zero || options.RunnerTimeout > TimeSpan.FromMinutes(2) ||
    string.IsNullOrWhiteSpace(options.ServerCertificatePasswordFilePath) && string.IsNullOrWhiteSpace(options.ServerCertificatePasswordEnvironmentVariable))
    throw new InvalidOperationException("SMB helper HTTPS/mTLS configuration is invalid.");

var pfxPassword = SmbHelperServerCertificatePassword.Load(options);
if (string.IsNullOrEmpty(pfxPassword)) throw new InvalidOperationException("SMB helper server certificate password is unavailable.");
#pragma warning disable SYSLIB0057
using var serverCertificate = new X509Certificate2(
    options.ServerCertificatePfxPath, pfxPassword,
    X509KeyStorageFlags.MachineKeySet);
#pragma warning restore SYSLIB0057
var allowedCertificates = options.AllowedBackendCertificateSha256
    .Select(value => Convert.FromHexString(value.Replace(":", "", StringComparison.Ordinal)))
    .ToArray();

builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.Limits.MaxRequestBodySize = 128 * 1024;
    kestrel.Listen(listenAddress, options.ListenUri.Port, listen => listen.UseHttps(new HttpsConnectionAdapterOptions
    {
        ServerCertificate = serverCertificate,
        ClientCertificateMode = ClientCertificateMode.RequireCertificate,
        ClientCertificateValidation = (certificate, _, _) => certificate is not null &&
            SmbBackendCertificateValidator.IsAllowed(certificate, allowedCertificates),
    }));
});
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<ISmbAclExecutor>(_ => new PowerShellSmbAclExecutor(
    Path.Combine(AppContext.BaseDirectory, "SmbAclReconcile.ps1"), options.RunnerTimeout, options.RunnerScriptSha256));
builder.Services.AddSingleton<SmbHelperReconciler>();

var app = builder.Build();
app.MapGet("/health", async (HttpContext context, SmbHelperReconciler reconciler) =>
{
    _ = reconciler; // Resolve validated resources and the state fence store before reporting readiness.
    var certificate = await context.Connection.GetClientCertificateAsync(context.RequestAborted);
    if (certificate is null || !SmbBackendCertificateValidator.IsAllowed(certificate, allowedCertificates)) return Results.Unauthorized();
    return Results.Json(new { status = "ok", service = "connector-smb-helper" });
});
app.MapPost("/v1/smb/grants/reconcile", async (HttpContext context, SmbHelperReconciler reconciler) =>
{
    var certificate = await context.Connection.GetClientCertificateAsync(context.RequestAborted);
    if (certificate is null || !SmbBackendCertificateValidator.IsAllowed(certificate, allowedCertificates)) return Results.Unauthorized();
    if (context.Request.ContentLength is null or <= 0 or > 128 * 1024) return Results.BadRequest();
    SmbHelperRequest? request;
    try
    {
        request = await JsonSerializer.DeserializeAsync<SmbHelperRequest>(context.Request.Body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web), context.RequestAborted);
    }
    catch (JsonException)
    {
        return Results.BadRequest();
    }
    if (request is null) return Results.BadRequest();
    try
    {
        var response = await reconciler.ReconcileAsync(request, context.RequestAborted);
        return Results.Json(response);
    }
    catch (SmbHelperValidationException)
    {
        return Results.BadRequest();
    }
    catch (SmbHelperPostconditionException)
    {
        return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, detail: "SMB reconciliation was not confirmed.");
    }
});
await app.RunAsync();

static bool TrySha256(string value, out byte[] bytes)
{
    try
    {
        bytes = Convert.FromHexString(value.Replace(":", "", StringComparison.Ordinal));
        return bytes.Length == 32;
    }
    catch (FormatException)
    {
        bytes = [];
        return false;
    }
}
