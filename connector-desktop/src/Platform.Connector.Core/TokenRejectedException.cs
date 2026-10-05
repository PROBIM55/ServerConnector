using System.Net;

namespace Platform.Connector.Core;

/// <summary>
/// Сервер отклонил device token (HTTP 401/403). Терминальная ошибка для runtime:
/// автоматические ретраи бессмысленны, нужен новый токен от пользователя.
/// В отличие от <see cref="SessionConflictException"/> (409 — сессию вытеснил
/// другой вход тем же токеном, можно пере-bootstrap'иться), здесь сам токен
/// невалиден/отозван.
/// </summary>
public sealed class TokenRejectedException : InvalidOperationException
{
    public TokenRejectedException(string message, HttpStatusCode statusCode, string? responseBody = null)
        : base(message)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    public HttpStatusCode StatusCode { get; }

    public string? ResponseBody { get; }
}
