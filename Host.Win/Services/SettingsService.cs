// File: Host.Win/Services/SettingsService.cs
using System;
using System.IO;
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
                var settings = await JsonSerializer.DeserializeAsync<HostSettings>(stream, _jsonOptions).ConfigureAwait(false);
                return settings ?? new HostSettings();
            }
            catch
            {
                return new HostSettings();
            }
        }

        public async Task SaveAsync(HostSettings settings)
        {
            await using var stream = File.Create(_settingsPath);
            await JsonSerializer.SerializeAsync(stream, settings, _jsonOptions).ConfigureAwait(false);
        }
    }
}
