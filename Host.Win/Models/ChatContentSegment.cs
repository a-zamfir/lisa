// File: Host.Win/Models/ChatContentSegment.cs
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Host.Win.Models
{
    public sealed class ChatContentSegment : INotifyPropertyChanged
    {
        private string _text = string.Empty;

        public bool IsApproval { get; init; }
        public string ToolName { get; init; } = string.Empty;
        public bool Approved { get; init; }
        public bool IsAutoApproved { get; init; }

        public bool IsBadge { get; init; }
        public string BadgeText { get; init; } = string.Empty;
        public bool BadgeSuccess { get; init; } = true;

        public string Text
        {
            get => _text;
            set => SetProperty(ref _text, value);
        }

        public string StatusText => IsAutoApproved ? "Auto" : (Approved ? "Accepted" : "Rejected");

        public static ChatContentSegment TextSegment(string text)
        {
            return new ChatContentSegment { IsApproval = false, Text = text };
        }

        public static ChatContentSegment ApprovalSegment(string toolName, bool approved)
        {
            return new ChatContentSegment { IsApproval = true, ToolName = toolName, Approved = approved };
        }

        public static ChatContentSegment AutoApprovedSegment(string toolName)
        {
            return new ChatContentSegment { IsApproval = true, ToolName = toolName, Approved = true, IsAutoApproved = true };
        }

        public static ChatContentSegment BadgeSegment(string text, bool success = true)
        {
            return new ChatContentSegment { IsBadge = true, BadgeText = text ?? string.Empty, BadgeSuccess = success };
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
