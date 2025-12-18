// File: Host.Win/Models/TextInputRequest.cs
using System.Text.Json.Serialization;

namespace Host.Win.Models
{
    public sealed class TextInputRequest
    {
        [JsonPropertyName("session_id")]
        public string SessionId { get; set; } = string.Empty;

        [JsonPropertyName("turn_id")]
        public string TurnId { get; set; } = string.Empty;

        [JsonPropertyName("text")]
        public string Text { get; set; } = string.Empty;

        [JsonPropertyName("input_meta")]
        public InputMetadata? InputMeta { get; set; }
    }
}
