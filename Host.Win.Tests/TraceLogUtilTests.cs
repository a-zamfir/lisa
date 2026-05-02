using System.IO;
using Host.Win.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Host.Win.Tests;

[TestClass]
public class TraceLogUtilTests
{
    [TestMethod]
    public void AppendVerboseDetail_WritesToVerboseTraceLog()
    {
        var tempDir = CreateTempDir();

        TraceLogUtil.AppendVerboseDetail("unit-test", "first line\r\nsecond line", tempDir);

        var tracePath = Path.Combine(tempDir, "verbose-trace.log");
        Assert.IsTrue(File.Exists(tracePath));
        var text = File.ReadAllText(tracePath);
        StringAssert.Contains(text, "[unit-test] raw details @");
        StringAssert.Contains(text, "first line");
        StringAssert.Contains(text, "second line");
    }

    [TestMethod]
    public void AppendVerboseDetail_AllowsSharedReadWriteAccess()
    {
        var tempDir = CreateTempDir();
        var tracePath = Path.Combine(tempDir, "verbose-trace.log");
        Directory.CreateDirectory(tempDir);
        using var lockStream = new FileStream(tracePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);

        TraceLogUtil.AppendVerboseDetail("unit-test", "shared write ok", tempDir);

        lockStream.Position = 0;
        using var reader = new StreamReader(lockStream, leaveOpen: true);
        var text = reader.ReadToEnd();
        StringAssert.Contains(text, "shared write ok");
    }

    [TestMethod]
    public void SummarizeText_UsesTrailingNonEmptyLinesAndTruncates()
    {
        var input = "\r\nalpha\r\n\r\nbeta\r\ngamma\r\n";

        var summary = TraceLogUtil.SummarizeText(input, maxLines: 2, maxChars: 9);

        Assert.AreEqual("beta | ga...", summary);
    }

    private static string CreateTempDir()
    {
        var path = Path.Combine(Path.GetTempPath(), "lisa-tests", Path.GetRandomFileName());
        Directory.CreateDirectory(path);
        return path;
    }
}
