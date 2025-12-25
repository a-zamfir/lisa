// File: Host.Win/Models/VisualFrameRequest.cs
using System;

namespace Host.Win.Models
{
    public sealed class VisualFrameRequest
    {
        public string SessionId { get; set; } = string.Empty;
        public string MimeType { get; set; } = "image/jpeg";
        public string DataBase64 { get; set; } = string.Empty;
        public int Width { get; set; }
        public int Height { get; set; }
        public DateTimeOffset Timestamp { get; set; } = DateTimeOffset.UtcNow;
    }
}

