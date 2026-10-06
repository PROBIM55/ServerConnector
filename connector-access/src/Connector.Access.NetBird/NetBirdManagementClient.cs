using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Connector.Access.NetBird;

internal sealed class NetBirdManagementClient
{
    private const int MaximumResponseBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;
    private readonly ValidatedNetBirdOptions _options;

    public NetBirdManagementClient(HttpClient httpClient, ValidatedNetBirdOptions options)
    {
        _httpClient = httpClient;
        _options = options;
    }

    public async ValueTask<NetBirdGroup> EnsureDeviceGroupAsync(string name, CancellationToken cancellationToken)
    {
        var matches = await GetAsync<List<NetBirdGroup>>("api/groups?name=" + Uri.EscapeDataString(name), cancellationToken);
        if (matches.Count > 1) throw new InvalidOperationException("NetBird returned multiple device groups with the same exact name.");
        if (matches.Count == 1)
        {
            if (!string.Equals(matches[0].Name, name, StringComparison.Ordinal))
                throw new InvalidOperationException("NetBird group filter returned a non-exact result.");
            return matches[0];
        }

        return await SendJsonAsync<NetBirdGroup>(HttpMethod.Post, "api/groups",
            new NetBirdGroupWrite(name, [], []), cancellationToken);
    }

    public ValueTask<NetBirdGroup?> GetGroupAsync(string id, CancellationToken cancellationToken) =>
        GetOptionalAsync<NetBirdGroup>("api/groups/" + Uri.EscapeDataString(id), cancellationToken);

    public async ValueTask<NetBirdGroup?> FindGroupByNameAsync(string name, CancellationToken cancellationToken)
    {
        var named = (await GetAsync<List<NetBirdGroup>>("api/groups?name=" + Uri.EscapeDataString(name), cancellationToken))
            .Where(existing => string.Equals(existing.Name, name, StringComparison.Ordinal))
            .ToArray();
        if (named.Length > 1) throw new InvalidOperationException("NetBird returned duplicate device-owned groups.");
        return named.SingleOrDefault();
    }

    public async ValueTask AssertBootstrapIsolationAsync(CancellationToken cancellationToken)
    {
        var allGroups = (await GetAsync<List<NetBirdGroup>>("api/groups?name=All", cancellationToken))
            .Where(group => string.Equals(group.Name, "All", StringComparison.Ordinal))
            .ToArray();
        if (allGroups.Length != 1)
            throw new InvalidOperationException("NetBird built-in All group could not be identified uniquely.");

        var allGroupId = allGroups[0].Id;
        var policies = await GetAsync<List<NetBirdPolicy>>("api/policies", cancellationToken);
        foreach (var policy in policies.Where(policy => policy.Enabled))
        {
            foreach (var rule in policy.Rules.Where(rule => rule.Enabled && rule.Action == "accept"))
            {
                if (ContainsReference(rule.Sources, allGroupId) ||
                    rule.Bidirectional && ContainsReference(rule.Destinations, allGroupId))
                    throw new InvalidOperationException("NetBird has an enabled policy that grants the built-in All group traffic; bootstrap is denied.");
            }
        }
    }

    public async ValueTask<NetBirdSetupKey> CreateSetupKeyAsync(
        string name,
        string deviceGroupId,
        CancellationToken cancellationToken) =>
        await SendJsonAsync<NetBirdSetupKey>(HttpMethod.Post, "api/setup-keys",
            new NetBirdSetupKeyCreate(name, "one-off", _options.SetupKeyLifetimeSeconds, [deviceGroupId], 1, false, false),
            cancellationToken);

    public ValueTask<NetBirdSetupKey?> GetSetupKeyAsync(string id, CancellationToken cancellationToken) =>
        GetOptionalAsync<NetBirdSetupKey>("api/setup-keys/" + Uri.EscapeDataString(id), cancellationToken);

    public async ValueTask RevokeSetupKeysNamedAsync(string name, CancellationToken cancellationToken)
    {
        var keys = await GetAsync<List<NetBirdSetupKey>>("api/setup-keys", cancellationToken);
        foreach (var key in keys.Where(key => string.Equals(key.Name, name, StringComparison.Ordinal) && !key.Revoked))
        {
            await RevokeSetupKeyAsync(key.Id, key.AutoGroups, cancellationToken);
        }
    }

    public async ValueTask RevokeSetupKeyAsync(string id, IReadOnlyList<string> autoGroups, CancellationToken cancellationToken)
    {
        var existing = await GetOptionalAsync<NetBirdSetupKey>("api/setup-keys/" + Uri.EscapeDataString(id), cancellationToken);
        if (existing is null || existing.Revoked) return;
        var revoked = await SendJsonAsync<NetBirdSetupKey>(HttpMethod.Put, "api/setup-keys/" + Uri.EscapeDataString(id),
            new NetBirdSetupKeyUpdate(true, autoGroups.Count == 0 ? existing.AutoGroups : autoGroups), cancellationToken);
        if (!revoked.Revoked)
            throw new InvalidOperationException("NetBird did not confirm setup-key revocation.");
    }

