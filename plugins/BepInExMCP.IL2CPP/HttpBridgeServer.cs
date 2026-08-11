using System.Net;
using System.Text;
using BepInEx.Logging;

namespace BepInExMCP.IL2CPP;

internal sealed class HttpBridgeServer : IAsyncDisposable
{
    private const int MaxRawUrlLength = 65_536;
    private readonly CommandRouter router;
    private readonly ManualLogSource log;
    private readonly HttpListener listener = new();
    private readonly CancellationTokenSource shutdown = new();
    private Task? serverTask;

    internal HttpBridgeServer(
        CommandRouter router,
        ManualLogSource log,
        string listenAddress,
        int listenPort)
    {
        this.router = router;
        this.log = log;

        if (listenPort is < 1 or > 65_535)
        {
            throw new ArgumentOutOfRangeException(
                nameof(listenPort),
                listenPort,
                "The listen port must be between 1 and 65535.");
        }

        listener.Prefixes.Add($"http://{listenAddress}:{listenPort}/mcp/");
    }

    internal void Start()
    {
        if (serverTask is not null)
        {
            throw new InvalidOperationException("The HTTP bridge is already running.");
        }

        listener.Start();
        serverTask = Task.Run(() => AcceptLoopAsync(shutdown.Token));
        log.LogInfo($"IL2CPP MCP bridge listening on {listener.Prefixes.Single()}");
    }

    internal async Task StopAsync()
    {
        shutdown.Cancel();

        if (listener.IsListening)
        {
            listener.Stop();
        }

        listener.Close();

        if (serverTask is null)
        {
            return;
        }

        try
        {
            await serverTask.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (TimeoutException)
        {
            log.LogWarning("Timed out waiting for the HTTP bridge to stop.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        shutdown.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested && listener.IsListening)
            {
                HttpListenerContext context;

                try
                {
                    context = await listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (HttpListenerException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                _ = HandleContextAsync(context, cancellationToken);
            }
        }
        catch (Exception exception)
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                log.LogError($"IL2CPP MCP bridge accept loop failed: {exception}");
            }
        }
        finally
        {
            log.LogInfo("IL2CPP MCP bridge stopped.");
        }
    }

    private async Task HandleContextAsync(
        HttpListenerContext context,
        CancellationToken cancellationToken)
    {
        ApiResponse response;

        try
        {
            var rawUrl = context.Request.RawUrl ?? string.Empty;
            if (rawUrl.Length > MaxRawUrlLength)
            {
                response = ApiResponse.Failure(
                    414,
                    $"Request URL exceeds the {MaxRawUrlLength}-character limit.",
                    "request_too_large");
            }
            else if (context.Request.ContentLength64 > Protocol.MaxRequestBodyBytes)
            {
                response = ApiResponse.Failure(
                    413,
                    $"Request body exceeds the {Protocol.MaxRequestBodyBytes}-byte limit.",
                    "request_too_large");
            }
            else
            {
                log.LogDebug(
                    $"{context.Request.HttpMethod} {context.Request.Url?.AbsolutePath}");
                var requestBody = await ReadRequestBodyAsync(context.Request)
                    .ConfigureAwait(false);
                response = await router
                    .HandleAsync(context.Request, requestBody, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            log.LogError($"Unhandled HTTP request failure: {exception}");
            response = ApiResponse.Failure(500, "Unhandled bridge failure.", "internal_error");
        }

        try
        {
            var buffer = Encoding.UTF8.GetBytes(response.Body);
            context.Response.StatusCode = response.StatusCode;
            context.Response.ContentType = ApiResponse.ContentType;
            context.Response.ContentEncoding = Encoding.UTF8;
            context.Response.ContentLength64 = buffer.Length;
            await context.Response.OutputStream
                .WriteAsync(buffer, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is HttpListenerException or IOException or OperationCanceledException)
        {
            log.LogDebug($"Client disconnected before the response completed: {exception.Message}");
        }
        finally
        {
            context.Response.OutputStream.Close();
            context.Response.Close();
        }
    }

    private static async Task<string?> ReadRequestBodyAsync(HttpListenerRequest request)
    {
        if (!request.HasEntityBody)
        {
            return null;
        }

        using var reader = new StreamReader(
            request.InputStream,
            request.ContentEncoding ?? Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 8_192,
            leaveOpen: true);
        var body = await reader.ReadToEndAsync().ConfigureAwait(false);
        if (Encoding.UTF8.GetByteCount(body) > Protocol.MaxRequestBodyBytes)
        {
            throw new ArgumentException(
                $"Request body exceeds the {Protocol.MaxRequestBodyBytes}-byte limit.");
        }

        return body;
    }
}
