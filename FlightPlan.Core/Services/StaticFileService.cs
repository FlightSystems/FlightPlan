using System.Net;
using System.Text;

namespace FlightPlan.Services;

/// <summary>
/// Service for serving static files with content type detection
/// </summary>
public class StaticFileService(DirectoryInfo baseDirectory)
{
    private readonly DirectoryInfo _baseDirectory = baseDirectory;
    
    /// <summary>
    /// Mapping of file extensions to content types
    /// </summary>
    private static readonly Dictionary<string, string> ContentTypes = new()
    {
        { ".html", "text/html; charset=utf-8" },
        { ".htm", "text/html; charset=utf-8" },
        { ".css", "text/css" },
        { ".js", "application/javascript" },
        { ".json", "application/json" },
        { ".xml", "application/xml" },
        { ".txt", "text/plain" },
        { ".md", "text/markdown" },
        { ".markdown", "text/markdown" },
        { ".yaml", "text/yaml" },
        { ".yml", "text/yaml" },
        { ".png", "image/png" },
        { ".jpg", "image/jpeg" },
        { ".jpeg", "image/jpeg" },
        { ".gif", "image/gif" },
        { ".svg", "image/svg+xml" },
        { ".ico", "image/x-icon" },
        { ".woff", "font/woff" },
        { ".woff2", "font/woff2" },
        { ".ttf", "font/ttf" },
        { ".eot", "application/vnd.ms-fontobject" },
        { ".pdf", "application/pdf" },
        { ".zip", "application/zip" }
    };

    /// <summary>
    /// Get the content type for a file based on its extension
    /// </summary>
    public static string GetContentType(string filePath)
    {
        var extension = Path.GetExtension(filePath).ToLowerInvariant();
        return ContentTypes.TryGetValue(extension, out var contentType) 
            ? contentType 
            : "application/octet-stream";
    }

    /// <summary>
    /// Resolve a URL path to a file system path, with security checks
    /// </summary>
    /// <param name="urlPath">The URL path (e.g., "/docs/index.html")</param>
    /// <param name="fullPath">The resolved full file system path</param>
    /// <returns>True if the path is valid and within the base directory</returns>
    public bool TryResolvePath(string urlPath, out string fullPath)
    {
        // Normalize path
        urlPath = WebUtility.UrlDecode(urlPath);
        var safePath = urlPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
        fullPath = Path.GetFullPath(Path.Combine(_baseDirectory.FullName, safePath));
        
        // Security: prevent directory traversal
        return fullPath.StartsWith(_baseDirectory.FullName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Serve a file from disk
    /// </summary>
    public void ServeFile(string filePath, HttpListenerResponse response)
    {
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("File not found", filePath);
        }

        var contentType = GetContentType(filePath);
        var buffer = File.ReadAllBytes(filePath);
        
        response.ContentType = contentType;
        response.ContentLength64 = buffer.Length;
        response.StatusCode = 200;
        response.OutputStream.Write(buffer, 0, buffer.Length);
        response.OutputStream.Close();
    }

    /// <summary>
    /// Check if a path is a directory and try to find an index file
    /// </summary>
    /// <param name="directoryPath">The directory path</param>
    /// <param name="indexFile">The found index file name (e.g., "index.html", "index.md")</param>
    /// <returns>True if an index file was found</returns>
    public static bool TryFindIndexFile(string directoryPath, out string? indexFile)
    {
        indexFile = null;
        
        if (!Directory.Exists(directoryPath))
        {
            return false;
        }

        var indexFiles = new[] { "index.html", "index.md", "readme.md", "README.md" };
        
        foreach (var file in indexFiles)
        {
            var indexPath = Path.Combine(directoryPath, file);
            if (File.Exists(indexPath))
            {
                indexFile = file;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Send a 404 Not Found error response
    /// </summary>
    public static void Send404(HttpListenerResponse response, string message = "File not found")
    {
        response.StatusCode = 404;
        var html = BuildErrorPage("404 Not Found", message);
        var buffer = Encoding.UTF8.GetBytes(html);
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = buffer.Length;
        response.OutputStream.Write(buffer, 0, buffer.Length);
        response.OutputStream.Close();
    }

    /// <summary>
    /// Send a 500 Internal Server Error response
    /// </summary>
    public static void Send500(HttpListenerResponse response, string message = "Internal Server Error")
    {
        response.StatusCode = 500;
        var html = BuildErrorPage("500 Internal Server Error", message);
        var buffer = Encoding.UTF8.GetBytes(html);
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = buffer.Length;
        response.OutputStream.Write(buffer, 0, buffer.Length);
        response.OutputStream.Close();
    }

    /// <summary>
    /// Build an HTML error page
    /// </summary>
    private static string BuildErrorPage(string title, string message)
    {
        return $@"<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>{title}</title>
    <script src=""https://cdn.tailwindcss.com""></script>
</head>
<body class=""bg-gray-50 text-gray-900"">
    <main class=""max-w-4xl mx-auto p-8 mt-16"">
        <h1 class=""text-4xl font-bold text-red-600 mb-4"">{title}</h1>
        <p class=""text-lg text-gray-700"">{WebUtility.HtmlEncode(message)}</p>
        <p class=""mt-4"">
            <a href=""/"" class=""text-blue-600 hover:underline"">Go to home</a>
        </p>
    </main>
</body>
</html>";
    }
}
