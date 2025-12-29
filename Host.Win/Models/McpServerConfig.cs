// File: Host.Win/Models/McpServerConfig.cs
using System.Collections.Generic;

namespace Host.Win.Models
{
    public sealed class McpServerConfig
    {
        // Whether server is configured to run
        public bool Enabled { get; set; } = true;

        // stdio transport (new standard)
        public string? Command { get; set; }
        public List<string>? Args { get; set; }
        public Dictionary<string, string>? Env { get; set; }

        // Legacy HTTP transport (deprecated)
        public int Port { get; set; } = 8124;
        public string Host { get; set; } = "127.0.0.1";
        public string ToolPrefix { get; set; } = string.Empty;

        // Server metadata
        public string Description { get; set; } = string.Empty;

        // Runtime state (not persisted) - whether tools from this server are included in requests
        [System.Text.Json.Serialization.JsonIgnore]
        public bool Active { get; set; } = true;
    }
}
