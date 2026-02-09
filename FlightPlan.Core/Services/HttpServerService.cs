using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;

namespace FlightPlan.Services;

/// <summary>
/// Reusable HTTP server for serving local content
/// </summary>
public class HttpServerService : IDisposable
{
    private readonly HttpListener _listener;
    private readonly int _port;
    private bool _isRunning;

    public delegate Task RequestHandler(HttpListenerContext context);
    
    public RequestHandler? HandleRequest { get; set; }
    
    public string BaseUrl => $"http://localhost:{_port}/";
    
    public bool IsRunning => _isRunning;

    public HttpServerService(int port)
    {
        _port = port;
        _listener = new HttpListener();
        _listener.Prefixes.Add(BaseUrl);
    }

    /// <summary>
    /// Start the HTTP server
    /// </summary>
    /// <param name="openBrowser">Automatically open the browser</param>
    /// <param name="cancellationToken">Cancellation token to stop the server</param>
    public async Task StartAsync(bool openBrowser = false, CancellationToken cancellationToken = default)
    {
        try
        {
            _listener.Start();
            _isRunning = true;
            
            Console.WriteLine($"🚀 Server started at {BaseUrl}");
            
            if (openBrowser)
            {
                OpenBrowser(BaseUrl);
            }

            while (!cancellationToken.IsCancellationRequested && _listener.IsListening)
            {
                try
                {
                    var context = await _listener.GetContextAsync();
                    
                    // Handle request in background
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            if (HandleRequest != null)
                            {
                                await HandleRequest(context);
                            }
                            else
                            {
                                // Default 404 response
                                context.Response.StatusCode = 404;
                                context.Response.Close();
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"⚠️  Request error: {ex.Message}");
                            try
                            {
                                context.Response.StatusCode = 500;
                                context.Response.Close();
                            }
                            catch { }
                        }
                    }, cancellationToken);
                }
                catch (HttpListenerException) when (cancellationToken.IsCancellationRequested || !_listener.IsListening)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
            }
        }
        catch (HttpListenerException ex)
        {
            Console.Error.WriteLine($"❌ Failed to start server on port {_port}: {ex.Message}");
            Console.Error.WriteLine($"   Try a different port.");
            throw;
        }
        finally
        {
            _isRunning = false;
        }
    }

    /// <summary>
    /// Stop the HTTP server
    /// </summary>
    public void Stop()
    {
        if (_listener.IsListening)
        {
            _listener.Stop();
            _isRunning = false;
            Console.WriteLine("\n✔ Server stopped.");
        }
    }

    /// <summary>
    /// Open the URL in the system's default browser
    /// </summary>
    public static void OpenBrowser(string url)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", url);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Process.Start("xdg-open", url);
            }
            else
            {
                Console.WriteLine($"⚠️  Could not auto-open browser. Please open manually: {url}");
            }
        }
        catch (Exception)
        {
            Console.WriteLine($"⚠️  Could not auto-open browser. Please open manually: {url}");
        }
    }

    /// <summary>
    /// Log an HTTP request
    /// </summary>
    public static void LogRequest(HttpListenerRequest request, int statusCode)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        var method = request.HttpMethod;
        var path = request.Url?.AbsolutePath ?? "/";
        var statusSymbol = statusCode switch
        {
            200 => "✔",
            302 => "↪",
            404 => "⚠️",
            500 => "❌",
            _ => "•"
        };
        
        Console.WriteLine($"{statusSymbol} [{timestamp}] {method} {path} → {statusCode}");
    }

    /// <summary>
    /// Send a simple text response
    /// </summary>
    public static void SendTextResponse(HttpListenerResponse response, string text, string contentType = "text/plain", int statusCode = 200)
    {
        response.StatusCode = statusCode;
        response.ContentType = contentType;
        var buffer = Encoding.UTF8.GetBytes(text);
        response.ContentLength64 = buffer.Length;
        response.OutputStream.Write(buffer, 0, buffer.Length);
        response.OutputStream.Close();
    }

    /// <summary>
    /// Send a redirect response
    /// </summary>
    public static void SendRedirect(HttpListenerResponse response, string location)
    {
        response.StatusCode = 302;
        response.RedirectLocation = location;
        response.ContentLength64 = 0;
        response.OutputStream.Close();
    }

    public void Dispose()
    {
        Stop();
        _listener.Close();
    }
}
