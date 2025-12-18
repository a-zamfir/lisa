// File: Host.Win/Models/InputMetadata.cs
using System.Text.Json.Serialization;

namespace Host.Win.Models
{
    public sealed class InputMetadata
    {
        [JsonPropertyName("active_app")]
        public string? ActiveApp { get; set; }

        [JsonPropertyName("window_title")]
        public string? WindowTitle { get; set; }

        [JsonPropertyName("clipboard")]
        public string? Clipboard { get; set; }
    }
}
