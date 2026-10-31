namespace AngleSharp.Browser.Tests;

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

/// <summary>
/// A minimal HTTP server serving fixed resources from localhost.
/// </summary>
sealed class TestServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Dictionary<string, (string Type, byte[] Content)> _resources;

    public TestServer(Dictionary<string, (string Type, byte[] Content)> resources)
    {
        _resources = resources;
        Address = $"http://localhost:{GetFreePort()}/";
        _listener.Prefixes.Add(Address);
        _listener.Start();
        _ = ServeAsync();
    }

    public string Address { get; }

    public string? LastUserAgent { get; private set; }

    public System.Collections.Specialized.NameValueCollection? LastHeaders { get; private set; }

    public void Dispose() => _listener.Close();

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;

            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception)
            {
                return;
            }

            var response = context.Response;
            LastUserAgent = context.Request.UserAgent;
            LastHeaders = context.Request.Headers;

            if (_resources.TryGetValue(context.Request.Url!.AbsolutePath, out var resource))
            {
                response.ContentType = resource.Type;
                await response.OutputStream.WriteAsync(resource.Content);
            }
            else
            {
                response.StatusCode = 404;
            }

            response.Close();
        }
    }

    private static int GetFreePort()
    {
        using var socket = new TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        return ((IPEndPoint)socket.LocalEndpoint).Port;
    }
}
