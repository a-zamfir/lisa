// File: Host.Win/Models/AgentToolCallback.cs
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Host.Win.Models
{
    public sealed class AgentToolCallback
    {
        [JsonPropertyName("session_id")]
        public string SessionId { get; set; } = string.Empty;

        [JsonPropertyName("turn_id")]
        public string TurnId { get; set; } = string.Empty;

        [JsonPropertyName("phase")]
        public string Phase { get; set; } = string.Empty;

        [JsonPropertyName("tool_calls")]
        public List<string> ToolCalls { get; set; } = new();

        [JsonPropertyName("tool_name")]
        public string? ToolName { get; set; }

        [JsonPropertyName("tool_args")]
        public string? ToolArgs { get; set; }

        [JsonPropertyName("friendly_desc")]
        public string? FriendlyDescription { get; set; }

        [JsonPropertyName("approval_id")]
        public string? ApprovalId { get; set; }

        [JsonPropertyName("timeout_s")]
        public int? TimeoutSeconds { get; set; }

        [JsonPropertyName("thinking_delta")]
        public string? ThinkingDelta { get; set; }

        [JsonPropertyName("content_delta")]
        public string? ContentDelta { get; set; }

        [JsonPropertyName("token")]
        public string? Token { get; set; }

        [JsonPropertyName("session_nonce")]
        public string? SessionNonce { get; set; }
    }
}
