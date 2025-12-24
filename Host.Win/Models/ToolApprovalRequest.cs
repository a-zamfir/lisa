// File: Host.Win/Models/ToolApprovalRequest.cs
using System.Text.Json.Serialization;

namespace Host.Win.Models
{
    public sealed class ToolApprovalRequest
    {
        [JsonPropertyName("approval_id")]
        public string ApprovalId { get; set; } = string.Empty;

        [JsonPropertyName("approved")]
        public bool Approved { get; set; }
    }
}
