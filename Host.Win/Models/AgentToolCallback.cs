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
    }
}
