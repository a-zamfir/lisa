// File: Host.Win/Models/ChatMessage.cs
using System.ComponentModel;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using System.Linq;

namespace Host.Win.Models
{
    public sealed class ChatMessage : INotifyPropertyChanged
    {
        private const int MaxMarkdownLength = 4000;

        private string _sender = string.Empty;
        private string _text = string.Empty;
        private bool _isAssistant;
        private bool _isStreaming;
        private bool _isRetryable;
        private string _toolLabel = string.Empty;
        private bool _hasToolLabel;
        private string _turnId = string.Empty;
        private string _reasoning = string.Empty;
        private bool _hasReasoning;
        private bool _isReasoningExpanded;
        private double _thoughtSeconds;
        private bool _isCancellable;
        private bool _hasContentStream;
        private bool _hasMarkdown;
        private readonly ObservableCollection<ChatContentSegment> _contentSegments = new();
        private bool _hasToolApprovals;
        private readonly ObservableCollection<ChatContentSegment> _statusSegments = new();
        private bool _hasStatusBadges;
        private bool _isMemoryUpdating;

        public ChatMessage()
        {
            _contentSegments.CollectionChanged += OnSegmentsChanged;
            _statusSegments.CollectionChanged += OnStatusSegmentsChanged;
        }

        public string Sender
        {
            get => _sender;
            set => SetProperty(ref _sender, value);
        }

        public string Text
        {
            get => _text;
            set
            {
                if (Equals(_text, value)) return;
                _text = value;
                UpdateMarkdownState(value);
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
            }
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

        public string Reasoning
        {
            get => _reasoning;
            set => SetProperty(ref _reasoning, value);
        }

        public bool HasReasoning
        {
            get => _hasReasoning;
            set => SetProperty(ref _hasReasoning, value);
        }

        public bool IsReasoningExpanded
        {
            get => _isReasoningExpanded;
            set => SetProperty(ref _isReasoningExpanded, value);
        }

        public double ThoughtSeconds
        {
            get => _thoughtSeconds;
            set => SetProperty(ref _thoughtSeconds, value);
        }

        public bool IsCancellable
        {
            get => _isCancellable;
            set => SetProperty(ref _isCancellable, value);
        }

        public bool HasContentStream
        {
            get => _hasContentStream;
            set => SetProperty(ref _hasContentStream, value);
        }

        public bool HasMarkdown
        {
            get => _hasMarkdown;
            private set => SetProperty(ref _hasMarkdown, value);
        }

        public ObservableCollection<ChatContentSegment> ContentSegments => _contentSegments;

        public bool HasToolApprovals
        {
            get => _hasToolApprovals;
            private set => SetProperty(ref _hasToolApprovals, value);
        }

        public ObservableCollection<ChatContentSegment> StatusSegments => _statusSegments;

        public bool HasStatusBadges
        {
            get => _hasStatusBadges;
            private set => SetProperty(ref _hasStatusBadges, value);
        }

        public bool IsMemoryUpdating
        {
            get => _isMemoryUpdating;
            set => SetProperty(ref _isMemoryUpdating, value);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public void ResetContentSegments()
        {
            _contentSegments.Clear();
        }

        public void KeepOnlyToolApprovals()
        {
            for (var i = _contentSegments.Count - 1; i >= 0; i--)
            {
                if (!_contentSegments[i].IsApproval)
                {
                    _contentSegments.RemoveAt(i);
                }
            }
        }

        public void AppendContentChunk(string chunk)
        {
            if (string.IsNullOrEmpty(chunk))
            {
                return;
            }

            EnsureTextSegment();
            _contentSegments[^1].Text += chunk;
        }

        public void AddToolApprovalLabel(string toolName, bool approved)
        {
            _contentSegments.Add(ChatContentSegment.ApprovalSegment(toolName, approved));
        }

        private void EnsureTextSegment()
        {
            if (_contentSegments.Count == 0 || _contentSegments[^1].IsApproval)
            {
                _contentSegments.Add(ChatContentSegment.TextSegment(string.Empty));
            }
        }

        private void OnSegmentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            HasToolApprovals = _contentSegments.Any(segment => segment.IsApproval);
        }

        private void OnStatusSegmentsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            HasStatusBadges = _statusSegments.Any(segment => segment.IsBadge);
        }

        private void UpdateMarkdownState(string? text)
        {
            var value = text ?? string.Empty;
            if (value.Length > MaxMarkdownLength)
            {
                HasMarkdown = false;
                return;
            }

            HasMarkdown =
                value.Contains("**", System.StringComparison.Ordinal)
                || value.Contains("`", System.StringComparison.Ordinal)
                || value.Contains("```", System.StringComparison.Ordinal)
                || value.StartsWith("#", System.StringComparison.Ordinal)
                || value.Contains("\n#", System.StringComparison.Ordinal)
                || value.StartsWith("---", System.StringComparison.Ordinal)
                || value.Contains("\n---", System.StringComparison.Ordinal)
                || value.StartsWith("- ", System.StringComparison.Ordinal)
                || value.Contains("\n- ", System.StringComparison.Ordinal);
        }

        private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (Equals(field, value)) return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
