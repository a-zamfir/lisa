// File: Host.Win/Services/SettingsService.cs
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Host.Win.Models;

namespace Host.Win.Services
{
    public sealed class SettingsService
    {
        private readonly string _settingsPath;
        private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };
        private const string ApiKeyPrefix = "dpapi:";

        public SettingsService()
        {
            var basePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LISA");
            Directory.CreateDirectory(basePath);
            _settingsPath = Path.Combine(basePath, "host-settings.json");
        }

        public async Task<HostSettings> LoadAsync()
        {
            if (!File.Exists(_settingsPath))
            {
                var defaults = new HostSettings();
                await SaveAsync(defaults).ConfigureAwait(false);
                return defaults;
            }

            try
            {
                await using var stream = File.OpenRead(_settingsPath);
                var settings = await JsonSerializer.DeserializeAsync<HostSettings>(stream, _jsonOptions).ConfigureAwait(false)
                    ?? new HostSettings();
                settings.ProviderApiKey = DecryptApiKey(settings.ProviderApiKey);
                return settings;
            }
            catch
            {
                return new HostSettings();
            }
        }

        public async Task SaveAsync(HostSettings settings)
        {
            if (!TryEncryptApiKey(settings.ProviderApiKey, out var encryptedApiKey))
            {
                throw new InvalidOperationException("DPAPI encryption failed; settings not saved.");
            }

            var toSave = NormalizeForSave(settings, encryptedApiKey);
            await using var stream = File.Create(_settingsPath);
            await JsonSerializer.SerializeAsync(stream, toSave, _jsonOptions).ConfigureAwait(false);
        }

        private static HostSettings NormalizeForSave(HostSettings settings, string encryptedApiKey)
        {
            return new HostSettings
            {
                McpHost = settings.McpHost,
                McpPort = settings.McpPort,
                AgentHost = settings.AgentHost,
                AgentPort = settings.AgentPort,
                ProviderType = settings.ProviderType,
                ProviderMode = settings.ProviderMode,
                ProviderHost = settings.ProviderHost,
                ProviderPort = settings.ProviderPort,
                ProviderApiKey = encryptedApiKey,
                ProviderModel = settings.ProviderModel,
                ProviderTemperature = settings.ProviderTemperature,
                ProviderThink = settings.ProviderThink,
                VoiceType = settings.VoiceType,
                VoiceRate = settings.VoiceRate,
                VoiceVolume = settings.VoiceVolume,
                TtsEnabledInChat = settings.TtsEnabledInChat,
                PiperExePath = settings.PiperExePath,
                PiperMode = settings.PiperMode,
                PiperPythonPath = settings.PiperPythonPath,
                PiperVoiceModelPath = settings.PiperVoiceModelPath,
                PiperVoiceConfigPath = settings.PiperVoiceConfigPath,
                PiperSpeakerId = settings.PiperSpeakerId
            };
        }

        private static bool TryEncryptApiKey(string? apiKey, out string encrypted)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                encrypted = string.Empty;
                return true;
            }

            if (apiKey.StartsWith(ApiKeyPrefix, StringComparison.Ordinal))
            {
                encrypted = apiKey;
                return true;
            }

            try
            {
                var bytes = Encoding.UTF8.GetBytes(apiKey);
                var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
                encrypted = $"{ApiKeyPrefix}{Convert.ToBase64String(protectedBytes)}";
                return true;
            }
            catch
            {
                encrypted = string.Empty;
                return false;
            }
        }

        private static string DecryptApiKey(string? apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return string.Empty;
            }

            if (!apiKey.StartsWith(ApiKeyPrefix, StringComparison.Ordinal))
            {
                return apiKey;
            }

            try
            {
                var payload = apiKey.Substring(ApiKeyPrefix.Length);
                var bytes = Convert.FromBase64String(payload);
                var unprotected = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(unprotected);
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
