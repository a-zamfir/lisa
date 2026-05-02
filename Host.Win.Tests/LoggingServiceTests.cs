using System.IO;
using System.Linq;
using System.Text.Json;
using Host.Win.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Host.Win.Tests;

[TestClass]
public class LoggingServiceTests
{
    [TestMethod]
    public void LogEvent_RedactsSensitiveKeys()
    {
        var tempDir = CreateTempDir();
        var service = new LoggingService(tempDir);

        service.LogEvent("request.test", new
        {
            providerApiKey = "top-secret",
            nested = new
            {
                authorization = "Bearer abc",
                safe = "visible"
            }
        });

        var line = File.ReadLines(Path.Combine(tempDir, "host.log")).Single();
        using var doc = JsonDocument.Parse(line);
        var payload = doc.RootElement.GetProperty("payload");

        Assert.AreEqual("(redacted)", payload.GetProperty("providerApiKey").GetString());
        Assert.AreEqual("(redacted)", payload.GetProperty("nested").GetProperty("authorization").GetString());
        Assert.AreEqual("visible", payload.GetProperty("nested").GetProperty("safe").GetString());
    }

    [TestMethod]
    public void LogEvent_Rotates_WhenFileExceedsLimit()
    {
        var tempDir = CreateTempDir();
        var logPath = Path.Combine(tempDir, "host.log");
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(logPath, new string('x', 10 * 1024 * 1024 + 128));
        var service = new LoggingService(tempDir);

        service.LogEvent("rotation.test", new { ok = true });

        Assert.IsTrue(File.Exists(logPath));
        Assert.IsTrue(File.Exists(logPath + ".1"));
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "lisa-tests", Path.GetRandomFileName());
        Directory.CreateDirectory(path);
        return path;
    }
}
