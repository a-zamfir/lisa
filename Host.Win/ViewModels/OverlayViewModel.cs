// File: Host.Win/ViewModels/OverlayViewModel.cs
using Host.Win.Commands;
using Host.Win.Models;
using Host.Win.Services;
using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Host.Win.ViewModels
{
    public sealed class OverlayViewModel : BaseViewModel
    {
        private string? _appName;
        private string? _statusText;
        private AssistantMode _selectedMode;
        private string? _modeContent;
        private bool _isLightTheme;
        private string _themeIcon = "\uE708"; // Sun by default
        private bool _isMcpReady;
        private bool _isAgentReady;
        private bool _isProviderReady;
        private RelayCommand? _toggleThemeCommand;
        private RelayCommand<AssistantMode>? _setModeCommand;
        private string? _mcpStatusText = "Checking...";
        private string? _agentStatusText = "Checking...";
        private string? _providerStatusText = "Checking...";
        private System.Windows.Media.Brush? _mcpStatusBrush;
        private System.Windows.Media.Brush? _agentStatusBrush;
        private System.Windows.Media.Brush? _providerStatusBrush;
        private string _chatInput = string.Empty;
        private bool _isSending;
        private ICommand? _sendChatCommand;
        private ICommand? _copyMessageCommand;
        private ICommand? _retryMessageCommand;
        private ICommand? _stopMessageCommand;
        private ICommand? _resetConversationCommand;
        private ICommand? _toggleShareCommand;
        private HostSettings? _settings;
        private ICommand? _saveSettingsCommand;
        private readonly System.Collections.Generic.Dictionary<string, System.Threading.CancellationTokenSource> _inflightTurns = new();
        private string _sessionId = Guid.NewGuid().ToString();
        private bool _isSharing;

        public string? AppName
        {
            get => _appName;
            set => SetProperty(ref _appName, value);
        }

        public string? StatusText
        {
            get => _statusText;
            set => SetProperty(ref _statusText, value);
        }

        public AssistantMode SelectedMode
        {
            get => _selectedMode;
            set
            {
                if (!SetProperty(ref _selectedMode, value)) return;
                UpdateModeContent();
            }
        }

        public string? ModeContent
        {
            get => _modeContent;
            set => SetProperty(ref _modeContent, value);
        }

        public bool IsLightTheme
        {
            get => _isLightTheme;
            set
            {
                if (!SetProperty(ref _isLightTheme, value)) return;
                UpdateThemeIcon();
            }
        }

        public string ThemeIcon
        {
            get => _themeIcon;
            set => SetProperty(ref _themeIcon, value);
        }

        public RelayCommand? ToggleThemeCommand
        {
            get => _toggleThemeCommand;
            set => SetProperty(ref _toggleThemeCommand, value);
        }

        public RelayCommand<AssistantMode>? SetModeCommand
        {
            get => _setModeCommand;
            set => SetProperty(ref _setModeCommand, value);
        }

        public string? McpStatusText
        {
            get => _mcpStatusText;
            set => SetProperty(ref _mcpStatusText, value);
        }

        public string? ProviderStatusText
        {
            get => _providerStatusText;
            set => SetProperty(ref _providerStatusText, value);
        }

        public string? AgentStatusText
        {
            get => _agentStatusText;
            set => SetProperty(ref _agentStatusText, value);
        }

        public System.Windows.Media.Brush? McpStatusBrush
        {
            get => _mcpStatusBrush;
            set => SetProperty(ref _mcpStatusBrush, value);
        }

        public System.Windows.Media.Brush? ProviderStatusBrush
        {
            get => _providerStatusBrush;
            set => SetProperty(ref _providerStatusBrush, value);
        }

        public System.Windows.Media.Brush? AgentStatusBrush
        {
            get => _agentStatusBrush;
            set => SetProperty(ref _agentStatusBrush, value);
        }

        public string ChatInput
        {
            get => _chatInput;
            set
            {
                if (SetProperty(ref _chatInput, value))
                {
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public ICommand? SendChatCommand
        {
            get => _sendChatCommand;
            set => SetProperty(ref _sendChatCommand, value);
        }

        public ICommand? CopyMessageCommand
        {
            get => _copyMessageCommand;
            set => SetProperty(ref _copyMessageCommand, value);
        }

        public ICommand? RetryMessageCommand
        {
            get => _retryMessageCommand;
            set => SetProperty(ref _retryMessageCommand, value);
        }

        public ICommand? StopMessageCommand
        {
            get => _stopMessageCommand;
            set => SetProperty(ref _stopMessageCommand, value);
        }

        public ICommand? ResetConversationCommand
        {
            get => _resetConversationCommand;
            set => SetProperty(ref _resetConversationCommand, value);
        }

        public ICommand? ToggleShareCommand
        {
            get => _toggleShareCommand;
            set => SetProperty(ref _toggleShareCommand, value);
        }

        public HostSettings? Settings
        {
            get => _settings;
            set => SetProperty(ref _settings, value);
        }

        public ICommand? SaveSettingsCommand
        {
            get => _saveSettingsCommand;
            set => SetProperty(ref _saveSettingsCommand, value);
        }

        public ObservableCollection<ChatMessage> ChatMessages { get; } = new();

        public string SessionId
        {
            get => _sessionId;
            private set => SetProperty(ref _sessionId, value);
        }

        public bool IsSharing
        {
            get => _isSharing;
            set => SetProperty(ref _isSharing, value);
        }

        public AgentClient? AgentClient { get; set; }
        public LoggingService? Logger { get; set; }

        public void ApplyTheme(AppTheme theme)
        {
            IsLightTheme = theme == AppTheme.Light;
            UpdateMcpStatus(_isMcpReady);
            UpdateAgentStatus(_isAgentReady);
            UpdateProviderStatus(_isProviderReady);
        }

        public void UpdateMcpStatus(bool ready)
        {
            _isMcpReady = ready;
            var resourceKey = ready ? "StatusReadyBrush" : "StatusOfflineBrush";
            var brush = System.Windows.Application.Current.Resources[resourceKey] as System.Windows.Media.Brush;
            McpStatusBrush = brush ?? (ready ? System.Windows.Media.Brushes.LimeGreen : System.Windows.Media.Brushes.IndianRed);
            McpStatusText = ready ? "MCP Ready" : "MCP Offline";
        }

        public void UpdateProviderStatus(bool ready)
        {
            _isProviderReady = ready;
            var resourceKey = ready ? "StatusReadyBrush" : "StatusOfflineBrush";
            var brush = System.Windows.Application.Current.Resources[resourceKey] as System.Windows.Media.Brush;
            ProviderStatusBrush = brush ?? (ready ? System.Windows.Media.Brushes.LimeGreen : System.Windows.Media.Brushes.IndianRed);
            ProviderStatusText = ready ? "Provider Ready" : "Provider Offline";
        }

        public void UpdateAgentStatus(bool ready)
        {
            _isAgentReady = ready;
            var resourceKey = ready ? "StatusReadyBrush" : "StatusOfflineBrush";
            var brush = System.Windows.Application.Current.Resources[resourceKey] as System.Windows.Media.Brush;
            AgentStatusBrush = brush ?? (ready ? System.Windows.Media.Brushes.LimeGreen : System.Windows.Media.Brushes.IndianRed);
            AgentStatusText = ready ? "Agent Ready" : "Agent Offline";
        }

        private void UpdateThemeIcon()
        {
            // Sun for light, moon for dark (Segoe MDL2 Assets glyphs)
            ThemeIcon = IsLightTheme ? "\uE708" : "\uE9D4";
        }

        private void UpdateModeContent()
        {
            ModeContent = SelectedMode switch
            {
                AssistantMode.Talk => "Voice mode ready...",
                AssistantMode.Chat => "Type a message...",
                AssistantMode.Share => "Screen sharing is off.",
                AssistantMode.Settings => "Settings panel (placeholder).",
                _ => string.Empty
            };
        }

        public async Task SendChatAsync()
        {
            if (_isSending) return;
            var text = ChatInput?.Trim();
            if (string.IsNullOrEmpty(text)) return;

            _isSending = true;
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            var turnId = Guid.NewGuid().ToString();
            var cts = new System.Threading.CancellationTokenSource();
            _inflightTurns[turnId] = cts;

            try
            {
                var userMessage = new ChatMessage { Sender = "You", Text = text, IsAssistant = false };
                ChatMessages.Add(userMessage);
                ChatInput = string.Empty;

                var streamingMessage = new ChatMessage
                {
                    Sender = "Lisa",
                    Text = string.Empty,
                    IsAssistant = true,
                    IsStreaming = true,
                    TurnId = turnId,
                    IsRetryable = false,
                    IsCancellable = true
                };
                SetRetryableMessage(null);
                ChatMessages.Add(streamingMessage);
                OnPropertyChanged(nameof(ChatMessages)); // notify for scroll refresh

                var request = new TextInputRequest
                {
                    SessionId = SessionId,
                    TurnId = turnId,
                    Text = text,
                    InputMeta = new InputMetadata()
                };

                Logger?.LogEvent("request.text.send", new
                {
                    request.SessionId,
                    request.TurnId,
                    input_type = "text",
                    request.Text,
                    request.InputMeta
                });

                AgentResponse? response = null;
                if (AgentClient != null)
                {
                    response = await AgentClient.SendTextAsync(request, cts.Token);
                }

                if (response != null)
                {
                    ApplyToolLabel(streamingMessage, response.ToolCalls);
                    ApplyReasoning(streamingMessage, response.Reasoning, response.ThinkingMs);
                    foreach (var msg in response.Messages)
                    {
                        if (msg.Role == "assistant")
                        {
                            if (streamingMessage.HasContentStream)
                            {
                                if (string.IsNullOrEmpty(streamingMessage.Text))
                                {
                                    streamingMessage.Text = msg.Content;
                                }
                            }
                            else
                            {
                                await StreamTextAsync(streamingMessage, msg.Content, cts.Token);
                            }
                        }
                    }
                    Logger?.LogEvent("response.text", new
                    {
                        response.SessionId,
                        response.TurnId,
                        input_type = "text",
                        messages = response.Messages
                    });
                }
                else
                {
                    streamingMessage.Text = "(no response)";
                    Logger?.LogEvent("response.text.missing", new
                    {
                        request.SessionId,
                        request.TurnId
                    });
                }

                streamingMessage.IsStreaming = false;
                streamingMessage.IsCancellable = false;
                SetRetryableMessage(streamingMessage);
                OnPropertyChanged(nameof(ChatMessages)); // ensure UI hooks update for autoscroll
            }
            finally
            {
                _inflightTurns.Remove(turnId);
                _isSending = false;
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }

        public bool CanSendChat() => !_isSending && !string.IsNullOrWhiteSpace(ChatInput);

        private static async Task StreamTextAsync(ChatMessage target, string content, System.Threading.CancellationToken cancellationToken)
        {
            var buffer = string.Empty;
            foreach (var ch in content)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                buffer += ch;
                target.Text = buffer;
                await Task.Delay(12, cancellationToken).ConfigureAwait(true);
            }
            target.IsStreaming = false;
        }

        public void CopyMessage(ChatMessage? message)
        {
            if (message == null) return;
            try
            {
                System.Windows.Clipboard.SetText(message.Text ?? string.Empty);
            }
            catch
            {
                // ignore clipboard errors
            }
        }

        public async Task RetryAssistantAsync(ChatMessage? message)
        {
            if (message == null || !message.IsRetryable) return;
            if (AgentClient == null) return;

            message.IsStreaming = true;
            message.IsRetryable = false;
            message.Text = string.Empty;
            message.ToolLabel = string.Empty;
            message.HasToolLabel = false;
            message.IsCancellable = true;
            CommandManager.InvalidateRequerySuggested();

            var request = new RetryRequest
            {
                SessionId = SessionId,
                TurnId = Guid.NewGuid().ToString()
            };
            message.TurnId = request.TurnId;
            var cts = new System.Threading.CancellationTokenSource();
            _inflightTurns[request.TurnId] = cts;

            Logger?.LogEvent("request.text.retry", new
            {
                request.SessionId,
                request.TurnId,
                input_type = "text"
            });

            var response = await AgentClient.SendRetryAsync(request, cts.Token);
            if (response != null)
            {
                ApplyToolLabel(message, response.ToolCalls);
                ApplyReasoning(message, response.Reasoning, response.ThinkingMs);
                foreach (var msg in response.Messages)
                {
                    if (msg.Role == "assistant")
                    {
                        if (message.HasContentStream)
                        {
                            if (string.IsNullOrEmpty(message.Text))
                            {
                                message.Text = msg.Content;
                            }
                        }
                        else
                        {
                            await StreamTextAsync(message, msg.Content, cts.Token);
                        }
                    }
                }
                Logger?.LogEvent("response.text.retry", new
                {
                    response.SessionId,
                    response.TurnId,
                    input_type = "text",
                    messages = response.Messages
                });
            }
            else
            {
                message.Text = "(no response)";
            }

            message.IsStreaming = false;
            message.IsCancellable = false;
            SetRetryableMessage(message);
            _inflightTurns.Remove(request.TurnId);
        }

        private void SetRetryableMessage(ChatMessage? active)
        {
            foreach (var message in ChatMessages)
            {
                if (message.IsAssistant)
                {
                    message.IsRetryable = false;
                }
            }
            if (active != null)
            {
                active.IsRetryable = true;
            }
        }

        private static void ApplyToolLabel(ChatMessage message, System.Collections.Generic.List<string>? toolCalls)
        {
            if (toolCalls == null || toolCalls.Count == 0)
            {
                message.ToolLabel = string.Empty;
                message.HasToolLabel = false;
                return;
            }

            message.ToolLabel = $"Tool: {string.Join(", ", toolCalls)}";
            message.HasToolLabel = true;
        }

        private static void ApplyReasoning(ChatMessage message, string? reasoning, int? thinkingMs)
        {
            var hasReasoning = !string.IsNullOrWhiteSpace(reasoning);
            message.Reasoning = reasoning ?? string.Empty;
            message.HasReasoning = hasReasoning || (thinkingMs.HasValue && thinkingMs.Value > 0);
            message.IsReasoningExpanded = false;
            if (thinkingMs.HasValue)
            {
                message.ThoughtSeconds = Math.Round(thinkingMs.Value / 1000.0, 1);
            }
        }

        public void UpdateToolStatus(string turnId, string phase, System.Collections.Generic.List<string> toolCalls)
        {
            foreach (var message in ChatMessages)
            {
                if (!message.IsAssistant || message.TurnId != turnId) continue;
                var names = toolCalls.Count > 0 ? string.Join(", ", toolCalls) : "tool";
                message.ToolLabel = phase switch
                {
                    "awaiting_tool" => $"Awaiting tool: {names}",
                    "tool_response" => $"Processing tool: {names}",
                    "tool_complete" => $"Tool: {names}",
                    _ => $"Tool: {names}"
                };
                message.HasToolLabel = true;
                break;
            }
        }

        public void UpdateThinkingStatus(string turnId, string phase, string? delta)
        {
            foreach (var message in ChatMessages)
            {
                if (!message.IsAssistant || message.TurnId != turnId) continue;
                if (phase == "thinking_chunk" && !string.IsNullOrEmpty(delta))
                {
                    message.Reasoning += delta;
                    message.HasReasoning = true;
                    message.IsReasoningExpanded = true;
                }
                else if (phase == "thinking_done")
                {
                    message.IsReasoningExpanded = false;
                }
                break;
            }
        }

        public void UpdateContentStatus(string turnId, string phase, string? delta)
        {
            foreach (var message in ChatMessages)
            {
                if (!message.IsAssistant || message.TurnId != turnId) continue;
                if (phase == "content_chunk" && !string.IsNullOrEmpty(delta))
                {
                    message.HasContentStream = true;
                    message.IsStreaming = true;
                    message.Text += delta;
                }
                else if (phase == "content_done")
                {
                    message.HasContentStream = true;
                    message.IsStreaming = false;
                    message.IsCancellable = false;
                }
                break;
            }
        }

        public void StopMessage(ChatMessage? message)
        {
            if (message == null || string.IsNullOrWhiteSpace(message.TurnId)) return;
            if (_inflightTurns.TryGetValue(message.TurnId, out var cts))
            {
                cts.Cancel();
                _inflightTurns.Remove(message.TurnId);
            }
            message.IsStreaming = false;
            message.IsCancellable = false;
            message.ToolLabel = "Stopped";
            message.HasToolLabel = true;
        }

        public void ClearChatHistory()
        {
            ChatMessages.Clear();
        }

        public void ResetConversation()
        {
            foreach (var kvp in _inflightTurns)
            {
                kvp.Value.Cancel();
            }
            _inflightTurns.Clear();
            ClearChatHistory();
            SessionId = Guid.NewGuid().ToString();
            Logger?.LogEvent("conversation.reset", new { sessionId = SessionId });
            CommandManager.InvalidateRequerySuggested();
        }

        public void ToggleShare()
        {
            IsSharing = !IsSharing;
            Logger?.LogEvent(IsSharing ? "share.start" : "share.stop", new { sessionId = SessionId });
        }
    }
}
