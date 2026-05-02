using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Host.Win.Models;
using Host.Win.Services;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Host.Win.Tests;

[TestClass]
public class AgentClientTests
{
    [TestMethod]
    public async Task SendTextAsync_ReturnsNull_WhenCanceled()
    {
        using var handler = new StubHandler(async (_, ct) =>
        {
            await Task.Delay(5000, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var httpClient = new HttpClient(handler);
        using var agentClient = new AgentClient(new Uri("http://127.0.0.1:5050"), httpClient);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await agentClient.SendTextAsync(new TextInputRequest
        {
            SessionId = "s1",
            TurnId = "t1",
            Text = "hi",
        }, cts.Token);

        Assert.IsNull(result);
    }

    [TestMethod]
    public async Task SendTextAsync_ReturnsMockResponse_WhenRequestFails()
    {
        using var handler = new StubHandler((_, _) => throw new HttpRequestException("offline"));
        using var httpClient = new HttpClient(handler);
        using var agentClient = new AgentClient(new Uri("http://127.0.0.1:5050"), httpClient);

        var result = await agentClient.SendTextAsync(new TextInputRequest
        {
            SessionId = "s2",
            TurnId = "t2",
            Text = "hello",
        });

        Assert.IsNotNull(result);
        Assert.AreEqual("s2", result.SessionId);
        Assert.AreEqual("t2", result.TurnId);
        Assert.AreEqual("(mock) Received: \"hello\"", result.Messages[0].Content);
    }

    [TestMethod]
    public async Task SendRetryAsync_ReturnsNull_WhenCanceled()
    {
        using var handler = new StubHandler(async (_, ct) =>
        {
            await Task.Delay(5000, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var httpClient = new HttpClient(handler);
        using var agentClient = new AgentClient(new Uri("http://127.0.0.1:5050"), httpClient);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await agentClient.SendRetryAsync(new RetryRequest
        {
            SessionId = "s3",
            TurnId = "t3",
        }, cts.Token);

        Assert.IsNull(result);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _handler;

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _handler(request, cancellationToken);
        }
    }
}
