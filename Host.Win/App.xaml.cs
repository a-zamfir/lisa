// File: Host.Win/App.xaml.cs
using System;
using System.Diagnostics;
using System.Windows;
using Host.Win.Models;
using Host.Win.Services;
using Host.Win.ViewModels;
using Host.Win.Views;
using System.IO;
using System.Windows.Threading;

namespace Host.Win
{
    public partial class App : System.Windows.Application
    {
        private TrayIconManager? _trayIconManager;
        private HotkeyManager? _hotkeyManager;
        private OverlayController? _overlayController;
        private AgentClient? _agentClient;
        private ContextCollector? _contextCollector;
        private ThemeService? _themeService;
        private SettingsService? _settingsService;
        private PortHealthChecker? _mcpHealthChecker;
        private PortHealthChecker? _agentHealthChecker;
        private PortHealthChecker? _providerHealthChecker;
        private LoggingService? _loggingService;
        private AudioCaptureService? _audioCaptureService;
        private AudioPlaybackService? _audioPlaybackService;
        private TtsService? _ttsService;
        private HostSettings? _hostSettings;
        private AgentProcessHost? _agentProcessHost;
        private McpProcessHost? _mcpProcessHost;
        private TcpCallbackServer? _agentCallbackServer;
        private TextWriterTraceListener? _verboseTraceListener;
        private readonly string _agentCallbackToken = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        private const int AgentCallbackPort = 5052;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            Trace.Listeners.Add(new ConsoleTraceListener());
            Trace.WriteLine("Host.Win starting.");

            _settingsService = new SettingsService();
            _hostSettings = _settingsService.LoadAsync().GetAwaiter().GetResult();
            UpdateVerboseTraceListener(_hostSettings.VerboseLogging);
            _contextCollector = new ContextCollector();
            _audioCaptureService = new AudioCaptureService();
            _audioPlaybackService = new AudioPlaybackService();
            _ttsService = new TtsService();
            _agentClient = new AgentClient(new Uri($"http://{_hostSettings.AgentHost}:{_hostSettings.AgentPort}"));
            _agentClient.SetProviderApiKey(_hostSettings.ProviderApiKey);
            _themeService = new ThemeService();
            _loggingService = new LoggingService();
            var displayName = _contextCollector.GetDisplayName();
            var greetingName = string.IsNullOrWhiteSpace(displayName)
                ? Environment.UserName
                : displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            var overlayVm = new OverlayViewModel
            {
                AppName = "LISA",
                StatusText = "Checking...",
                GreetingName = greetingName,
                ShowGreeting = true,
                AgentClient = _agentClient,
                Logger = _loggingService,
                Settings = _hostSettings,
                AudioCaptureService = _audioCaptureService,
                AudioPlaybackService = _audioPlaybackService,
                TtsService = _ttsService,
                ContextCollector = _contextCollector
            };
            overlayVm.UpdateMcpStatus(false);
            overlayVm.UpdateAgentStatus(false);
            overlayVm.UpdateProviderStatus(false);
            overlayVm.ProviderType = _hostSettings.ProviderType;
            overlayVm.RefreshProviderModels();

            var initialTheme = AppTheme.Glass;
            _themeService.ApplyTheme(initialTheme);
            overlayVm.ApplyTheme(initialTheme);

            var overlayWindow = new OverlayWindow { DataContext = overlayVm };
            _overlayController = new OverlayController(overlayWindow, _contextCollector);
            var captureWasVisible = false;
            overlayVm.SetOverlayOpacityAsync = opacity =>
                overlayWindow.Dispatcher.InvokeAsync(() =>
                {
                    if (opacity <= 0)
                    {
                        captureWasVisible = overlayWindow.IsVisible;
                        overlayWindow.Opacity = 0;
                        if (captureWasVisible)
                        {
                            overlayWindow.Hide();
                        }
                        return;
                    }

                    if (captureWasVisible && !overlayWindow.IsVisible)
                    {
                        overlayWindow.Show();
                        overlayWindow.Topmost = true;
                    }
                    overlayWindow.Opacity = opacity;
                }, DispatcherPriority.Send).Task;

