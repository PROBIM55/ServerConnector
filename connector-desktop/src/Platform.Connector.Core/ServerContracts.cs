using System.Text.Json.Serialization;

namespace Platform.Connector.Core;

public enum ConnectorAuthenticationMode { LegacyDeviceToken, IssuedCertificate }

public sealed class ConnectorRuntimeOptions
{
    public string ServerUrl { get; set; } = "http://127.0.0.1:8000";
    public string DeviceId { get; set; } = $"pc-{Environment.MachineName.ToLowerInvariant()}";
    public string DeviceToken { get; set; } = string.Empty;
    public ConnectorAuthenticationMode AuthenticationMode { get; set; } = ConnectorAuthenticationMode.LegacyDeviceToken;
    public string SecureTokenPath { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public int HeartbeatSeconds { get; set; } = 60;
    public int PollIntervalSeconds { get; set; } = 3;
    public int PollBackoffMaxSeconds { get; set; } = 30;
    public int StatusRefreshSeconds { get; set; } = 5;
    public bool EnableJobPolling { get; set; } = true;
    public bool RunDemoJobWhenIdle { get; set; } = true;
    public bool EnableStatusShell { get; set; } = true;
    public string IdempotencyStatePath { get; set; } = string.Empty;
    public string LogFilePath { get; set; } = string.Empty;
    public string AgentType { get; set; } = "platform-cad-connector";
    public string ModuleScope { get; set; } = "shze,bridge,bns-piles,agr-publication";
    public List<string> Capabilities { get; set; } = ["autocad.build", "tekla.apply"];
    public string UpdateManifestUrl { get; set; } = string.Empty;
    public BootstrapSmbAccessDto? SmbAccess { get; set; }
}

public sealed class BootstrapRequestDto
{
    [JsonPropertyName("hostname")]
    public string? Hostname { get; set; }

    [JsonPropertyName("agent_version")]
    public string? AgentVersion { get; set; }

    [JsonPropertyName("public_ip")]
    public string? PublicIp { get; set; }

    [JsonPropertyName("agent_type")]
    public string? AgentType { get; set; }

    [JsonPropertyName("module_scope")]
    public string? ModuleScope { get; set; }

    [JsonPropertyName("capabilities")]
    public List<string>? Capabilities { get; set; }
}

public sealed class BootstrapResponseDto
{
    [JsonPropertyName("ok")]
    public bool Ok { get; set; }

    [JsonPropertyName("session_id")]
    public string SessionId { get; set; } = string.Empty;

    [JsonPropertyName("device_id")]
    public string DeviceId { get; set; } = string.Empty;

    [JsonPropertyName("heartbeat_seconds")]
    public int HeartbeatSeconds { get; set; } = 60;

    [JsonPropertyName("update_manifest_url")]
    public string? UpdateManifestUrl { get; set; }

    [JsonPropertyName("agent_type")]
    public string? AgentType { get; set; }

    [JsonPropertyName("module_scope")]
    public string? ModuleScope { get; set; }

    [JsonPropertyName("capabilities")]
    public List<string>? Capabilities { get; set; }

    [JsonPropertyName("smb_access")]
    public BootstrapSmbAccessDto? SmbAccess { get; set; }
}

public sealed class BootstrapSmbAccessDto
{
    [JsonPropertyName("login")]
    public string? Login { get; set; }

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("password")]
    public string? Password { get; set; }

    [JsonPropertyName("share_unc")]
    public string? ShareUnc { get; set; }

    [JsonPropertyName("share_path")]
    public string? SharePath { get; set; }
}
