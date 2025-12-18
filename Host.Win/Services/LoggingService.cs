// File: Host.Win/Services/LoggingService.cs
using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

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

        public LoggingService()
        {
            var basePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LISA", "logs");
            Directory.CreateDirectory(basePath);
            _logFilePath = Path.Combine(basePath, "host.log");
        }

        public void LogEvent(string eventType, object payload)
        {
            var record = new
            {
                ts = DateTime.UtcNow.ToString("o"),
                event_type = eventType,
                payload
            };

            var line = JsonSerializer.Serialize(record, _jsonOptions);
            Trace.WriteLine(line);
            lock (_fileLock)
            {
                File.AppendAllText(_logFilePath, line + Environment.NewLine);
            }
        }
    }
}