            overlayVm.ToggleThemeCommand = new Commands.RelayCommand(() =>
            {
                var newTheme = overlayVm.CurrentTheme switch
                {
                    AppTheme.Glass => AppTheme.Dark,
                    AppTheme.Dark => AppTheme.Light,
                    _ => AppTheme.Glass
                };
                _themeService.ApplyTheme(newTheme);
                overlayVm.ApplyTheme(newTheme);
            });

            overlayVm.SetModeCommand = new Commands.RelayCommand<AssistantMode>(mode =>
            {
                if (mode == AssistantMode.Share)
                {
                    overlayVm.ToggleShare();
                    overlayVm.StatusText = overlayVm.IsSharing ? "Screen share armed" : "Screen share off";
                    return;
                }

                if (mode == AssistantMode.Chat || mode == AssistantMode.Settings)
                {
                    overlayVm.ExpandOverlay();
                }
                overlayVm.SelectedMode = mode;
            });

            overlayVm.SendChatCommand = new Commands.AsyncRelayCommand(() => overlayVm.SendChatAsync(), overlayVm.CanSendChat);
            overlayVm.CopyMessageCommand = new Commands.RelayCommand<Models.ChatMessage>(message => overlayVm.CopyMessage(message));
            overlayVm.RetryMessageCommand = new Commands.RelayCommand<Models.ChatMessage>(message => _ = overlayVm.RetryAssistantAsync(message));
            overlayVm.StopMessageCommand = new Commands.RelayCommand<Models.ChatMessage>(message => overlayVm.StopMessage(message));
            overlayVm.ResetConversationCommand = new Commands.RelayCommand(() => overlayVm.ResetConversation());
            overlayVm.ToggleShareCommand = new Commands.RelayCommand(() => overlayVm.ToggleShare());
            overlayVm.StartTalkCommand = new Commands.AsyncRelayCommand(() => overlayVm.StartTalkAsync(), overlayVm.CanStartTalk);
            overlayVm.ReplayTtsCommand = new Commands.AsyncRelayCommand(() => overlayVm.ReplayLastTtsAsync());
            overlayVm.ToggleCollapseCommand = new Commands.RelayCommand(() => overlayVm.ToggleCollapsed());
            overlayVm.BrowseProviderModelCommand = new Commands.RelayCommand(() => overlayVm.BrowseProviderModel());

            overlayVm.SelectedMode = AssistantMode.Chat;

