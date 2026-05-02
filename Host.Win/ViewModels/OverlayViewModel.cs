// File: Host.Win/ViewModels/OverlayViewModel.cs
using Host.Win.Commands;
using Host.Win.Models;
using Host.Win.Services;
using Host.Win.Views;
using System;
using System.Diagnostics;
using System.Collections.ObjectModel;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.VisualBasic;
using System.Text.Json;
using System.Windows.Threading;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Threading;
using System.Windows.Forms;

namespace Host.Win.ViewModels
{
    public sealed class OverlayViewModel : BaseViewModel
    {
        private string? _appName;
        private string? _statusText;
        private AssistantMode _selectedMode;
        private string? _modeContent;
        private bool _isLightTheme;
        private AppTheme _currentTheme = AppTheme.Glass;
        private string _themeIcon = "\uE708"; // Sun by default
        private bool _isToolClientReady;
        private bool _isAgentReady;
        private bool _isProviderReady;
        private string _toolClientLabel = "Skills";
        private RelayCommand? _toggleThemeCommand;
        private RelayCommand<AssistantMode>? _setModeCommand;
        private string? _toolStatusText = "Checking...";
        private string? _agentStatusText = "Checking...";
        private string? _providerStatusText = "Checking...";
        private System.Windows.Media.Brush? _toolStatusBrush;
        private System.Windows.Media.Brush? _agentStatusBrush;
        private System.Windows.Media.Brush? _providerStatusBrush;
        private string _chatInput = string.Empty;
        private string _greetingName = Environment.UserName;
        private bool _showGreeting = true;
        private bool _isSending;
        private bool _isThinkArmed;
        private ChatMessage? _activeStreamingMessage;
        private ICommand? _sendChatCommand;
        private ICommand? _copyMessageCommand;
        private ICommand? _retryMessageCommand;
        private ICommand? _stopMessageCommand;
        private ICommand? _resetConversationCommand;
        private ICommand? _toggleShareCommand;
        private ICommand? _startTalkCommand;
        private ICommand? _replayTtsCommand;
        private ICommand? _attachFileCommand;
        private HostSettings? _settings;
        private ICommand? _saveSettingsCommand;
        private ICommand? _clearMemoryCommand;
        private ICommand? _toggleCollapseCommand;
        private readonly System.Collections.Generic.Dictionary<string, System.Threading.CancellationTokenSource> _inflightTurns = new();
        private readonly Dictionary<string, StringBuilder> _pendingThinkingByTurn = new();
        private readonly Dictionary<string, Queue<char>> _characterBufferByTurn = new();
        private readonly HashSet<string> _contentDoneTurns = new();
        private readonly Dictionary<string, string> _finalContentByTurn = new();
        private DispatcherTimer? _streamFlushTimer;
        private string _sessionId = Guid.NewGuid().ToString();
        private string _sessionNonce = GenerateSessionNonce();
        private string _providerType = "Ollama";
        private bool _isSharing;
        private bool _isListening;
        private bool _isProcessing;
        private bool _isSpeaking;
        private bool _isContinuousListening;
        private bool _isCollapsed;
        private double _overlayWidth = ExpandedWidth;
        private double _overlayHeight = ExpandedHeight;
        private readonly SemaphoreSlim _screenShareCaptureGate = new(1, 1);
        public Func<double, Task>? SetOverlayOpacityAsync { get; set; }
        public Action? RequestScrollToEnd { get; set; }

        private const double ExpandedWidth = 720;
        private const double ExpandedHeight = 460;
        private const double CollapsedWidth = 420;
        private const double CollapsedHeight = 160;
        private const int ScreenShareMaxDimensionPx = 1280;
        private const long ScreenShareJpegQuality = 70L;
        private const int ScreenShareHideMs = 5;
        private const int MaxChatMessages = 50;

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

        public string GreetingName
        {
            get => _greetingName;
            set
            {
                if (!SetProperty(ref _greetingName, value)) return;
                OnPropertyChanged(nameof(GreetingText));
            }
        }

        public string GreetingText
        {
            get
            {
                var hour = DateTime.Now.Hour;
                var greeting = hour < 12 ? "Good morning" : (hour < 18 ? "Good afternoon" : "Good evening");
                var name = string.IsNullOrWhiteSpace(GreetingName) ? Environment.UserName : GreetingName.Trim();
                return $"{greeting}, {name}!";
            }
        }

