using System.Net.Http;
using System.Text;
using BepInEx.Logging;

namespace BepInExMCP.IL2CPP;

internal sealed class WebhookClient : IDisposable
{
    private readonly HttpClient client = new()
    {
        Timeout = TimeSpan.FromSeconds(2)
    };
    private readonly Uri endpoint;
    private readonly ManualLogSource log;

    internal WebhookClient(ManualLogSource log, string host, int port)
    {
        this.log = log;
        endpoint = new UriBuilder(Uri.UriSchemeHttp, host, port, "event").Uri;
    }

    public void Dispose()
    {
        client.Dispose();
    }

    internal async Task SendAsync(object payload)
    {
        try
        {
            var json = Protocol.Serialize(payload);
            using var content = new StringContent(json, Encoding.UTF8, "application/json");
            using var response = await client.PostAsync(endpoint, content).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                log.LogWarning(
                    $"Webhook receiver returned HTTP {(int)response.StatusCode}.");
            }
        }
        catch (Exception exception)
        {
            log.LogWarning($"Could not deliver webhook event: {exception.Message}");
        }
    }
}
