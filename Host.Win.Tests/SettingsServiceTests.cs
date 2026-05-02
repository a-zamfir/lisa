using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using Host.Win.Models;
using Host.Win.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Host.Win.Tests;

[TestClass]
public class SettingsServiceTests
{
    [TestMethod]
    public async Task SaveAndLoad_RoundTripsSettings_AndDecryptsApiKey()
    {
        var tempDir = CreateTempDir();
        var settingsPath = Path.Combine(tempDir, "host-settings.json");
        var service = new SettingsService(settingsPath);
        var settings = new HostSettings
        {
            ProviderType = "OpenAI",
            ProviderApiKey = "secret-key",
            ProviderModel = "gpt-test",
            MemoryEnabled = true,
        };

        await service.SaveAsync(settings);
        var raw = await File.ReadAllTextAsync(settingsPath);
        var loaded = await service.LoadAsync();

        StringAssert.Contains(raw, "dpapi:");
        Assert.AreEqual("secret-key", loaded.ProviderApiKey);
        Assert.AreEqual("OpenAI", loaded.ProviderType);
        Assert.AreEqual("gpt-test", loaded.ProviderModel);
        Assert.IsTrue(loaded.MemoryEnabled);
    }

    [TestMethod]
    public async Task LoadAsync_MigratesMissingVersion()
    {
        var tempDir = CreateTempDir();
        var settingsPath = Path.Combine(tempDir, "host-settings.json");
        await File.WriteAllTextAsync(
            settingsPath,
            JsonSerializer.Serialize(new
            {
                ProviderType = "LM Studio",
                ProviderModel = "test-model",
                ProviderApiKey = string.Empty,
            }));

        var service = new SettingsService(settingsPath);
        var loaded = await service.LoadAsync();

        Assert.AreEqual("0.1.0-alpha", loaded.SettingsVersion);
        Assert.AreEqual("LM Studio", loaded.ProviderType);
        Assert.AreEqual("test-model", loaded.ProviderModel);
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "lisa-tests", Path.GetRandomFileName());
        Directory.CreateDirectory(path);
        return path;
    }
}
