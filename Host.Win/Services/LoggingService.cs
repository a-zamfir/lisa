// File: Host.Win/Services/LoggingService.cs
using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
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
        private const long MaxLogFileBytes = 10 * 1024 * 1024; // 10MB
        private const int MaxLogFiles = 5; // Keep host.log + host.log.1 through host.log.4
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
            string? line = null;
            try
            {
                using var stream = new MemoryStream();
                using var writer = new Utf8JsonWriter(stream);
                writer.WriteStartObject();
                writer.WriteString("ts", DateTime.UtcNow.ToString("o"));
                writer.WriteString("event_type", eventType);
                writer.WritePropertyName("payload");
                WriteRedactedPayload(writer, payload);
                writer.WriteEndObject();
                writer.Flush();
                line = Encoding.UTF8.GetString(stream.ToArray());
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
                RotateIfNeeded();
                File.AppendAllText(_logFilePath, line + Environment.NewLine);
            }
        }

        private void RotateIfNeeded()
        {
            try
            {
                if (!File.Exists(_logFilePath))
                {
                    return;
                }

                var fileInfo = new FileInfo(_logFilePath);
                if (fileInfo.Length < MaxLogFileBytes)
                {
                    return;
                }

                // Rotate: host.log.3 -> host.log.4, host.log.2 -> host.log.3, etc.
                for (var i = MaxLogFiles - 1; i >= 1; i--)
                {
                    var oldPath = $"{_logFilePath}.{i}";
                    var newPath = $"{_logFilePath}.{i + 1}";

                    if (File.Exists(oldPath))
                    {
                        if (i + 1 >= MaxLogFiles)
                        {
                            // Delete oldest file
                            File.Delete(oldPath);
                        }
                        else
                        {
                            // Rotate to next number
                            if (File.Exists(newPath))
                            {
                                File.Delete(newPath);
                            }
                            File.Move(oldPath, newPath);
                        }
                    }
                }

                // host.log -> host.log.1
                var firstBackup = $"{_logFilePath}.1";
                if (File.Exists(firstBackup))
                {
                    File.Delete(firstBackup);
                }
                File.Move(_logFilePath, firstBackup);
            }
            catch (Exception ex)
            {
                // Don't fail logging due to rotation errors
                Trace.TraceWarning($"Log rotation failed: {ex.Message}");
            }
        }

        private void WriteRedactedPayload(Utf8JsonWriter writer, object payload)
        {
            try
            {
                var json = JsonSerializer.Serialize(payload, _jsonOptions);
                using var doc = JsonDocument.Parse(json);
                WriteRedactedElement(writer, doc.RootElement);
            }
            catch
            {
                writer.WriteStringValue("(redacted)");
            }
        }

        private void WriteRedactedElement(Utf8JsonWriter writer, JsonElement element)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (var prop in element.EnumerateObject())
                    {
                        writer.WritePropertyName(prop.Name);
                        if (_redactedKeys.Contains(prop.Name))
                        {
                            writer.WriteStringValue("(redacted)");
                        }
                        else
                        {
                            WriteRedactedElement(writer, prop.Value);
                        }
                    }
                    writer.WriteEndObject();
                    break;
                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in element.EnumerateArray())
                    {
                        WriteRedactedElement(writer, item);
                    }
                    writer.WriteEndArray();
                    break;
                case JsonValueKind.String:
                    writer.WriteStringValue(element.GetString());
                    break;
                case JsonValueKind.Number:
                    if (element.TryGetInt64(out var l))
                    {
                        writer.WriteNumberValue(l);
                    }
                    else
                    {
                        writer.WriteNumberValue(element.GetDouble());
                    }
                    break;
                case JsonValueKind.True:
                    writer.WriteBooleanValue(true);
                    break;
                case JsonValueKind.False:
                    writer.WriteBooleanValue(false);
                    break;
                case JsonValueKind.Null:
                default:
                    writer.WriteNullValue();
                    break;
            }
        }
    }
}
