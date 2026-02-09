using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;

namespace FlightPlan.Services;

/// <summary>
/// Service for rendering Markdown to HTML with support for Mermaid diagrams
/// </summary>
public class MarkdownRenderingService
{
    private readonly MarkdownPipeline _pipeline;

    public MarkdownRenderingService()
    {
        // Configure pipeline for GitHub Flavored Markdown
        _pipeline = new MarkdownPipelineBuilder()
            .UseAdvancedExtensions()  // Includes pipe tables, task lists, definition lists, footnotes, etc.
            .UseAutoLinks()            // Auto-detect URLs and email addresses
            .UseEmojiAndSmiley()       // Support :emoji: syntax
            .UseSmartyPants()          // Smart quotes and typography
            .Build();
    }

    /// <summary>
    /// Render Markdown text to HTML
    /// </summary>
    public string RenderToHtml(string markdown)
    {
        var html = Markdown.ToHtml(markdown, _pipeline);
        
        // Post-process to convert Mermaid code blocks to proper format
        html = ConvertMermaidCodeBlocks(html);
        
        return html;
    }

    /// <summary>
    /// Convert Mermaid code blocks from Markdig format to Mermaid.js format
    /// Converts: <pre><code class="language-mermaid">...</code></pre>
    /// To: <pre class="mermaid">...</pre>
    /// </summary>
    private static string ConvertMermaidCodeBlocks(string html)
    {
        var mermaidPattern = @"<pre><code\s+class=""language-mermaid"">(.*?)</code></pre>";
        return Regex.Replace(html, mermaidPattern, 
            m => $"<pre class=\"mermaid\">{m.Groups[1].Value}</pre>", 
            RegexOptions.Singleline);
    }

    /// <summary>
    /// Extract the first H1 title from HTML content
    /// </summary>
    public static string? ExtractTitle(string htmlContent)
    {
        var h1Match = Regex.Match(htmlContent, @"<h1[^>]*>(.*?)</h1>", RegexOptions.IgnoreCase);
        if (h1Match.Success)
        {
            // Strip any HTML tags from the title
            return Regex.Replace(h1Match.Groups[1].Value, @"<[^>]+>", "");
        }
        return null;
    }

    /// <summary>
    /// Check if HTML content contains Mermaid diagrams
    /// </summary>
    public static bool ContainsMermaid(string htmlContent)
    {
        return htmlContent.Contains("class=\"mermaid\"", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Build a navigation breadcrumb from a file path
    /// </summary>
    public static string BuildBreadcrumb(string relativePath)
    {
        var parts = relativePath.Split(Path.DirectorySeparatorChar);
        var breadcrumbParts = new List<string>
        {
            @"<a href=""/"" class=""hover:text-blue-600"">Home</a>"
        };

        var currentPath = "";
        for (int i = 0; i < parts.Length; i++)
        {
            currentPath = string.IsNullOrEmpty(currentPath) 
                ? parts[i] 
                : currentPath + "/" + parts[i];
            
            var isLast = i == parts.Length - 1;
            var displayName = WebUtility.HtmlEncode(parts[i]);
            
            if (isLast)
            {
                breadcrumbParts.Add($@"<span class=""font-semibold"">{displayName}</span>");
            }
            else
            {
                var urlPath = currentPath.Replace(Path.DirectorySeparatorChar, '/');
                breadcrumbParts.Add($@"<a href=""/{urlPath}"" class=""hover:text-blue-600"">{displayName}</a>");
            }
        }

        return string.Join(@"<span class=""mx-2"">/</span>", breadcrumbParts);
    }

    /// <summary>
    /// Build complete HTML page with navigation, styling, and optional Mermaid support
    /// </summary>
    public string BuildHtmlPage(
        string htmlContent, 
        string title, 
        string relativePath,
        string mermaidScript = "",
        string stylesTemplate = "",
        string chatScript = "",
        bool includeChatInterface = false)
    {
        // Extract title from H1 if available
        var extractedTitle = ExtractTitle(htmlContent);
        if (!string.IsNullOrEmpty(extractedTitle))
        {
            title = extractedTitle;
        }

        // Check if Mermaid is needed
        var usesMermaid = ContainsMermaid(htmlContent);
        var mermaidScriptTag = usesMermaid ? mermaidScript : "";

        // Build breadcrumb
        var breadcrumb = BuildBreadcrumb(relativePath);

        // Chat interface
        var chatButton = includeChatInterface ? @"
    <div id=""chat-button""  title=""Ask questions about the architecture"">
        <svg xmlns=""http://www.w3.org/2000/svg"" width=""24"" height=""24"" viewBox=""0 0 24 24"" fill=""none"" stroke=""currentColor"" stroke-width=""2"" stroke-linecap=""round"" stroke-linejoin=""round"">
            <path d=""M21 15a2 2 0 0 1-2 2H7l-4 4V5a2 2 0 0 1 2-2h14a2 2 0 0 1 2 2z""></path>
        </svg>
    </div>" : "";

        var chatModal = includeChatInterface ? @"
    <div id=""chat-modal"">
        <div id=""chat-header"">
            <span>FlightPlan Assistant</span>
            <span id=""chat-close"">&times;</span>
        </div>
        <div id=""chat-messages"">
            <div class=""chat-message assistant markdown-body"">
                Hello! I can help you understand your system architecture. Ask me anything about services, resources, deployments, or dependencies.
            </div>
        </div>
        <div id=""chat-input-container"">
            <input type=""text"" id=""chat-input"" placeholder=""Ask about your architecture..."" />
            <button id=""chat-send"">Send</button>
        </div>
    </div>" : "";

        var chatScriptTag = includeChatInterface ? $"<script>{chatScript}</script>" : "";

        return $@"<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>{WebUtility.HtmlEncode(title)}</title>
    <script src=""https://cdn.tailwindcss.com""></script>
    {mermaidScriptTag}
    <style>
        {stylesTemplate}
    </style>
</head>
<body class=""bg-gray-50 text-gray-900"">
    <nav class=""bg-white border-b border-gray-200 px-8 py-3"">
        <div class=""max-w-6xl mx-auto"">
            <div class=""flex items-center justify-between"">
                <div class=""flex items-center gap-2 text-sm text-gray-600"">
                    {breadcrumb}
                </div>
            </div>
        </div>
    </nav>
    
    {chatButton}
    {chatModal}

    <main class=""max-w-6xl mx-auto p-8"">
        <div class=""markdown-body"">
            {htmlContent}
        </div>
    </main>
    <footer class=""bg-white border-t border-gray-200 px-8 py-4 mt-16"">
        <div class=""max-w-6xl mx-auto text-center text-sm text-gray-600"">
            <p>Generated by FlightPlan • Viewing: {WebUtility.HtmlEncode(relativePath)}</p>
        </div>
    </footer>
    
    {chatScriptTag}
</body>
</html>";
    }
}
