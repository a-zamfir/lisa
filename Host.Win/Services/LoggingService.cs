// File: Host.Win/Services/LoggingService.cs
using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Host.Win.Services
{
    /// <summary>
    /// Simple structured logging to Trace and a local JSONL file.
    /// </summary>
    public sealed class LoggingService
    {
        private readonly string _logFilePath;
        private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web);
        private readonly object _fileLock = new();
        private static readonly HashSet<string> _redactedKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "providerApiKey",
            "provider_api_key",
            "apiKey",
            "api_key",
            "authorization",
            "bearer",
            "token",
            "mcp_auth_token",
            "lisa_callback_token",
            "x-mcp-token",
            "x-provider-api-key",
            "secret",
            "password"
        };

        public LoggingService()
        {
            var basePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LISA", "logs");
            Directory.CreateDirectory(basePath);
            _logFilePath = Path.Combine(basePath, "host.log");
        }

        public void LogEvent(string eventType, object payload)
        {
            var safePayload = RedactPayload(payload);
            var record = new
            {
                ts = DateTime.UtcNow.ToString("o"),
                event_type = eventType,
                payload = safePayload
            };

            string? line = null;
            try
            {
                line = JsonSerializer.Serialize(record, _jsonOptions);
            }
            catch
            {
                var fallback = new
                {
                    ts = DateTime.UtcNow.ToString("o"),
                    event_type = eventType,
                    payload = "(redacted)"
                };
                line = JsonSerializer.Serialize(fallback, _jsonOptions);
            }

            Trace.WriteLine(line);
            lock (_fileLock)
            {
                File.AppendAllText(_logFilePath, line + Environment.NewLine);
            }
        }

        private JsonNode? RedactPayload(object payload)
        {
            try
            {
                var node = JsonSerializer.SerializeToNode(payload, _jsonOptions);
                return RedactNode(node);
            }
            catch
            {
                return JsonValue.Create("(redacted)");
            }
        }

        private JsonNode? RedactNode(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                foreach (var entry in obj.ToList())
                {
                    var key = entry.Key;
                    if (_redactedKeys.Contains(key))
                    {
                        obj[key] = "(redacted)";
                        continue;
                    }

                    obj[key] = RedactNode(entry.Value);
                }

                return obj;
            }

            if (node is JsonArray array)
            {
                for (var i = 0; i < array.Count; i++)
                {
                    array[i] = RedactNode(array[i]);
                }

                return array;
            }

            return node;
        }
    }
}
