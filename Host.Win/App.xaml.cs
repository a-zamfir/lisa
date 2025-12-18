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
        private HostSettings? _hostSettings;
        private AgentProcessHost? _agentProcessHost;
        private McpProcessHost? _mcpProcessHost;
        private AgentCallbackServer? _agentCallbackServer;
        private const int AgentCallbackPort = 5052;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            Trace.Listeners.Add(new ConsoleTraceListener());
            Trace.WriteLine("Host.Win starting.");

            _settingsService = new SettingsService();
            _hostSettings = _settingsService.LoadAsync().GetAwaiter().GetResult();
            _contextCollector = new ContextCollector();
            _agentClient = new AgentClient(new Uri($"http://{_hostSettings.AgentHost}:{_hostSettings.AgentPort}"));
            _agentClient.SetProviderApiKey(_hostSettings.ProviderApiKey);
            _themeService = new ThemeService();
            _loggingService = new LoggingService();
            var overlayVm = new OverlayViewModel
            {
                AppName = "LISA",
                StatusText = "Checking...",
                AgentClient = _agentClient,
                Logger = _loggingService,
                Settings = _hostSettings
            };
            overlayVm.UpdateMcpStatus(false);
            overlayVm.UpdateAgentStatus(false);
            overlayVm.UpdateProviderStatus(false);

            var initialTheme = _themeService.GetSystemTheme();
            _themeService.ApplyTheme(initialTheme);
            overlayVm.ApplyTheme(initialTheme);

            var overlayWindow = new OverlayWindow { DataContext = overlayVm };
            _overlayController = new OverlayController(overlayWindow, _contextCollector);

            overlayVm.ToggleThemeCommand = new Commands.RelayCommand(() =>
            {
                var newTheme = overlayVm.IsLightTheme ? AppTheme.Dark : AppTheme.Light;
                _themeService.ApplyTheme(newTheme);
                overlayVm.ApplyTheme(newTheme);
            });

            overlayVm.SetModeCommand = new Commands.RelayCommand<AssistantMode>(mode =>
            {
                overlayVm.SelectedMode = mode;
            });

            overlayVm.SendChatCommand = new Commands.AsyncRelayCommand(() => overlayVm.SendChatAsync(), overlayVm.CanSendChat);
            overlayVm.CopyMessageCommand = new Commands.RelayCommand<Models.ChatMessage>(message => overlayVm.CopyMessage(message));
            overlayVm.RetryMessageCommand = new Commands.RelayCommand<Models.ChatMessage>(message => _ = overlayVm.RetryAssistantAsync(message));
            overlayVm.StopMessageCommand = new Commands.RelayCommand<Models.ChatMessage>(message => overlayVm.StopMessage(message));

            overlayVm.SelectedMode = AssistantMode.Chat;

            // From Host.Win/bin/Debug/... back to repo root then into Agent.Worker/main.py
            var agentScript = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "Agent.Worker", "main.py");
            _agentProcessHost = new AgentProcessHost(Path.GetFullPath(agentScript), port: _hostSettings.AgentPort);
            _agentCallbackServer = new AgentCallbackServer(AgentCallbackPort, callback =>
            {
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
            _agentCallbackServer.Start();
            _agentProcessHost.Start();
            var mcpScript = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "Agent.MCP", "main.py");
            _mcpProcessHost = new McpProcessHost(Path.GetFullPath(mcpScript), port: _hostSettings.McpPort);
            _mcpProcessHost.Start();
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
                await _settingsService.SaveAsync(overlayVm.Settings);
                _loggingService?.LogEvent("settings.saved", overlayVm.Settings);

                // Refresh agent client to configured agent host/port and provider key
                _agentClient?.UpdateBaseUri(new Uri($"http://{overlayVm.Settings.AgentHost}:{overlayVm.Settings.AgentPort}"));
                _agentClient?.SetProviderApiKey(overlayVm.Settings.ProviderApiKey);

                // Restart agent process if needed (port change)
                _agentProcessHost?.Dispose();
                var agentScriptNew = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "Agent.Worker", "main.py");
                _agentProcessHost = new AgentProcessHost(Path.GetFullPath(agentScriptNew), port: overlayVm.Settings.AgentPort);
                _agentProcessHost.Start();
                _agentCallbackServer?.Stop();
                _agentCallbackServer = new AgentCallbackServer(AgentCallbackPort, callback =>
                {
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
                _agentCallbackServer.Start();
                _mcpProcessHost?.Dispose();
                var mcpScriptNew = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..", "Agent.MCP", "main.py");
                _mcpProcessHost = new McpProcessHost(Path.GetFullPath(mcpScriptNew), port: overlayVm.Settings.McpPort);
                _mcpProcessHost.Start();
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
            _agentProcessHost?.Dispose();
            _mcpProcessHost?.Dispose();
            _agentCallbackServer?.Dispose();
            base.OnExit(e);
        }
    }
}