    public ValueTask<NetBirdPeer?> GetPeerAsync(string id, CancellationToken cancellationToken) =>
        GetOptionalAsync<NetBirdPeer>("api/peers/" + Uri.EscapeDataString(id), cancellationToken);

    public async ValueTask<NetBirdPolicy> UpsertPolicyAsync(
        string? policyId,
        NetBirdPolicyWrite policy,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(policyId) &&
            await GetOptionalAsync<NetBirdPolicy>("api/policies/" + Uri.EscapeDataString(policyId), cancellationToken) is not null)
        {
            return await SendJsonAsync<NetBirdPolicy>(HttpMethod.Put, "api/policies/" + Uri.EscapeDataString(policyId), policy, cancellationToken);
        }

        var named = (await GetAsync<List<NetBirdPolicy>>("api/policies", cancellationToken))
            .Where(existing => string.Equals(existing.Name, policy.Name, StringComparison.Ordinal))
            .ToArray();
        if (named.Length > 1) throw new InvalidOperationException("NetBird returned duplicate device-owned policies.");
        if (named.Length == 1)
        {
            return await SendJsonAsync<NetBirdPolicy>(HttpMethod.Put, "api/policies/" + Uri.EscapeDataString(named[0].Id), policy, cancellationToken);
        }
        return await SendJsonAsync<NetBirdPolicy>(HttpMethod.Post, "api/policies", policy, cancellationToken);
    }

    public async ValueTask<NetBirdPolicy?> FindPolicyByNameAsync(string name, CancellationToken cancellationToken)
    {
        var named = (await GetAsync<List<NetBirdPolicy>>("api/policies", cancellationToken))
            .Where(existing => string.Equals(existing.Name, name, StringComparison.Ordinal))
            .ToArray();
        if (named.Length > 1) throw new InvalidOperationException("NetBird returned duplicate device-owned policies.");
        return named.SingleOrDefault();
    }

    public ValueTask<NetBirdPolicy?> GetPolicyAsync(string id, CancellationToken cancellationToken) =>
        GetOptionalAsync<NetBirdPolicy>("api/policies/" + Uri.EscapeDataString(id), cancellationToken);

    public ValueTask DeletePolicyAsync(string? id, CancellationToken cancellationToken) =>
        DeleteOptionalAsync("api/policies", id, cancellationToken);

    public ValueTask DeletePeerAsync(string? id, CancellationToken cancellationToken) =>
        DeleteOptionalAsync("api/peers", id, cancellationToken);

    public ValueTask DeleteGroupAsync(string? id, CancellationToken cancellationToken) =>
        DeleteOptionalAsync("api/groups", id, cancellationToken);

    private async ValueTask DeleteOptionalAsync(string collection, string? id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id)) return;
        using var request = CreateRequest(HttpMethod.Delete, collection + "/" + Uri.EscapeDataString(id));
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return;
        await EnsureSuccessAsync(response, cancellationToken);
    }

    private async ValueTask<T> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        var value = await GetOptionalAsync<T>(path, cancellationToken);
        return value ?? throw new NetBirdManagementException(HttpStatusCode.NotFound, "NetBird resource was not found.");
    }

    private async ValueTask<T?> GetOptionalAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, path);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return default;
        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadJsonAsync<T>(response.Content, cancellationToken);
    }

    private async ValueTask<T> SendJsonAsync<T>(HttpMethod method, string path, object body, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(method, path);
        request.Content = JsonContent.Create(body, options: JsonOptions);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await ReadJsonAsync<T>(response.Content, cancellationToken);
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, new Uri(_options.ManagementUri, path));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", _options.AccessToken);
        return request;
    }

    private static bool ContainsReference(IReadOnlyList<JsonElement>? references, string id) =>
        (references ?? []).Any(element => element.ValueKind switch
        {
            JsonValueKind.String => string.Equals(element.GetString(), id, StringComparison.Ordinal),
            JsonValueKind.Object when element.TryGetProperty("id", out var referenceId) =>
                string.Equals(referenceId.GetString(), id, StringComparison.Ordinal),
            _ => false,
        });

    private static async ValueTask EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var detail = await ReadBoundedTextAsync(response.Content, 4096, cancellationToken);
        throw new NetBirdManagementException(response.StatusCode,
            string.IsNullOrWhiteSpace(detail) ? "NetBird Management API rejected the request." : detail);
    }

    private static async ValueTask<T> ReadJsonAsync<T>(HttpContent content, CancellationToken cancellationToken)
    {
        var bytes = await ReadBoundedBytesAsync(content, MaximumResponseBytes, cancellationToken);
        try
        {
            return JsonSerializer.Deserialize<T>(bytes, JsonOptions)
                ?? throw new NetBirdManagementException(HttpStatusCode.OK, "NetBird returned an empty JSON payload.");
        }
        catch (JsonException exception)
        {
            throw new NetBirdManagementException(HttpStatusCode.OK, "NetBird returned invalid JSON.", exception);
        }
    }

    private static async ValueTask<string> ReadBoundedTextAsync(HttpContent content, int limit, CancellationToken cancellationToken) =>
        System.Text.Encoding.UTF8.GetString(await ReadBoundedBytesAsync(content, limit, cancellationToken));

    private static async ValueTask<byte[]> ReadBoundedBytesAsync(HttpContent content, int limit, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > 0 && content.Headers.ContentLength > limit)
            throw new NetBirdManagementException(HttpStatusCode.OK, "NetBird response exceeded the allowed size.");
        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var read = await input.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0) return output.ToArray();
            if (output.Length + read > limit)
                throw new NetBirdManagementException(HttpStatusCode.OK, "NetBird response exceeded the allowed size.");
            output.Write(buffer, 0, read);
        }
    }
}

