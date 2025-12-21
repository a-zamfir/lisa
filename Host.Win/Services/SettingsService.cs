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
            var toSave = NormalizeForSave(settings);
            await using var stream = File.Create(_settingsPath);
            await JsonSerializer.SerializeAsync(stream, toSave, _jsonOptions).ConfigureAwait(false);
        }

        private static HostSettings NormalizeForSave(HostSettings settings)
        {
            return new HostSettings
            {
                McpHost = settings.McpHost,
                McpPort = settings.McpPort,
                AgentHost = settings.AgentHost,
                AgentPort = settings.AgentPort,
                ProviderMode = settings.ProviderMode,
                ProviderHost = settings.ProviderHost,
                ProviderPort = settings.ProviderPort,
                ProviderApiKey = EncryptApiKey(settings.ProviderApiKey),
                ProviderModel = settings.ProviderModel,
                ProviderTemperature = settings.ProviderTemperature,
                ProviderThink = settings.ProviderThink,
                VoiceType = settings.VoiceType,
                VoiceRate = settings.VoiceRate,
                VoiceVolume = settings.VoiceVolume,
                PiperExePath = settings.PiperExePath,
                PiperMode = settings.PiperMode,
                PiperPythonPath = settings.PiperPythonPath,
                PiperVoiceModelPath = settings.PiperVoiceModelPath,
                PiperVoiceConfigPath = settings.PiperVoiceConfigPath,
                PiperSpeakerId = settings.PiperSpeakerId
            };
        }

        private static string EncryptApiKey(string? apiKey)
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return string.Empty;
            }

            if (apiKey.StartsWith(ApiKeyPrefix, StringComparison.Ordinal))
            {
                return apiKey;
            }

            try
            {
                var bytes = Encoding.UTF8.GetBytes(apiKey);
                var protectedBytes = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
                return $"{ApiKeyPrefix}{Convert.ToBase64String(protectedBytes)}";
            }
            catch
            {
                return apiKey;
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
