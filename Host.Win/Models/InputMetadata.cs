// File: Host.Win/Models/InputMetadata.cs
using System.Collections.Generic;
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

        [JsonPropertyName("session_nonce")]
        public string? SessionNonce { get; set; }

        /// <summary>
        /// List of active MCP server names to filter tools.
        /// Only tools from these servers will be included in the request.
        /// </summary>
        [JsonPropertyName("active_mcps")]
        public List<string>? ActiveMcps { get; set; }
    }
}