internal sealed record NetBirdGroup(
    [property: JsonConverter(typeof(FlexibleStringConverter))] string Id,
    string Name,
    IReadOnlyList<NetBirdPeerReference>? Peers,
    IReadOnlyList<NetBirdResourceReference>? Resources);
internal sealed record NetBirdPeerReference(string Id, string Name);
internal sealed record NetBirdResourceReference(string Id, string Type);
internal sealed record NetBirdGroupWrite(string Name, IReadOnlyList<string> Peers, IReadOnlyList<NetBirdResourceReference> Resources);

internal sealed record NetBirdSetupKey(
    [property: JsonConverter(typeof(FlexibleStringConverter))] string Id,
    string Name,
    DateTimeOffset Expires,
    bool Valid,
    bool Revoked,
    [property: JsonPropertyName("used_times")] int UsedTimes,
    [property: JsonPropertyName("auto_groups")] IReadOnlyList<string> AutoGroups,
    string? Key);
internal sealed record NetBirdSetupKeyCreate(
    string Name,
    string Type,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("auto_groups")] IReadOnlyList<string> AutoGroups,
    [property: JsonPropertyName("usage_limit")] int UsageLimit,
    bool Ephemeral,
    [property: JsonPropertyName("allow_extra_dns_labels")] bool AllowExtraDnsLabels);
internal sealed record NetBirdSetupKeyUpdate(bool Revoked, [property: JsonPropertyName("auto_groups")] IReadOnlyList<string> AutoGroups);

internal sealed record NetBirdPeer(
    string Id,
    string Name,
    string? Ip,
    string? Ipv6,
    bool Connected,
    [property: JsonPropertyName("last_seen")] DateTimeOffset LastSeen);

internal sealed record NetBirdPolicy(
    string Id,
    string Name,
    string? Description,
    bool Enabled,
    [property: JsonPropertyName("source_posture_checks")] IReadOnlyList<string>? SourcePostureChecks,
    IReadOnlyList<NetBirdPolicyRuleResponse> Rules);
internal sealed record NetBirdPolicyRuleResponse(
    string Name,
    bool Enabled,
    string Action,
    bool Bidirectional,
    string Protocol,
    IReadOnlyList<JsonElement>? Sources,
    IReadOnlyList<JsonElement>? Destinations,
    IReadOnlyList<string>? Ports);
internal sealed record NetBirdPolicyWrite(
    string Name,
    string Description,
    bool Enabled,
    [property: JsonPropertyName("source_posture_checks")] IReadOnlyList<string> SourcePostureChecks,
    IReadOnlyList<NetBirdPolicyRuleWrite> Rules);
internal sealed record NetBirdPolicyRuleWrite(
    string Name,
    string Description,
    bool Enabled,
    string Action,
    bool Bidirectional,
    string Protocol,
    IReadOnlyList<string> Ports,
    IReadOnlyList<string> Sources,
    IReadOnlyList<string> Destinations);

internal sealed class FlexibleStringConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString()!,
            JsonTokenType.Number when reader.TryGetInt64(out var id) => id.ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonTokenType.Number => reader.GetDecimal().ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => throw new JsonException("Expected a string or number identifier.")
        };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}

public sealed class NetBirdManagementException : Exception
{
    public NetBirdManagementException(HttpStatusCode statusCode, string message, Exception? innerException = null)
        : base(message, innerException) => StatusCode = statusCode;
    public HttpStatusCode StatusCode { get; }
}
