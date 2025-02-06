using System;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using BepInEx.Logging;

namespace WebRequestMask.Core;

public class UrlProxy
{
    private static ManualLogSource Log;
    private static readonly string validToken = Guid.NewGuid().ToString();
    private static readonly StringComparison ignoreCase = StringComparison.OrdinalIgnoreCase;

    private readonly HttpListener listener = new();
    private readonly object lockObj = new();

    private HttpClient proxyClient = null;

    public int Port { get; private set; }
    private string currPrefix = null;

    private bool started = false;

    private Task task = null;

    public UrlProxy(ManualLogSource Log, int initPort = -1)
    {
        UrlProxy.Log = Log;
        Port = initPort;
    }

    public bool HasHttpProxy()
    {
        return proxyClient != null;
    }

    public void UseHttpProxy(string httpProxy)
    {
        if (string.IsNullOrWhiteSpace(httpProxy))
        {
            proxyClient = null;
            return;
        }

        var handler = new HttpClientHandler
        {
            PreAuthenticate = false,
            UseCookies = false,
            ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator,
            AutomaticDecompression = DecompressionMethods.None,
            Proxy = new WebProxy(httpProxy, false),
            UseProxy = true,
        };

        proxyClient = new HttpClient(handler);
    }

    static int FreeTcpPort()
    {
        TcpListener l = new(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private bool StartListener()
    {
        // loop in case of port allocation racing
        for (int i = 0; i < 10; i++)
        {
            try
            {
                int tryPort;
                if (Port > 0)
                {
                    // try previous port
                    tryPort = Port;
                    Port = -1;
                }
                else
                {
                    tryPort = FreeTcpPort();
                }

                var prefix = $"http://{IPAddress.Loopback}:{tryPort}/";
                listener.Prefixes.Clear();
                listener.Prefixes.Add(prefix);
                listener.Start();

                currPrefix = prefix;
                Port = tryPort;
                return true;
            }
            catch (Exception) { }

            Thread.Sleep(100);
        }

        return false;
    }

    private class RawHeaders : WebHeaderCollection
    {
        // Transfer-Encoding is only informational from HttpClient response so skip this
        private readonly string[] skipHeaders = ["Transfer-Encoding", "Content-Length"];

        public RawHeaders(HttpHeaders headers)
        {
            foreach (var header in headers)
            {
                if (skipHeaders.Any((h) => string.Equals(header.Key, h, ignoreCase)))
                    continue;

                var hasValue = false;

                foreach (var value in header.Value)
                {
                    hasValue = true;
                    AddWithoutValidate(header.Key, value);
                }
                if (!hasValue)
                {
                    AddWithoutValidate(header.Key, null);
                }
            }
        }
    }

    private async Task HandleRequest(HttpListenerContext ctx)
    {
        try
        {
            var client = proxyClient;
            if (client != null)
            {
                await HandleRequest_(client, ctx);
            }
            else
            {
                ResponseOk(ctx);
            }
        }
        catch (Exception e)
        {
            Log.LogWarning(e);
        }
    }

    private static void ResponseOk(HttpListenerContext ctx)
    {
        using var res = ctx.Response;
        res.StatusCode = (int)HttpStatusCode.OK;
        res.StatusDescription = "OK";
        res.SendChunked = false;
    }

    private static async Task HandleRequest_(HttpClient proxyClient, HttpListenerContext ctx)
    {
        var req = ctx.Request;
        using var res = ctx.Response;

        var query = req.QueryString;
        var token = query.Get("token");
        var originalUrl = query.Get("url");

        if (string.IsNullOrEmpty(originalUrl) || token != validToken)
        {
            res.StatusCode = (int)HttpStatusCode.Forbidden;
            res.StatusDescription = "Forbidden";
            res.SendChunked = false;
            return;
        }

        var proxyReq = new HttpRequestMessage(new(req.HttpMethod), originalUrl);

        if (req.HasEntityBody)
        {
            proxyReq.Content = new StreamContent(req.InputStream);
        }

        foreach (var key in req.Headers.AllKeys)
        {
            var values = req.Headers.GetValues(key);

            if (string.Equals(key, "Host", ignoreCase))
            {
                continue;
            }

            if (key.StartsWith("Content-", ignoreCase))
            {
                proxyReq.Content?.Headers.TryAddWithoutValidation(key, values);
            }
            else
            {
                proxyReq.Headers.TryAddWithoutValidation(key, values);
            }
        }

        var proxyRes = await proxyClient.SendAsync(proxyReq);

        res.StatusCode = (int)proxyRes.StatusCode;
        res.StatusDescription = proxyRes.ReasonPhrase ?? "";

        res.Headers.Add(new RawHeaders(proxyRes.Headers));
        res.Headers.Add(new RawHeaders(proxyRes.Content.Headers));

        // need to set this so that HttpListenerResponse knows how much to read into OutputStream
        res.ContentLength64 = proxyRes.Content.Headers.ContentLength ?? 0;

        await proxyRes.Content.CopyToAsync(res.OutputStream);
        await res.OutputStream.FlushAsync();
    }

    public async Task Accept()
    {
        // XXX: bound tasks with bounded channel
        while (started)
        {
            try
            {
                var ctx = await listener.GetContextAsync();
                var _ = HandleRequest(ctx).ConfigureAwait(false);
            }
            catch (Exception e)
            {
                Log.LogWarning(e);
            }
        }
    }

    public void Start()
    {
        lock (lockObj)
        {
            if (started)
                return;

            if (!StartListener())
            {
                Log.LogError("Failed to allocate a HTTP port");
                return;
            }

            Log.LogInfo($"Start proxy server {currPrefix}?token={validToken}");

            started = true;

            task = Task.Run(Accept);
        }
    }

    public void Stop()
    {
        lock (lockObj)
        {
            if (!started)
                return;

            Log.LogDebug($"Stopping proxy server");

            started = false;

            task.Wait();

            listener.Stop();

            currPrefix = null;
            Port = -1;
        }
    }

    public string ProxyUrl(string original)
    {
        if (!started || currPrefix == null)
            return original;

        var encodedUrl = HttpUtility.UrlEncode(original);

        return $"{currPrefix}?url={encodedUrl}&token={validToken}";
    }

    ~UrlProxy()
    {
        Stop();
        ((IDisposable)listener).Dispose();
    }
}
