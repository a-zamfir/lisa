// File: Host.Win/Models/AgentMessage.cs
using System.Text.Json.Serialization;

namespace Host.Win.Models
{
    public sealed class AgentMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = "assistant";

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;
    }
}
