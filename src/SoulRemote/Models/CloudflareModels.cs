using System.Text.Json.Serialization;

namespace SoulRemote.Models;

// Minimal DTOs for the subset of the Cloudflare API v4 that Soul Remote uses.
// See https://developers.cloudflare.com/api/

public sealed class CfResponse<T>
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("errors")] public List<CfMessage>? Errors { get; set; }
    [JsonPropertyName("messages")] public List<CfMessage>? Messages { get; set; }
    [JsonPropertyName("result")] public T? Result { get; set; }

    public string ErrorSummary()
    {
        if (Errors is null || Errors.Count == 0)
            return "Unknown Cloudflare error";
        return string.Join("; ", Errors.Select(e => $"[{e.Code}] {e.Message}"));
    }
}

public sealed class CfMessage
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("message")] public string? Message { get; set; }
}

public sealed class CfTokenVerify
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
}

public sealed class CfAccount
{
    [JsonPropertyName("id")] public string Id { get; set; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
}

public sealed class CfSubdomain
{
    [JsonPropertyName("subdomain")] public string? Subdomain { get; set; }
}

/// <summary>Result of a successful worker connectivity check.</summary>
public sealed class CloudflareDeployResult
{
    public string AccountId { get; set; } = string.Empty;
    public string AccountName { get; set; } = string.Empty;
    public string Subdomain { get; set; } = string.Empty;
    public string WorkerUrl { get; set; } = string.Empty;
}
