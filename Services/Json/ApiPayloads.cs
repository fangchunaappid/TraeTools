using System.Text.Json.Serialization;

namespace TraeTools.Services.Json;

// NativeAOT 下匿名类型无法由源生成器处理，原先的 new { ... } 请求体统一改成具名 DTO。

/// <summary>飞书自定义机器人 text 消息体：{ "msg_type": "text", "content": { "text": ... } }。</summary>
internal sealed class FeishuTextMessage
{
    [JsonPropertyName("msg_type")] public string MsgType { get; set; } = "text";
    [JsonPropertyName("content")] public FeishuTextContent Content { get; set; } = new();
}

internal sealed class FeishuTextContent
{
    [JsonPropertyName("text")] public string Text { get; set; } = "";
}

/// <summary>GitHub 设备码申请体：{ client_id, scope }。</summary>
internal sealed class GitHubDeviceCodeRequest
{
    [JsonPropertyName("client_id")] public string ClientId { get; set; } = "";
    [JsonPropertyName("scope")] public string Scope { get; set; } = "";
}

/// <summary>GitHub 设备码轮询体：{ client_id, device_code, grant_type }。</summary>
internal sealed class GitHubTokenPollRequest
{
    [JsonPropertyName("client_id")] public string ClientId { get; set; } = "";
    [JsonPropertyName("device_code")] public string DeviceCode { get; set; } = "";
    [JsonPropertyName("grant_type")] public string GrantType { get; set; } = "";
}

/// <summary>GitHub Actions secret 写入体：{ encrypted_value, key_id }。</summary>
internal sealed class GitHubSecretPayload
{
    [JsonPropertyName("encrypted_value")] public string EncryptedValue { get; set; } = "";
    [JsonPropertyName("key_id")] public string KeyId { get; set; } = "";
}

/// <summary>GitHub Actions 权限体：{ enabled, allowed_actions }。</summary>
internal sealed class GitHubActionsPermissionPayload
{
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("allowed_actions")] public string AllowedActions { get; set; } = "all";
}

/// <summary>workflow dispatch 体：{ ref }。</summary>
internal sealed class GitHubWorkflowDispatchPayload
{
    [JsonPropertyName("ref")] public string Ref { get; set; } = "main";
}