            // From Host.Win/bin/Debug/... back to repo root then into Agent.Worker/main.py
            var agentScript = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "Agent.Worker", "main.py");
            var mcpScript = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "Agent.MCP", "main.py");
            _mcpProcessHost = new McpProcessHost(Path.GetFullPath(mcpScript), port: _hostSettings.McpPort, verboseLogging: _hostSettings.VerboseLogging);
            _mcpProcessHost.Start();
            _agentClient?.SetMcpAuthToken(_mcpProcessHost.AuthToken);

            _agentProcessHost = new AgentProcessHost(
                Path.GetFullPath(agentScript),
                port: _hostSettings.AgentPort,
                callbackToken: _agentCallbackToken,
                callbackPort: AgentCallbackPort,
                mcpAuthToken: _mcpProcessHost.AuthToken,
                providerApiKey: _hostSettings.ProviderApiKey,
                verboseLogging: _hostSettings.VerboseLogging);
            _agentCallbackServer = new TcpCallbackServer(AgentCallbackPort, _agentCallbackToken, callback =>
            {
                Dispatcher.InvokeAsync(() =>
                {
                    if (!overlayVm.IsCallbackAuthorized(callback.SessionId, callback.SessionNonce))
                    {
                        return;
                    }
                     if (callback.Phase == "tool_approval_required")
                     {
                         _ = overlayVm.HandleToolApprovalAsync(callback);
                     }
                     else if (callback.Phase == "memory_update_started"
                              || callback.Phase == "memory_update_done"
                              || callback.Phase == "memory_update_failed")
                     {
                         overlayVm.UpdateMemoryStatus(callback.TurnId, callback.Phase);
                         if (_hostSettings?.VerboseLogging == true)
                         {
                             _loggingService?.LogEvent("memory.update", new
                             {
                                 callback.SessionId,
                                 callback.TurnId,
                                 callback.Phase,
                                 callback.MemoryOpCount,
                                 callback.MemoryError
                             });
                         }
                     }
                     else if (callback.Phase == "thinking_chunk" || callback.Phase == "thinking_done")
                     {
                         overlayVm.UpdateThinkingStatus(callback.TurnId, callback.Phase, callback.ThinkingDelta);
                     }
                     else if (callback.Phase == "content_chunk" || callback.Phase == "content_done")
                    {
                        overlayVm.UpdateContentStatus(callback.TurnId, callback.Phase, callback.ContentDelta);
                    }
                    else
                    {
                        overlayVm.UpdateToolStatus(callback.TurnId, callback.Phase, callback.ToolCalls);
                    }
                });
            });
            _agentCallbackServer.Start();
            _agentProcessHost.Start();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => _agentProcessHost?.Stop();
            DispatcherUnhandledException += (_, _) => _agentProcessHost?.Stop();
            Exit += (_, _) => _agentProcessHost?.Stop();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => _mcpProcessHost?.Stop();
            DispatcherUnhandledException += (_, _) => _mcpProcessHost?.Stop();
            Exit += (_, _) => _mcpProcessHost?.Stop();
            AppDomain.CurrentDomain.ProcessExit += (_, _) => _agentCallbackServer?.Stop();
            DispatcherUnhandledException += (_, _) => _agentCallbackServer?.Stop();
            Exit += (_, _) => _agentCallbackServer?.Stop();

            _hotkeyManager = new HotkeyManager(() =>
            {
                Trace.WriteLine("Global hotkey pressed.");
                _overlayController?.ToggleOverlay();
            });

            if (!_hotkeyManager.IsRegistered)
            {
                Trace.TraceWarning("Hotkey registration failed; overlay will only open from tray.");
            }

            _trayIconManager = new TrayIconManager(
                onOpenAssistant: () => _overlayController?.ShowOverlay(),
                onOpenSettings: () =>
                {
                    overlayVm.SelectedMode = AssistantMode.Settings;
                    _overlayController?.ShowOverlay();
                },
                onQuit: ShutdownApplication);

            _mcpHealthChecker = new PortHealthChecker(_hostSettings.McpHost, _hostSettings.McpPort, ready =>
            {
                overlayVm.UpdateMcpStatus(ready);
            });
            _mcpHealthChecker.Start();

            _agentHealthChecker = new PortHealthChecker(_hostSettings.AgentHost, _hostSettings.AgentPort, ready =>
            {
                overlayVm.UpdateAgentStatus(ready);
            }, () => _agentClient!.CheckHealthAsync());
            _agentHealthChecker.Start();

            _providerHealthChecker = new PortHealthChecker(_hostSettings.ProviderHost, _hostSettings.ProviderPort, ready =>
            {
                overlayVm.UpdateProviderStatus(ready);
            });
            _providerHealthChecker.Start();

            overlayVm.SaveSettingsCommand = new Commands.AsyncRelayCommand(async () =>
            {
                if (_settingsService == null || overlayVm.Settings == null) return;
                try
                {
                    await _settingsService.SaveAsync(overlayVm.Settings);
                    _loggingService?.LogEvent("settings.saved", overlayVm.Settings);
                    UpdateVerboseTraceListener(overlayVm.Settings.VerboseLogging);
                }
                catch (Exception ex)
                {
                    overlayVm.StatusText = "Settings save failed";
                    Trace.TraceWarning($"Settings save failed: {ex.Message}");
                    return;
                }

                // Refresh agent client to configured agent host/port and provider key
                _agentClient?.UpdateBaseUri(new Uri($"http://{overlayVm.Settings.AgentHost}:{overlayVm.Settings.AgentPort}"));
                _agentClient?.SetProviderApiKey(overlayVm.Settings.ProviderApiKey);

                // Restart agent process if needed (port change)
                _mcpProcessHost?.Dispose();
                var mcpScriptNew = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "Agent.MCP", "main.py");
                _mcpProcessHost = new McpProcessHost(Path.GetFullPath(mcpScriptNew), port: overlayVm.Settings.McpPort, verboseLogging: overlayVm.Settings.VerboseLogging);
                _mcpProcessHost.Start();
                _agentClient?.SetMcpAuthToken(_mcpProcessHost.AuthToken);
                _agentProcessHost?.Dispose();
                var agentScriptNew = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "Agent.Worker", "main.py");
                _agentProcessHost = new AgentProcessHost(
                    Path.GetFullPath(agentScriptNew),
                    port: overlayVm.Settings.AgentPort,
                    callbackToken: _agentCallbackToken,
                    callbackPort: AgentCallbackPort,
                    mcpAuthToken: _mcpProcessHost.AuthToken,
                    providerApiKey: overlayVm.Settings.ProviderApiKey,
                    verboseLogging: overlayVm.Settings.VerboseLogging);
                _agentProcessHost.Start();
                _agentCallbackServer?.Stop();
                _agentCallbackServer = new TcpCallbackServer(AgentCallbackPort, _agentCallbackToken, callback =>
                {
                    Dispatcher.InvokeAsync(() =>
                    {
                        if (!overlayVm.IsCallbackAuthorized(callback.SessionId, callback.SessionNonce))
                        {
                            return;
                        }
                        if (callback.Phase == "thinking_chunk" || callback.Phase == "thinking_done")
                        {
                            overlayVm.UpdateThinkingStatus(callback.TurnId, callback.Phase, callback.ThinkingDelta);
                        }
                        else if (callback.Phase == "content_chunk" || callback.Phase == "content_done")
                        {
                            overlayVm.UpdateContentStatus(callback.TurnId, callback.Phase, callback.ContentDelta);
                        }
                        else
                        {
                            overlayVm.UpdateToolStatus(callback.TurnId, callback.Phase, callback.ToolCalls);
                        }
                    });
                });
                _agentCallbackServer.Start();
                _mcpHealthChecker?.Dispose();
                _mcpHealthChecker = new PortHealthChecker(overlayVm.Settings.McpHost, overlayVm.Settings.McpPort, ready =>
                {
                    overlayVm.UpdateMcpStatus(ready);
                });
                _mcpHealthChecker.Start();

                _agentHealthChecker?.Dispose();
                _agentHealthChecker = new PortHealthChecker(overlayVm.Settings.AgentHost, overlayVm.Settings.AgentPort, ready =>
                {
                    overlayVm.UpdateAgentStatus(ready);
                }, () => _agentClient!.CheckHealthAsync());
                _agentHealthChecker.Start();

                _providerHealthChecker?.Dispose();
                _providerHealthChecker = new PortHealthChecker(overlayVm.Settings.ProviderHost, overlayVm.Settings.ProviderPort, ready =>
                {
                    overlayVm.UpdateProviderStatus(ready);
                });
                _providerHealthChecker.Start();

                overlayVm.ClearChatHistory();
            });

            Trace.WriteLine("Tray icon initialized.");
        }

        private void ShutdownApplication()
        {
            Trace.WriteLine("Shutdown requested from tray.");
            Shutdown();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            Trace.WriteLine("Host.Win exiting.");
            _mcpHealthChecker?.Dispose();
            _agentHealthChecker?.Dispose();
            _providerHealthChecker?.Dispose();
            _hotkeyManager?.Dispose();
            _trayIconManager?.Dispose();
            _overlayController?.Dispose();
            _agentClient?.Dispose();
            _contextCollector?.Dispose();
            _audioPlaybackService?.Dispose();
            _agentProcessHost?.Dispose();
            _mcpProcessHost?.Dispose();
            _agentCallbackServer?.Dispose();
            UpdateVerboseTraceListener(false);
            base.OnExit(e);
        }

        private void UpdateVerboseTraceListener(bool enabled)
        {
            if (!enabled)
            {
                if (_verboseTraceListener != null)
                {
                    try
                    {
                        Trace.Listeners.Remove(_verboseTraceListener);
                        _verboseTraceListener.Flush();
                        _verboseTraceListener.Close();
                        _verboseTraceListener.Dispose();
                    }
                    catch
                    {
                        // ignore cleanup errors
                    }
                    _verboseTraceListener = null;
                }

                return;
            }

            if (_verboseTraceListener != null)
            {
                return;
            }

            try
            {
                var logsDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LISA", "logs");
                Directory.CreateDirectory(logsDir);
                var tracePath = Path.Combine(logsDir, "verbose-trace.log");
                var stream = new FileStream(tracePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                var writer = new StreamWriter(stream) { AutoFlush = true };
                _verboseTraceListener = new TextWriterTraceListener(writer);
                Trace.Listeners.Add(_verboseTraceListener);
                Trace.AutoFlush = true;
                Trace.WriteLine($"Verbose trace logging enabled: {tracePath}");
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Failed to enable verbose trace logging: {ex.Message}");
            }
        }
    }
}