        public bool ShowGreeting
        {
            get => _showGreeting;
            set => SetProperty(ref _showGreeting, value);
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

        public AppTheme CurrentTheme
        {
            get => _currentTheme;
            set
            {
                if (!SetProperty(ref _currentTheme, value)) return;
                UpdateThemeIcon();
            }
        }

        public string ThemeIcon
        {
            get => _themeIcon;
            set => SetProperty(ref _themeIcon, value);
        }

        public string ToolClientLabel
        {
            get => _toolClientLabel;
            set
            {
                if (!SetProperty(ref _toolClientLabel, value)) return;
                UpdateToolStatus(_isToolClientReady);
            }
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

        public string? ToolStatusText
        {
            get => _toolStatusText;
            set => SetProperty(ref _toolStatusText, value);
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

        public System.Windows.Media.Brush? ToolStatusBrush
        {
            get => _toolStatusBrush;
            set => SetProperty(ref _toolStatusBrush, value);
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
                    if (ShowGreeting && !string.IsNullOrWhiteSpace(value))
                    {
                        ShowGreeting = false;
                    }
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

        public ICommand? StartTalkCommand
        {
            get => _startTalkCommand;
            set => SetProperty(ref _startTalkCommand, value);
        }

        public ICommand? ReplayTtsCommand
        {
            get => _replayTtsCommand;
            set => SetProperty(ref _replayTtsCommand, value);
        }

        public ICommand? AttachFileCommand
        {
            get => _attachFileCommand;
            set => SetProperty(ref _attachFileCommand, value);
        }

        public bool IsThinkArmed
        {
            get => _isThinkArmed;
            set => SetProperty(ref _isThinkArmed, value);
        }

        public ChatMessage? ActiveStreamingMessage
        {
            get => _activeStreamingMessage;
            private set
            {
                if (!SetProperty(ref _activeStreamingMessage, value)) return;
                OnPropertyChanged(nameof(HasActiveStreaming));
            }
        }

        public bool HasActiveStreaming => _activeStreamingMessage != null;

        public HostSettings? Settings
        {
            get => _settings;
            set
            {
                if (!SetProperty(ref _settings, value)) return;
                ProviderType = _settings?.ProviderType ?? "Ollama";
                OnPropertyChanged(nameof(VoiceRatePercent));
                OnPropertyChanged(nameof(VoiceVolumePercent));
            }
        }

        public int VoiceRatePercent
        {
            get => (int)Math.Round((Settings?.VoiceRate ?? 1.0) * 100.0);
            set
            {
                if (Settings == null) return;
                var clamped = Math.Clamp(value, 50, 200);
                Settings.VoiceRate = clamped / 100.0;
                OnPropertyChanged(nameof(VoiceRatePercent));
            }
        }

        public int VoiceVolumePercent
        {
            get => (int)Math.Round((Settings?.VoiceVolume ?? 1.0) * 100.0);
            set
            {
                if (Settings == null) return;
                var clamped = Math.Clamp(value, 0, 100);
                Settings.VoiceVolume = clamped / 100.0;
                OnPropertyChanged(nameof(VoiceVolumePercent));
            }
        }

        public ICommand? SaveSettingsCommand
        {
            get => _saveSettingsCommand;
            set => SetProperty(ref _saveSettingsCommand, value);
        }

        public ICommand? ClearMemoryCommand
        {
            get => _clearMemoryCommand;
            set => SetProperty(ref _clearMemoryCommand, value);
        }

        public ICommand? ToggleCollapseCommand
        {
            get => _toggleCollapseCommand;
            set => SetProperty(ref _toggleCollapseCommand, value);
        }

        public ObservableCollection<ChatMessage> ChatMessages { get; } = new();
        public ObservableCollection<string> ProviderOptions { get; } = new()
        {
            "Ollama",
            "LM Studio",
            "OpenAI"
        };
        public ObservableCollection<string> ProviderModels { get; } = new();

        public string SessionId
        {
            get => _sessionId;
            private set => SetProperty(ref _sessionId, value);
        }

        public string SessionNonce
        {
            get => _sessionNonce;
            private set => SetProperty(ref _sessionNonce, value);
        }

        public string ProviderType
        {
            get => _providerType;
            set
            {
                if (!SetProperty(ref _providerType, value)) return;
                if (Settings != null)
                {
                    Settings.ProviderType = value;
                }
                RefreshProviderModels();
                OnPropertyChanged(nameof(IsOpenAiProvider));
            }
        }

        public bool IsOpenAiProvider => string.Equals(ProviderType, "OpenAI", StringComparison.OrdinalIgnoreCase);

        public bool IsSharing
        {
            get => _isSharing;
            set => SetProperty(ref _isSharing, value);
        }

        public bool IsListening
        {
            get => _isListening;
            set
            {
                if (SetProperty(ref _isListening, value))
                {
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public bool IsProcessing
        {
            get => _isProcessing;
            set
            {
                if (SetProperty(ref _isProcessing, value))
                {
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public bool IsSpeaking
        {
            get => _isSpeaking;
            set => SetProperty(ref _isSpeaking, value);
        }

        public bool IsContinuousListening
        {
            get => _isContinuousListening;
            set => SetProperty(ref _isContinuousListening, value);
        }

        public bool IsCollapsed
        {
            get => _isCollapsed;
            set
            {
                if (!SetProperty(ref _isCollapsed, value)) return;
                UpdateOverlaySize();
            }
        }

        public double OverlayWidth
        {
            get => _overlayWidth;
            set => SetProperty(ref _overlayWidth, value);
        }

        public double OverlayHeight
        {
            get => _overlayHeight;
            set => SetProperty(ref _overlayHeight, value);
        }

        public AgentClient? AgentClient { get; set; }
        public LoggingService? Logger { get; set; }
        public AudioCaptureService? AudioCaptureService { get; set; }
        public AudioPlaybackService? AudioPlaybackService { get; set; }
        public TtsService? TtsService { get; set; }
        public ContextCollector? ContextCollector { get; set; }
        public ICommand? BrowseProviderModelCommand { get; set; }

        public void ApplyTheme(AppTheme theme)
        {
            CurrentTheme = theme;
            IsLightTheme = theme == AppTheme.Light;
            UpdateToolStatus(_isToolClientReady);
            UpdateAgentStatus(_isAgentReady);
            UpdateProviderStatus(_isProviderReady);
        }

        public void UpdateToolStatus(bool ready)
        {
            _isToolClientReady = ready;
            var resourceKey = ready ? "StatusReadyBrush" : "StatusOfflineBrush";
            var brush = System.Windows.Application.Current.Resources[resourceKey] as System.Windows.Media.Brush;
            ToolStatusBrush = brush ?? (ready ? System.Windows.Media.Brushes.LimeGreen : System.Windows.Media.Brushes.IndianRed);
            var label = string.IsNullOrWhiteSpace(ToolClientLabel) ? "Tools" : ToolClientLabel.Trim();
            ToolStatusText = ready ? $"{label} Ready" : $"{label} Offline";
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
            ThemeIcon = CurrentTheme == AppTheme.Light ? "\uE708" : "\uE706";
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
            var text = ChatInput?.Trim();
            if (string.IsNullOrEmpty(text)) return;

            ShowGreeting = false;
            await SendTextInternalAsync(text, inputType: "text", markSending: true, addUserMessage: true);
        }

        public bool CanSendChat() => !_isSending && !string.IsNullOrWhiteSpace(ChatInput);

        private async Task StreamTextAsync(ChatMessage target, string content, System.Threading.CancellationToken cancellationToken)
        {
            foreach (var ch in content)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                await RunOnUiAsync(() =>
                {
                    target.HasContentStream = true;
                    target.IsStreaming = true;
                    target.AppendContentChunk(ch.ToString());
                }).ConfigureAwait(false);
                await Task.Delay(12, cancellationToken).ConfigureAwait(false);
            }

            await RunOnUiAsync(() =>
            {
                target.Text = content;
                if (target.HasToolApprovals)
                {
                    target.KeepOnlyToolApprovals();
                }
                else
                {
                    target.ResetContentSegments();
                }
                target.IsStreaming = false;
                target.IsCancellable = false;
                if (ReferenceEquals(ActiveStreamingMessage, target))
                {
                    ActiveStreamingMessage = null;
                }
            }).ConfigureAwait(false);
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
            message.ResetContentSegments();
            message.ToolLabel = string.Empty;
            message.HasToolLabel = false;
            message.IsCancellable = true;
            ActiveStreamingMessage = message;
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

            var canceled = false;
            try
            {
                var response = await AgentClient.SendRetryAsync(request, cts.Token).ConfigureAwait(false);
                if (cts.IsCancellationRequested)
                {
                    canceled = true;
                }
                else if (response != null)
                {
                    ApplyToolLabel(message, response.ToolCalls);
                    ApplyReasoning(message, response.Reasoning, response.ThinkingMs);
                    foreach (var msg in response.Messages)
                    {
                        if (msg.Role != "assistant")
                        {
                            continue;
                        }

                        if (message.HasContentStream)
                        {
                            await RunOnUiAsync(() =>
                            {
                                lock (_finalContentByTurn)
                                {
                                    _finalContentByTurn[message.TurnId] = msg.Content;
                                }
                                EnsureStreamFlushTimer();
                            }).ConfigureAwait(false);
                        }
                        else
                        {
                            await StreamTextAsync(message, msg.Content, cts.Token).ConfigureAwait(false);
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
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                canceled = true;
                Logger?.LogEvent("response.text.retry.canceled", new
                {
                    request.SessionId,
                    request.TurnId,
                    input_type = "text"
                });
            }
            finally
            {
                _inflightTurns.Remove(request.TurnId);
                cts.Dispose();

                if (!message.HasContentStream)
                {
                    message.IsStreaming = false;
                    message.IsCancellable = false;
                    if (ReferenceEquals(ActiveStreamingMessage, message))
                    {
                        ActiveStreamingMessage = null;
                    }
                }

                if (!canceled)
                {
                    SetRetryableMessage(message);
                }
            }
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
            RunOnUi(() =>
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
                        "tool_rejected" => $"Tool rejected: {names}",
                        _ => $"Tool: {names}"
                    };
                    message.HasToolLabel = true;
                    break;
                }
            });
        }

        public void UpdateMemoryStatus(string turnId, string phase)
        {
            RunOnUi(() =>
            {
                foreach (var message in ChatMessages)
                {
                    if (!message.IsAssistant || message.TurnId != turnId) continue;
                    if (phase == "memory_update_started")
                    {
                        message.IsMemoryUpdating = true;
                        break;
                    }

                    if (phase == "memory_update_done" || phase == "memory_update_failed")
                    {
                        message.IsMemoryUpdating = false;
                        var success = phase == "memory_update_done";
                        var text = success ? "Memory updated." : "Memory update failed.";
                        message.StatusSegments.Clear();
                        message.StatusSegments.Add(ChatContentSegment.BadgeSegment(text, success));
                        break;
                    }
                    break;
                }
            });
        }

        public void UpdateThinkingStatus(string turnId, string phase, string? delta)
        {
            RunOnUi(() =>
            {
                if (phase == "thinking_chunk" && !string.IsNullOrEmpty(delta))
                {
                    QueueStreamDelta(_pendingThinkingByTurn, turnId, delta);
                    EnsureStreamFlushTimer();
                    return;
                }

                if (phase == "thinking_done")
                {
                    FlushStreamDeltasForTurn(turnId);
                    foreach (var message in ChatMessages)
                    {
                        if (!message.IsAssistant || message.TurnId != turnId) continue;
                        message.IsReasoningExpanded = false;
                        break;
                    }
                }
            });
        }

        public void UpdateContentStatus(string turnId, string phase, string? delta)
        {
            RunOnUi(() =>
            {
                if (phase == "content_chunk" && !string.IsNullOrEmpty(delta))
                {
                    BufferContentDelta(turnId, delta);
                    foreach (var message in ChatMessages)
                    {
                        if (!message.IsAssistant || message.TurnId != turnId) continue;
                        message.HasContentStream = true;
                        message.IsStreaming = true;
                        break;
                    }
                    EnsureStreamFlushTimer();
                    return;
                }

                if (phase == "content_done")
                {
                    lock (_contentDoneTurns)
                    {
                        _contentDoneTurns.Add(turnId);
                    }

                    foreach (var message in ChatMessages)
                    {
                        if (!message.IsAssistant || message.TurnId != turnId) continue;
                        message.HasContentStream = true;
                        message.IsStreaming = true;
                        break;
                    }
                    EnsureStreamFlushTimer();
                }
            });
        }

        public void StopMessage(ChatMessage? message)
        {
            if (message == null || string.IsNullOrWhiteSpace(message.TurnId)) return;
            if (_inflightTurns.TryGetValue(message.TurnId, out var cts))
            {
                cts.Cancel();
                _inflightTurns.Remove(message.TurnId);
            }

            lock (_characterBufferByTurn)
            {
                _characterBufferByTurn.Remove(message.TurnId);
            }
            lock (_contentDoneTurns)
            {
                _contentDoneTurns.Remove(message.TurnId);
            }
            lock (_finalContentByTurn)
            {
                _finalContentByTurn.Remove(message.TurnId);
            }

            message.IsStreaming = false;
            message.IsCancellable = false;
            message.ToolLabel = "Stopped";
            message.HasToolLabel = true;
            if (ReferenceEquals(ActiveStreamingMessage, message))
            {
                ActiveStreamingMessage = null;
            }
        }

        public void ClearChatHistory()
        {
            ChatMessages.Clear();
        }

        private void AddChatMessage(ChatMessage message)
        {
            ChatMessages.Add(message);
            while (ChatMessages.Count > MaxChatMessages)
            {
                ChatMessages.RemoveAt(0);
            }
        }

        public void RefreshProviderModels()
        {
            ProviderModels.Clear();
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.Equals(ProviderType, "Ollama", StringComparison.OrdinalIgnoreCase))
            {
                var manifestRoot = Path.Combine(userProfile, ".ollama", "models", "manifests", "registry.ollama.ai", "library");
                if (Directory.Exists(manifestRoot))
                {
                    foreach (var modelDir in Directory.GetDirectories(manifestRoot))
                    {
                        var modelName = Path.GetFileName(modelDir);
                        var tagFiles = Directory.GetFiles(modelDir);
                        if (tagFiles.Length == 0)
                        {
                            ProviderModels.Add(modelName);
                            continue;
                        }
                        foreach (var tag in tagFiles.Select(Path.GetFileName))
                        {
                            if (!string.IsNullOrWhiteSpace(tag))
                            {
                                ProviderModels.Add($"{modelName}:{tag}");
                            }
                        }
                    }
                }
            }
            else if (string.Equals(ProviderType, "LM Studio", StringComparison.OrdinalIgnoreCase))
            {
                var lmRoot = Path.Combine(userProfile, ".lmstudio", "hub", "models");
                if (Directory.Exists(lmRoot))
                {
                    foreach (var manifestPath in Directory.EnumerateFiles(lmRoot, "manifest.json", SearchOption.AllDirectories))
                    {
                        try
                        {
                            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
                            if (doc.RootElement.TryGetProperty("owner", out var ownerEl)
                                && doc.RootElement.TryGetProperty("name", out var nameEl)
                                && ownerEl.ValueKind == JsonValueKind.String
                                && nameEl.ValueKind == JsonValueKind.String)
                            {
                                var owner = ownerEl.GetString();
                                var name = nameEl.GetString();
                                if (!string.IsNullOrWhiteSpace(owner) && !string.IsNullOrWhiteSpace(name))
                                {
                                    ProviderModels.Add($"{owner}/{name}");
                                }
                                continue;
                            }
                        }
                        catch
                        {
                            // ignore malformed manifests
                        }
                    }
                }
            }
        }

        public void BrowseProviderModel()
        {
            if (!IsOpenAiProvider || Settings == null)
            {
                return;
            }

            var current = Settings.ProviderModel ?? string.Empty;
            var input = Interaction.InputBox("Enter the OpenAI model id.", "OpenAI Model", current);
            var value = input?.Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            Settings.ProviderModel = value;
            OnPropertyChanged(nameof(Settings));
        }

        public void ResetConversation()
        {
            foreach (var kvp in _inflightTurns)
            {
                kvp.Value.Cancel();
            }
            _inflightTurns.Clear();
            ClearChatHistory();
            ActiveStreamingMessage = null;
            ShowGreeting = true;
            UpdateSession(Guid.NewGuid().ToString());
            Logger?.LogEvent("conversation.reset", new { sessionId = SessionId });
            CommandManager.InvalidateRequerySuggested();
        }

        public void ToggleShare()
        {
            IsSharing = !IsSharing;
            Logger?.LogEvent(IsSharing ? "share.armed" : "share.disarmed", new { sessionId = SessionId });
        }

        private async Task CaptureAndSendScreenFrameAsync(CancellationToken cancellationToken)
        {
            if (AgentClient == null)
            {
                return;
            }

            await _screenShareCaptureGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var screen = Screen.PrimaryScreen;
                if (screen == null)
                {
                    return;
                }

                var bounds = screen.Bounds;
                using var bmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
                var hidden = false;
                var hideStart = Stopwatch.StartNew();
                try
                {
                    if (SetOverlayOpacityAsync != null)
                    {
                        await SetOverlayOpacityAsync(0).ConfigureAwait(false);
                        hidden = true;
                        Logger?.LogEvent("share.capture.hide", new { sessionId = SessionId, ms = ScreenShareHideMs });
                        await RunOnUiAsync(() => { }, DispatcherPriority.Render).ConfigureAwait(false);
                        WindowBackdropService.FlushComposition();
                        var remainingMs = ScreenShareHideMs - (int)hideStart.ElapsedMilliseconds;
                        if (remainingMs > 0)
                        {
                            await Task.Delay(remainingMs, cancellationToken).ConfigureAwait(false);
                        }
                    }

                    using var g = Graphics.FromImage(bmp);
                    g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bmp.Size, CopyPixelOperation.SourceCopy);
                }
                finally
                {
                    if (hidden && SetOverlayOpacityAsync != null)
                    {
                        try
                        {
                            await SetOverlayOpacityAsync(1).ConfigureAwait(false);
                            Logger?.LogEvent("share.capture.show", new { sessionId = SessionId });
                            await RunOnUiAsync(() => { }, DispatcherPriority.Render).ConfigureAwait(false);
                            WindowBackdropService.FlushComposition();
                        }
                        catch
                        {
                            // ignore restore failures
                        }
                    }
                }

                using var resized = ResizeToMaxDimension(bmp, ScreenShareMaxDimensionPx);
                var jpegBytes = EncodeJpeg(resized, ScreenShareJpegQuality);
                var req = new VisualFrameRequest
                {
                    SessionId = SessionId,
                    MimeType = "image/jpeg",
                    DataBase64 = Convert.ToBase64String(jpegBytes),
                    Width = resized.Width,
                    Height = resized.Height,
                    Timestamp = DateTimeOffset.UtcNow
                };

                var ok = await AgentClient.SendVisualFrameAsync(req, cancellationToken).ConfigureAwait(false);
                if (ok)
                {
                    Logger?.LogEvent("share.frame.sent", new { sessionId = SessionId, bytes = jpegBytes.Length, width = req.Width, height = req.Height });
                }
                else
                {
                    Logger?.LogEvent("share.frame.failed", new { sessionId = SessionId, bytes = jpegBytes.Length, width = req.Width, height = req.Height });
                }
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Screen share capture failed: {ex.Message}");
                Logger?.LogEvent("share.frame.error", new { sessionId = SessionId, error = ex.Message });
            }
            finally
            {
                _screenShareCaptureGate.Release();
            }
        }

        private static Bitmap ResizeToMaxDimension(Bitmap source, int maxDimension)
        {
            var w = source.Width;
            var h = source.Height;
            if (w <= 0 || h <= 0)
            {
                return (Bitmap)source.Clone();
            }

            var maxSide = Math.Max(w, h);
            if (maxSide <= maxDimension)
            {
                return (Bitmap)source.Clone();
            }

            var scale = (double)maxDimension / maxSide;
            var newW = Math.Max(1, (int)Math.Round(w * scale));
            var newH = Math.Max(1, (int)Math.Round(h * scale));

            var resized = new Bitmap(newW, newH, PixelFormat.Format24bppRgb);
            using var g = Graphics.FromImage(resized);
            g.CompositingQuality = CompositingQuality.HighSpeed;
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.SmoothingMode = SmoothingMode.HighSpeed;
            g.PixelOffsetMode = PixelOffsetMode.HighSpeed;
            g.DrawImage(source, 0, 0, newW, newH);
            return resized;
        }

        private static byte[] EncodeJpeg(Bitmap bmp, long quality)
        {
            using var ms = new MemoryStream();
            var encoder = ImageCodecInfo.GetImageEncoders().FirstOrDefault(e => e.MimeType == "image/jpeg");
            if (encoder == null)
            {
                bmp.Save(ms, ImageFormat.Jpeg);
                return ms.ToArray();
            }

            using var parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
            bmp.Save(ms, encoder, parameters);
            return ms.ToArray();
        }

        private void DisarmShareAfterCapture(string reason)
        {
            if (!IsSharing)
            {
                return;
            }

            IsSharing = false;
            Logger?.LogEvent("share.auto_stop", new { sessionId = SessionId, reason });
        }

        public async Task StartTalkAsync()
        {
            if (IsListening || IsProcessing) return;
            if (AudioCaptureService == null)
            {
                StatusText = "Microphone unavailable";
                return;
            }

            await RunOnUiAsync(() =>
            {
                ShowGreeting = false;
                IsListening = true;
                IsProcessing = false;
                StatusText = "Listening...";
            }).ConfigureAwait(false);

            do
            {
                var vad = new VadDetector();
                AudioCaptureResult? captureResult = null;

                try
                {
                    Trace.WriteLine("Talk: capture started.");
                    captureResult = await AudioCaptureService.CaptureAsync(vad, System.Threading.CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    Trace.TraceWarning("Talk: capture failed.");
                    await RunOnUiAsync(() => StatusText = "Microphone unavailable").ConfigureAwait(false);
                }

                await RunOnUiAsync(() => IsListening = false).ConfigureAwait(false);

                if (captureResult == null)
                {
                    Trace.TraceWarning("Talk: capture result missing.");
                    await RunOnUiAsync(() => IsProcessing = false).ConfigureAwait(false);
                    return;
                }

                if (!captureResult.HadSpeech)
                {
                    Trace.WriteLine($"Talk: no speech detected. Duration={captureResult.Duration.TotalMilliseconds:0}ms");
                    await RunOnUiAsync(() =>
                    {
                        StatusText = "No speech detected";
                        IsProcessing = false;
                    }).ConfigureAwait(false);

                    if (IsContinuousListening)
                    {
                        await Task.Delay(250).ConfigureAwait(false);
                        await RunOnUiAsync(() =>
                        {
                            IsListening = true;
                            StatusText = "Listening...";
                        }).ConfigureAwait(false);
                        continue;
                    }
                    return;
                }

                await RunOnUiAsync(() =>
                {
                    IsProcessing = true;
                    StatusText = "Processing...";
                }).ConfigureAwait(false);
                var processingStart = Stopwatch.StartNew();

                AudioInputResponse? response = null;
                if (AgentClient != null)
                {
                    var context = new AudioInputContext
                    {
                        ActiveWindowTitle = ContextCollector?.GetActiveWindowTitle(),
                        ActiveProcessName = ContextCollector?.GetActiveProcessName()
                    };
                    var meta = new AudioInputMeta
                    {
                        SessionId = SessionId,
                        SessionNonce = SessionNonce,
                        ContinuousVad = IsContinuousListening,
                        Timestamp = DateTimeOffset.UtcNow,
                        Context = context
                    };
                    Trace.WriteLine($"Talk: sending audio ({captureResult.WavBytes.Length} bytes) to agent.");
                    response = await AgentClient.SendAudioAsync(captureResult.WavBytes, meta, System.Threading.CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    Trace.TraceWarning("Talk: agent client missing.");
                }

                var minProcessingMs = 500;
                var remaining = minProcessingMs - (int)processingStart.ElapsedMilliseconds;
                if (remaining > 0)
                {
                    await Task.Delay(remaining).ConfigureAwait(false);
                }

                string transcriptText = string.Empty;
                bool hasTranscript = false;
                await RunOnUiAsync(() =>
                {
                    if (response != null)
                    {
                        if (!string.IsNullOrWhiteSpace(response.SessionId))
                        {
                        UpdateSession(response.SessionId);
                        }

                        if (!string.IsNullOrWhiteSpace(response.Transcript))
                        {
                            transcriptText = response.Transcript.Trim();
                            hasTranscript = true;
                            AddChatMessage(new ChatMessage
                            {
                                Sender = "You",
                                Text = response.Transcript,
                                IsAssistant = false
                            });
                        }
                        else if (response.SttMs == 0)
                        {
                            StatusText = "STT unavailable";
                        }
                        var transcriptLog = response.Transcript ?? string.Empty;
                        var sttDevice = string.IsNullOrWhiteSpace(response.SttDevice) ? "unknown" : response.SttDevice;
                        var sttCompute = string.IsNullOrWhiteSpace(response.SttCompute) ? "unknown" : response.SttCompute;
                        Trace.WriteLine($"Talk: response received. Session={response.SessionId} TranscriptLen={transcriptLog.Length} SttMs={response.SttMs} Device={sttDevice} Compute={sttCompute} Transcript=\"{transcriptLog}\"");
                    }
                    else
                    {
                        StatusText = "Agent unavailable";
                        Trace.TraceWarning("Talk: agent response null.");
                    }

                    if (!hasTranscript && StatusText == "Processing...")
                    {
                        StatusText = "Ready";
                    }
                }).ConfigureAwait(false);

                if (hasTranscript)
                {
                    await SendTextInternalAsync(transcriptText, inputType: "talk", markSending: false, addUserMessage: false).ConfigureAwait(false);
                }

                await RunOnUiAsync(() =>
                {
                    IsProcessing = false;
                    if (StatusText == "Processing...")
                    {
                        StatusText = "Ready";
                    }
                }).ConfigureAwait(false);

                if (IsContinuousListening)
                {
                    await Task.Delay(250).ConfigureAwait(false);
                    await RunOnUiAsync(() =>
                    {
                        IsListening = true;
                        StatusText = "Listening...";
                    }).ConfigureAwait(false);
                }
            } while (IsContinuousListening);
        }

        public bool CanStartTalk() => !IsListening && !IsProcessing;

        public async Task ReplayLastTtsAsync()
        {
            if (AudioPlaybackService == null)
            {
                StatusText = "No audio yet";
                return;
            }

            if (!AudioPlaybackService.HasAudio)
            {
                if (!string.IsNullOrWhiteSpace(AudioPlaybackService.LastText) && TtsService != null)
                {
                    if (SelectedMode == AssistantMode.Talk)
                    {
                        IsSpeaking = true;
                    }
                    var success = await TtsService.SpeakWithSapiAsync(AudioPlaybackService.LastText, Settings, System.Threading.CancellationToken.None)
                        .ConfigureAwait(false);
                    if (SelectedMode == AssistantMode.Talk)
                    {
                        await RunOnUiAsync(() => IsSpeaking = false).ConfigureAwait(false);
                    }
                    if (!success)
                    {
                        StatusText = "No audio yet";
                    }
                    return;
                }

                StatusText = "No audio yet";
                return;
            }

            if (SelectedMode == AssistantMode.Talk)
            {
                IsSpeaking = true;
            }
            // Apply current playback speed setting even for replay.
            var playbackRate = Settings?.VoiceRate ?? 1.0;
            var volume = Settings?.VoiceVolume ?? 1.0;
            await AudioPlaybackService.PlayLastAsync(playbackRate, volume, System.Threading.CancellationToken.None).ConfigureAwait(false);
            if (SelectedMode == AssistantMode.Talk)
            {
                IsSpeaking = false;
            }
        }

        public void ToggleCollapsed()
        {
            IsCollapsed = !IsCollapsed;
        }

        public void ExpandOverlay()
        {
            if (IsCollapsed)
            {
                IsCollapsed = false;
            }
        }

        private void UpdateOverlaySize()
        {
            if (IsCollapsed)
            {
                OverlayWidth = CollapsedWidth;
                OverlayHeight = CollapsedHeight;
            }
            else
            {
                OverlayWidth = ExpandedWidth;
                OverlayHeight = ExpandedHeight;
            }
        }

        private async Task SendTextInternalAsync(string text, string inputType, bool markSending, bool addUserMessage)
        {
            if (markSending && _isSending) return;
            if (string.IsNullOrWhiteSpace(text)) return;

            if (IsSharing)
            {
                Logger?.LogEvent("share.attach.prepare", new { sessionId = SessionId, input_type = inputType });
                try
                {
                    using var capCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await CaptureAndSendScreenFrameAsync(capCts.Token).ConfigureAwait(false);
                }
                catch
                {
                    // best-effort
                }
                finally
                {
                    await RunOnUiAsync(() => DisarmShareAfterCapture("captured")).ConfigureAwait(false);
                }
            }

            if (markSending)
            {
                _isSending = true;
                await RunOnUiAsync(() => CommandManager.InvalidateRequerySuggested()).ConfigureAwait(false);
            }

            var turnId = Guid.NewGuid().ToString();
            var cts = new System.Threading.CancellationTokenSource();
            _inflightTurns[turnId] = cts;

            ChatMessage streamingMessage = new ChatMessage
            {
                Sender = "Lisa",
                Text = string.Empty,
                IsAssistant = true,
                IsStreaming = true,
                TurnId = turnId,
                IsRetryable = false,
                IsCancellable = true
            };

            await RunOnUiAsync(() =>
            {
                if (addUserMessage)
                {
                    AddChatMessage(new ChatMessage { Sender = "You", Text = text, IsAssistant = false });
                }

                if (addUserMessage && inputType == "text")
                {
                    ChatInput = string.Empty;
                }

                SetRetryableMessage(null);
                AddChatMessage(streamingMessage);
                ActiveStreamingMessage = streamingMessage;
                OnPropertyChanged(nameof(ChatMessages));
            }).ConfigureAwait(false);

            Trace.WriteLine("[Skills] Active categories: all installed");

            var request = new TextInputRequest
            {
                SessionId = SessionId,
                TurnId = turnId,
                Text = text,
                InputMeta = new InputMetadata
                {
                    SessionNonce = SessionNonce,
                    ActiveSkillsCategories = null
                }
            };

            Logger?.LogEvent("request.text.send", new
            {
                request.SessionId,
                request.TurnId,
                input_type = inputType,
                request.Text,
                request.InputMeta
            });

            AgentResponse? response = null;
            var canceled = false;
            try
            {
                if (AgentClient != null)
                {
                    response = await AgentClient.SendTextAsync(request, cts.Token).ConfigureAwait(false);
                }

                if (cts.IsCancellationRequested)
                {
                    canceled = true;
                }
                else if (response != null)
                {
                    await RunOnUiAsync(() =>
                    {
                        ApplyToolLabel(streamingMessage, response.ToolCalls);
                        ApplyReasoning(streamingMessage, response.Reasoning, response.ThinkingMs);
                    }).ConfigureAwait(false);

                    foreach (var msg in response.Messages)
                    {
                        if (msg.Role != "assistant")
                        {
                            continue;
                        }

                        if (streamingMessage.HasContentStream)
                        {
                            await RunOnUiAsync(() =>
                            {
                                lock (_finalContentByTurn)
                                {
                                    _finalContentByTurn[turnId] = msg.Content;
                                }
                                EnsureStreamFlushTimer();
                            }).ConfigureAwait(false);
                        }
                        else
                        {
                            await StreamTextAsync(streamingMessage, msg.Content, cts.Token).ConfigureAwait(false);
                        }
                    }

                    Logger?.LogEvent("response.text", new
                    {
                        response.SessionId,
                        response.TurnId,
                        input_type = inputType,
                        messages = response.Messages
                    });
                }
                else
                {
                    await RunOnUiAsync(() => streamingMessage.Text = "(no response)").ConfigureAwait(false);
                    Logger?.LogEvent("response.text.missing", new
                    {
                        request.SessionId,
                        request.TurnId
                    });
                }

                if (!canceled)
                {
                    await HandleTtsAsync(response, streamingMessage, cts.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                canceled = true;
                Logger?.LogEvent("response.text.canceled", new
                {
                    request.SessionId,
                    request.TurnId,
                    input_type = inputType
                });
            }
            finally
            {
                _inflightTurns.Remove(turnId);
                cts.Dispose();

                await RunOnUiAsync(() =>
                {
                    if (!streamingMessage.HasContentStream)
                    {
                        streamingMessage.IsStreaming = false;
                        streamingMessage.IsCancellable = false;
                        if (ReferenceEquals(ActiveStreamingMessage, streamingMessage))
                        {
                            ActiveStreamingMessage = null;
                        }
                    }

                    if (!canceled)
                    {
                        SetRetryableMessage(streamingMessage);
                    }

                    OnPropertyChanged(nameof(ChatMessages));
                }).ConfigureAwait(false);

                if (markSending)
                {
                    _isSending = false;
                    await RunOnUiAsync(() => CommandManager.InvalidateRequerySuggested()).ConfigureAwait(false);
                }
            }
        }

        private async Task HandleTtsAsync(AgentResponse? response, ChatMessage streamingMessage, System.Threading.CancellationToken cancellationToken)
        {
            if (response == null || TtsService == null)
            {
                return;
            }

            var shouldAutoplay =
                SelectedMode == AssistantMode.Talk
                || (SelectedMode == AssistantMode.Chat && (Settings?.TtsEnabledInChat ?? true));

            if (!shouldAutoplay)
            {
                return;
            }

            var ttsText = response.TtsText;
            if (string.IsNullOrWhiteSpace(ttsText))
            {
                ttsText = response.Messages.FirstOrDefault(m => m.Role == "assistant")?.Content;
                if (string.IsNullOrWhiteSpace(ttsText))
                {
                    ttsText = streamingMessage.Text;
                }
            }

            if (string.IsNullOrWhiteSpace(ttsText))
            {
                return;
            }

            AudioPlaybackService?.SetLastText(ttsText);

            try
            {
                var result = await TtsService.GenerateAsync(ttsText, Settings, cancellationToken).ConfigureAwait(false);
                if (result.Success)
                {
                    if (result.AudioBytes != null && result.AudioBytes.Length > 0)
                    {
                        if (AudioPlaybackService != null)
                        {
                            AudioPlaybackService.SetLastAudio(result.AudioBytes);
                            AudioPlaybackService.SetLastText(ttsText);
                            if (SelectedMode == AssistantMode.Talk)
                            {
                                await RunOnUiAsync(() => IsSpeaking = true).ConfigureAwait(false);
                            }
                            await AudioPlaybackService.PlayAsync(
                                    result.AudioBytes,
                                    Settings?.VoiceRate ?? 1.0,
                                    Settings?.VoiceVolume ?? 1.0,
                                    cancellationToken)
                                .ConfigureAwait(false);
                            if (SelectedMode == AssistantMode.Talk)
                            {
                                await RunOnUiAsync(() => IsSpeaking = false).ConfigureAwait(false);
                            }
                        }
                    }
                }
                else if (!string.IsNullOrWhiteSpace(result.Error))
                {
                    await RunOnUiAsync(() => StatusText = result.Error).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"TTS failed: {ex.Message}");
                await RunOnUiAsync(() => StatusText = "TTS unavailable").ConfigureAwait(false);
            }
            finally
            {
                if (SelectedMode == AssistantMode.Talk && IsSpeaking)
                {
                    await RunOnUiAsync(() => IsSpeaking = false).ConfigureAwait(false);
                }
            }
        }

        private static Task RunOnUiAsync(Action action)
        {
            return System.Windows.Application.Current.Dispatcher.InvokeAsync(action).Task;
        }

        private static Task RunOnUiAsync(Action action, DispatcherPriority priority)
        {
            return System.Windows.Application.Current.Dispatcher.InvokeAsync(action, priority).Task;
        }

        private static void RunOnUi(Action action)
        {
            var dispatcher = System.Windows.Application.Current.Dispatcher;
            if (dispatcher.CheckAccess())
            {
                action();
                return;
            }

            _ = dispatcher.InvokeAsync(action);
        }

        private static void QueueStreamDelta(Dictionary<string, StringBuilder> dict, string turnId, string delta)
        {
            if (string.IsNullOrEmpty(delta) || string.IsNullOrWhiteSpace(turnId))
            {
                return;
            }

            lock (dict)
            {
                if (!dict.TryGetValue(turnId, out var sb))
                {
                    sb = new StringBuilder();
                    dict[turnId] = sb;
                }
                sb.Append(delta);
            }
        }

        private void EnsureStreamFlushTimer()
        {
            if (_streamFlushTimer != null)
            {
                return;
            }

            _streamFlushTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromSeconds(1.0 / 60.0) // 60fps
            };
            _streamFlushTimer.Tick += (_, _) => FlushStreamDeltas();
            _streamFlushTimer.Start();
        }

        private void BufferContentDelta(string turnId, string delta)
        {
            if (string.IsNullOrEmpty(delta) || string.IsNullOrWhiteSpace(turnId))
            {
                return;
            }

            lock (_characterBufferByTurn)
            {
                if (!_characterBufferByTurn.TryGetValue(turnId, out var buffer))
                {
                    buffer = new Queue<char>();
                    _characterBufferByTurn[turnId] = buffer;
                }

                foreach (var ch in delta)
                {
                    buffer.Enqueue(ch);
                }
            }
        }

        private void FlushStreamDeltasForTurn(string turnId)
        {
            if (string.IsNullOrWhiteSpace(turnId))
            {
                return;
            }

            string? thinking = null;

            lock (_pendingThinkingByTurn)
            {
                if (_pendingThinkingByTurn.Remove(turnId, out var sb))
                {
                    thinking = sb.ToString();
                }
            }

            ApplyStreamDeltas(turnId, contentDelta: null, thinkingDelta: thinking);
        }

        private void FlushStreamDeltas()
        {
            if (!System.Windows.Application.Current.Dispatcher.CheckAccess())
            {
                return;
            }

            var hadPending = false;
            Dictionary<string, string> thinking;
            lock (_pendingThinkingByTurn)
            {
                hadPending |= _pendingThinkingByTurn.Count > 0;
                thinking = _pendingThinkingByTurn.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToString());
                _pendingThinkingByTurn.Clear();
            }

            Dictionary<string, string> smoothedContent = new();
            lock (_characterBufferByTurn)
            {
                hadPending |= _characterBufferByTurn.Count > 0;

                foreach (var (turnId, buffer) in _characterBufferByTurn.ToList())
                {
                    if (buffer.Count == 0)
                    {
                        _characterBufferByTurn.Remove(turnId);
                        continue;
                    }

                    const int charsPerFrame = 3; // 180 chars/sec at 60fps
                    var charsToTake = buffer.Count <= 3 ? 1 : (buffer.Count <= 10 ? 2 : charsPerFrame);
                    var sb = new StringBuilder(capacity: charsToTake);
                    for (var i = 0; i < charsToTake && buffer.Count > 0; i++)
                    {
                        sb.Append(buffer.Dequeue());
                    }

                    if (sb.Length > 0)
                    {
                        smoothedContent[turnId] = sb.ToString();
                    }

                    if (buffer.Count == 0)
                    {
                        _characterBufferByTurn.Remove(turnId);
                    }
                }
            }

            var allTurnIds = new HashSet<string>(smoothedContent.Keys);
            allTurnIds.UnionWith(thinking.Keys);

            foreach (var turnId in allTurnIds)
            {
                smoothedContent.TryGetValue(turnId, out var contentDelta);
                thinking.TryGetValue(turnId, out var thinkingDelta);
                ApplyStreamDeltas(turnId, contentDelta, thinkingDelta);
            }

            FinalizeCompletedContentStreams();

            lock (_characterBufferByTurn)
            {
                hadPending |= _characterBufferByTurn.Count > 0;
            }

            if (!hadPending)
            {
                _streamFlushTimer?.Stop();
                _streamFlushTimer = null;
            }
        }

        private void FinalizeCompletedContentStreams()
        {
            List<string> turnIds;
            lock (_contentDoneTurns)
            {
                if (_contentDoneTurns.Count == 0)
                {
                    return;
                }

                turnIds = _contentDoneTurns.ToList();
            }

            foreach (var turnId in turnIds)
            {
                lock (_characterBufferByTurn)
                {
                    if (_characterBufferByTurn.ContainsKey(turnId))
                    {
                        continue;
                    }
                }

                ChatMessage? message = null;
                foreach (var candidate in ChatMessages)
                {
                    if (!candidate.IsAssistant || candidate.TurnId != turnId) continue;
                    message = candidate;
                    break;
                }

                if (message == null)
                {
                    lock (_contentDoneTurns)
                    {
                        _contentDoneTurns.Remove(turnId);
                    }
                    lock (_finalContentByTurn)
                    {
                        _finalContentByTurn.Remove(turnId);
                    }
                    continue;
                }

                string? finalText;
                lock (_finalContentByTurn)
                {
                    _finalContentByTurn.TryGetValue(turnId, out finalText);
                }

                finalText ??= string.Concat(message.ContentSegments
                    .Where(segment => !segment.IsApproval && !segment.IsBadge)
                    .Select(segment => segment.Text ?? string.Empty));

                message.Text = finalText;
                if (message.HasToolApprovals)
                {
                    message.KeepOnlyToolApprovals();
                }
                else
                {
                    message.ResetContentSegments();
                }

                message.IsStreaming = false;
                message.IsCancellable = false;
                if (ReferenceEquals(ActiveStreamingMessage, message))
                {
                    ActiveStreamingMessage = null;
                }

                lock (_contentDoneTurns)
                {
                    _contentDoneTurns.Remove(turnId);
                }
                lock (_finalContentByTurn)
                {
                    _finalContentByTurn.Remove(turnId);
                }

                // Request scroll after layout settles to avoid jump
                RequestScrollToEnd?.Invoke();
            }
        }

        private void ApplyStreamDeltas(string turnId, string? contentDelta, string? thinkingDelta)
        {
            if (string.IsNullOrWhiteSpace(turnId))
            {
                return;
            }

            foreach (var message in ChatMessages)
            {
                if (!message.IsAssistant || message.TurnId != turnId) continue;

                if (!string.IsNullOrEmpty(contentDelta))
                {
                    message.HasContentStream = true;
                    message.IsStreaming = true;
                    message.AppendContentChunk(contentDelta);
                }

                if (!string.IsNullOrEmpty(thinkingDelta))
                {
                    message.Reasoning += thinkingDelta;
                    message.HasReasoning = true;
                    message.IsReasoningExpanded = true;
                }

                break;
            }
        }

        private void UpdateSession(string newSessionId)
        {
            if (string.Equals(SessionId, newSessionId, StringComparison.Ordinal))
            {
                return;
            }

            SessionId = newSessionId;
            SessionNonce = GenerateSessionNonce();
        }

        private static string GenerateSessionNonce()
        {
            return Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        }

        public bool IsCallbackAuthorized(string sessionId, string? sessionNonce)
        {
            return string.Equals(SessionId, sessionId, StringComparison.Ordinal)
                && string.Equals(SessionNonce, sessionNonce ?? string.Empty, StringComparison.Ordinal);
        }

        public async Task HandleToolApprovalAsync(AgentToolCallback callback)
        {
            if (AgentClient == null || string.IsNullOrWhiteSpace(callback.ApprovalId))
            {
                return;
            }

            var toolName = callback.ToolName;
            if (string.IsNullOrWhiteSpace(toolName) && callback.ToolCalls.Count > 0)
            {
                toolName = callback.ToolCalls[0];
            }
            toolName ??= "tool";

            var friendly = callback.FriendlyDescription ?? $"would like to run {toolName}.";
            var description = $"Lisa {friendly}";
            var argsText = string.IsNullOrWhiteSpace(callback.ToolArgs) ? "None" : callback.ToolArgs;
            var timeout = callback.TimeoutSeconds.HasValue && callback.TimeoutSeconds.Value > 0
                ? callback.TimeoutSeconds.Value
                : 30;

            ToolApprovalWindow? dialog = null;
            await RunOnUiAsync(() =>
            {
                dialog = new ToolApprovalWindow(description, argsText)
                {
                    Owner = System.Windows.Application.Current.Windows.OfType<System.Windows.Window>().FirstOrDefault(w => w.IsActive)
                        ?? System.Windows.Application.Current.MainWindow
                };
                dialog.Show();
            }).ConfigureAwait(false);

            if (dialog == null)
            {
                return;
            }

            var decisionTask = dialog.WaitForDecisionAsync();
            var completed = await Task.WhenAny(decisionTask, Task.Delay(TimeSpan.FromSeconds(timeout))).ConfigureAwait(false);
            var approved = completed == decisionTask && decisionTask.Result;
            if (completed != decisionTask)
            {
                await RunOnUiAsync(() => dialog.Close()).ConfigureAwait(false);
            }

            await RunOnUiAsync(() => AppendToolApprovalLabel(callback.TurnId, toolName ?? "tool", approved)).ConfigureAwait(false);

            await AgentClient.SendToolApprovalAsync(new ToolApprovalRequest
            {
                ApprovalId = callback.ApprovalId,
                Approved = approved
            }).ConfigureAwait(false);
        }

        private void AppendToolApprovalLabel(string turnId, string toolName, bool approved)
        {
            ChatMessage? target = null;
            foreach (var message in ChatMessages)
            {
                if (!message.IsAssistant) continue;
                if (message.TurnId == turnId)
                {
                    target = message;
                    break;
                }
            }

            target ??= ChatMessages.LastOrDefault(message => message.IsAssistant);
            target?.AddToolApprovalLabel(toolName, approved);
        }

        public void HandleToolAutoApproved(AgentToolCallback callback)
        {
            var toolName = callback.ToolName;
            if (string.IsNullOrWhiteSpace(toolName) && callback.ToolCalls.Count > 0)
            {
                toolName = callback.ToolCalls[0];
            }
            toolName ??= "tool";

            ChatMessage? target = null;
            foreach (var message in ChatMessages)
            {
                if (!message.IsAssistant) continue;
                if (message.TurnId == callback.TurnId)
                {
                    target = message;
                    break;
                }
            }

            target ??= ChatMessages.LastOrDefault(message => message.IsAssistant);
            target?.AddAutoApprovedLabel(toolName);
        }
    }
}
