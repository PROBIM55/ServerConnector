using System.Net;

namespace Platform.Connector.Core;

public sealed class SessionConflictException : InvalidOperationException
{
    public SessionConflictException(string message, HttpStatusCode statusCode, string? responseBody = null)
        : base(message)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    public HttpStatusCode StatusCode { get; }

    public string? ResponseBody { get; }
}
