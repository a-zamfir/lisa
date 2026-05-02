using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;

namespace Host.Win.Services
{
    internal static class TraceLogUtil
    {
        internal static string ResolveLogsDirectory(string? basePath = null)
        {
            if (!string.IsNullOrWhiteSpace(basePath))
            {
                return basePath;
            }

            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LISA", "logs");
        }

        public static void AppendVerboseDetail(string source, string details, string? basePath = null)
        {
            if (string.IsNullOrWhiteSpace(details))
            {
                return;
            }

            try
            {
                var logsDir = ResolveLogsDirectory(basePath);
                Directory.CreateDirectory(logsDir);
                var tracePath = Path.Combine(logsDir, "verbose-trace.log");
                var payload = new StringBuilder();
                payload.Append('[').Append(source).Append("] raw details @ ").Append(DateTime.UtcNow.ToString("o")).AppendLine();
                payload.AppendLine(details.Trim());
                payload.AppendLine();

                using var stream = new FileStream(tracePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream, Encoding.UTF8);
                writer.Write(payload.ToString());
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"[Host/logging] failed to append verbose detail: {ex.Message}");
            }
        }

        public static string SummarizeText(string? text, int maxLines = 2, int maxChars = 240)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var lines = text
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(line => line.Trim())
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToArray();

            if (lines.Length == 0)
            {
                return string.Empty;
            }

            var summaryLines = lines
                .Skip(Math.Max(0, lines.Length - Math.Max(1, maxLines)))
                .ToArray();

            var summary = string.Join(" | ", summaryLines);
            if (summary.Length > maxChars)
            {
                summary = summary.Substring(0, maxChars) + "...";
            }

            return summary;
        }
    }
}
