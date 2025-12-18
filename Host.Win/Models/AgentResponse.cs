// File: Host.Win/Models/AgentResponse.cs
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Host.Win.Models
{
    public sealed class AgentResponse
    {
        [JsonPropertyName("session_id")]
        public string SessionId { get; set; } = string.Empty;

        [JsonPropertyName("turn_id")]
        public string TurnId { get; set; } = string.Empty;

        [JsonPropertyName("messages")]
        public List<AgentMessage> Messages { get; set; } = new();

        [JsonPropertyName("speak")]
        public bool Speak { get; set; }

        [JsonPropertyName("tts_text")]
        public string? TtsText { get; set; }

        [JsonPropertyName("tool_calls")]
        public List<string>? ToolCalls { get; set; }
    }
}
