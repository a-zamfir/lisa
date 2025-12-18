// File: Host.Win/Models/RetryRequest.cs
using System.Text.Json.Serialization;

namespace Host.Win.Models
{
    public sealed class RetryRequest
    {
        [JsonPropertyName("session_id")]
        public string SessionId { get; set; } = string.Empty;

        [JsonPropertyName("turn_id")]
        public string TurnId { get; set; } = string.Empty;
    }
}
