// File: Host.Win/Services/AgentClient.cs
using System;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using Host.Win.Models;
using System.Text.Json;
using System.Net.Http.Headers;
using System.Text;

namespace Host.Win.Services
{
    /// <summary>
    /// Lightweight HTTP client for the local Python agent.
    /// </summary>
    public sealed class AgentClient : IDisposable
    {
        private static readonly HttpClient SharedClient = CreateClient();
        private Uri _baseUri;
        private string? _providerApiKey;

        public AgentClient(Uri baseUri)
        {
            _baseUri = baseUri;
        }

        public void UpdateBaseUri(Uri baseUri)
        {
            _baseUri = baseUri;
        }

        public void SetProviderApiKey(string? apiKey)
        {
            _providerApiKey = string.IsNullOrWhiteSpace(apiKey) ? null : apiKey;
        }

        public async Task<bool> CheckHealthAsync(CancellationToken cancellationToken = default)
        {
            try
            {
                var response = await SharedClient.GetAsync(new Uri(_baseUri, "/health"), cancellationToken).ConfigureAwait(false);
                return response.IsSuccessStatusCode;
            }
            catch (Exception ex)
            {
                Trace.TraceError($"Health check failed: {ex.Message}");
                return false;
            }
        }

        public async Task<AgentResponse> SendTextAsync(TextInputRequest request, CancellationToken cancellationToken = default)
        {
            try
            {
                Trace.WriteLine($"Sending text input turn_id={request.TurnId}, session_id={request.SessionId}");
                using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(_baseUri, "/input/text"))
                {
                    Content = JsonContent.Create(request)
                };
                if (!string.IsNullOrWhiteSpace(_providerApiKey))
                {
                    message.Headers.Add("X-Provider-Api-Key", _providerApiKey);
                }

                var response = await SharedClient.SendAsync(message, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var payload = await response.Content.ReadFromJsonAsync<AgentResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
                if (payload != null)
                {
                    Trace.WriteLine($"Received response turn_id={payload.TurnId}, session_id={payload.SessionId}");
                    return payload;
                }
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Text send failed, returning mock response: {ex.Message}");
            }

            await Task.Delay(300, cancellationToken).ConfigureAwait(false);
            return CreateMockResponse(request);
        }

        public async Task<AudioInputResponse?> SendAudioAsync(byte[] wavBytes, AudioInputMeta meta, CancellationToken cancellationToken = default)
        {
            try
            {
                using var content = new MultipartFormDataContent();
                var audioContent = new ByteArrayContent(wavBytes);
                audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
                content.Add(audioContent, "audio", "input.wav");

                var metaJson = JsonSerializer.Serialize(meta);
                content.Add(new StringContent(metaJson, Encoding.UTF8, "application/json"), "meta");

                var response = await SharedClient.PostAsync(new Uri(_baseUri, "/input/audio"), content, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<AudioInputResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Audio send failed: {ex.Message}");
                return null;
            }
        }

        public async Task<AgentResponse?> SendRetryAsync(RetryRequest request, CancellationToken cancellationToken = default)
        {
            try
            {
                Trace.WriteLine($"Sending retry turn_id={request.TurnId}, session_id={request.SessionId}");
                var response = await SharedClient.PostAsJsonAsync(new Uri(_baseUri, "/input/retry"), request, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var payload = await response.Content.ReadFromJsonAsync<AgentResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
                if (payload != null)
                {
                    Trace.WriteLine($"Received retry response turn_id={payload.TurnId}, session_id={payload.SessionId}");
                    return payload;
                }
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"Retry send failed: {ex.Message}");
            }
            return null;
        }

        public async Task<IntentResponse?> SendIntentAsync(IntentRequest request, CancellationToken cancellationToken = default)
        {
            try
            {
                var response = await SharedClient.PostAsJsonAsync(new Uri(_baseUri, "/intent"), request, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<IntentResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Trace.TraceError($"Intent call failed: {ex.Message}");
                return new IntentResponse { Success = false, Message = "Agent unavailable" };
            }
        }

        public void Dispose()
        {
            // Shared HttpClient intentionally not disposed; process scoped.
        }

        private static HttpClient CreateClient()
        {
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5),
                MaxConnectionsPerServer = 4
            };
            return new HttpClient(handler, disposeHandler: true);
        }

        private static AgentResponse CreateMockResponse(TextInputRequest request)
        {
            return new AgentResponse
            {
                SessionId = request.SessionId,
                TurnId = request.TurnId,
                Messages =
                {
                    new AgentMessage
                    {
                        Role = "assistant",
                        Content = $"(mock) Received: \"{request.Text}\""
                    }
                },
                Speak = false,
                TtsText = null
            };
        }
    }
}
