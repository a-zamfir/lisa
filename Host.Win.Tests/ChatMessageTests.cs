using Host.Win.Models;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Host.Win.Tests;

[TestClass]
public class ChatMessageTests
{
    [TestMethod]
    public void AppendContentChunk_CreatesAndAppendsTextSegment()
    {
        var message = new ChatMessage();

        message.AppendContentChunk("Hello");
        message.AppendContentChunk(" world");

        Assert.AreEqual(1, message.ContentSegments.Count);
        Assert.AreEqual("Hello world", message.ContentSegments[0].Text);
    }

    [TestMethod]
    public void AddToolApprovalLabel_SetsApprovalState()
    {
        var message = new ChatMessage();

        message.AddToolApprovalLabel("system_overview", approved: true);

        Assert.IsTrue(message.HasToolApprovals);
        Assert.AreEqual(1, message.ContentSegments.Count);
        Assert.IsTrue(message.ContentSegments[0].IsApproval);
    }

    [TestMethod]
    public void Text_DetectsMarkdownMarkers()
    {
        var message = new ChatMessage();

        message.Text = "### Heading\n- item\n**bold**";

        Assert.IsTrue(message.HasMarkdown);
    }

    [TestMethod]
    public void KeepOnlyToolApprovals_RemovesTextSegments()
    {
        var message = new ChatMessage();
        message.AppendContentChunk("hello");
        message.AddToolApprovalLabel("tool", approved: false);

        message.KeepOnlyToolApprovals();

        Assert.AreEqual(1, message.ContentSegments.Count);
        Assert.IsTrue(message.ContentSegments[0].IsApproval);
    }
}
