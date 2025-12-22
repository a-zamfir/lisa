// File: Host.Win/Models/AudioInputResponse.cs
using System.Text.Json.Serialization;

namespace Host.Win.Models
{
    public sealed class AudioInputResponse
    {
        [JsonPropertyName("session_id")]
        public string SessionId { get; set; } = string.Empty;

        [JsonPropertyName("transcript")]
        public string Transcript { get; set; } = string.Empty;

        [JsonPropertyName("assistant_message")]
        public string AssistantMessage { get; set; } = string.Empty;

        [JsonPropertyName("stt_ms")]
        public int SttMs { get; set; }

        [JsonPropertyName("stt_device")]
        public string? SttDevice { get; set; }

        [JsonPropertyName("stt_compute")]
        public string? SttCompute { get; set; }
    }
}
