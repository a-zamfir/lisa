// File: Host.Win/Models/AudioInputMeta.cs
using System;
using System.Text.Json.Serialization;

namespace Host.Win.Models
{
    public sealed class AudioInputContext
    {
        [JsonPropertyName("active_window_title")]
        public string? ActiveWindowTitle { get; set; }

        [JsonPropertyName("active_process_name")]
        public string? ActiveProcessName { get; set; }
    }

    public sealed class AudioInputMeta
    {
        [JsonPropertyName("session_id")]
        public string SessionId { get; set; } = string.Empty;

        [JsonPropertyName("session_nonce")]
        public string? SessionNonce { get; set; }

        [JsonPropertyName("continuous_vad")]
        public bool ContinuousVad { get; set; }

        [JsonPropertyName("timestamp")]
        public DateTimeOffset Timestamp { get; set; }

        [JsonPropertyName("context")]
        public AudioInputContext Context { get; set; } = new();
    }
}
