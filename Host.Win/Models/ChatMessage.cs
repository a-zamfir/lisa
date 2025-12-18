// File: Host.Win/Models/ChatMessage.cs
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Host.Win.Models
{
    public sealed class ChatMessage : INotifyPropertyChanged
    {
        private string _sender = string.Empty;
        private string _text = string.Empty;
        private bool _isAssistant;
        private bool _isStreaming;
        private bool _isRetryable;
        private string _toolLabel = string.Empty;
        private bool _hasToolLabel;
        private string _turnId = string.Empty;

        public string Sender
        {
            get => _sender;
            set => SetProperty(ref _sender, value);
        }

        public string Text
        {
            get => _text;
            set => SetProperty(ref _text, value);
        }

        public bool IsAssistant
        {
            get => _isAssistant;
            set => SetProperty(ref _isAssistant, value);
        }

        public bool IsStreaming
        {
            get => _isStreaming;
            set => SetProperty(ref _isStreaming, value);
        }

        public bool IsRetryable
        {
            get => _isRetryable;
            set => SetProperty(ref _isRetryable, value);
        }

        public string ToolLabel
        {
            get => _toolLabel;
            set => SetProperty(ref _toolLabel, value);
        }

        public bool HasToolLabel
        {
            get => _hasToolLabel;
            set => SetProperty(ref _hasToolLabel, value);
        }

        public string TurnId
        {
            get => _turnId;
            set => SetProperty(ref _turnId, value);
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
