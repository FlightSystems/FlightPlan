namespace FlightPlan.Reporting;

/// <summary>
/// Utilities for generating hyperlinks to service and resource catalog detail pages
/// </summary>
public static class CatalogLinkHelper
{
    /// <summary>
    /// Sanitize a string to be safe for use as a filename
    /// </summary>
    public static string SanitizeFileName(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        // Keep file names stable and cross-platform.
        var chars = text.Trim().ToCharArray();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            var ok =
                (c >= 'a' && c <= 'z') ||
                (c >= 'A' && c <= 'Z') ||
                (c >= '0' && c <= '9') ||
                c == '-' ||
                c == '_' ||
                c == '.';

            chars[i] = ok ? c : '-';
        }

        var s = new string(chars);
        while (s.Contains("--", StringComparison.Ordinal)) s = s.Replace("--", "-", StringComparison.Ordinal);
        return s.Trim('-');
    }

    /// <summary>
    /// Generate a markdown hyperlink to a service's detail page in the catalog
    /// </summary>
    /// <param name="service">The service entity</param>
    /// <param name="extension">File extension (e.g., "md" or "html")</param>
    /// <returns>Markdown link like [Service Name](service-catalog/service-name.md)</returns>
    public static string ServiceLink(ServiceEntity service, string extension = "md")
    {
        var key = string.IsNullOrWhiteSpace(service.Id) ? service.Name : service.Id;
        var fileName = SanitizeFileName(key);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "service";
        
        var linkText = service.Name;
        var href = $"service-catalog/{fileName}.{extension}";
        
        return $"[{linkText}]({href})";
    }

    /// <summary>
    /// Generate a markdown hyperlink to a resource's detail page in the catalog
    /// </summary>
    /// <param name="resource">The resource entity</param>
    /// <param name="extension">File extension (e.g., "md" or "html")</param>
    /// <returns>Markdown link like [Resource Name](resource-catalog/resource-name.md)</returns>
    public static string ResourceLink(ResourceEntity resource, string extension = "md")
    {
        var key = string.IsNullOrWhiteSpace(resource.Id) ? resource.Name : resource.Id;
        var fileName = SanitizeFileName(key);
        if (string.IsNullOrWhiteSpace(fileName)) fileName = "resource";
        
        var linkText = resource.Name;
        var href = $"resource-catalog/{fileName}.{extension}";
        
        return $"[{linkText}]({href})";
    }

    /// <summary>
    /// Generate a markdown hyperlink using a custom lookup dictionary
    /// </summary>
    /// <param name="service">The service entity</param>
    /// <param name="fileNameByServiceId">Dictionary mapping service IDs to filenames</param>
    /// <param name="extension">File extension</param>
    /// <returns>Markdown link</returns>
    public static string ServiceLink(ServiceEntity service, Dictionary<string, string> fileNameByServiceId, string extension = "md")
    {
        var lookup = string.IsNullOrWhiteSpace(service.Id) ? service.Name : service.Id;
        var fileName = fileNameByServiceId.TryGetValue(lookup, out var fn) ? fn : SanitizeFileName(lookup);
        
        var linkText = service.Name;
        var href = $"service-catalog/{fileName}.{extension}";
        
        return $"[{linkText}]({href})";
    }

    /// <summary>
    /// Generate a markdown hyperlink using a custom lookup dictionary
    /// </summary>
    /// <param name="resource">The resource entity</param>
    /// <param name="fileNameByResourceId">Dictionary mapping resource IDs to filenames</param>
    /// <param name="extension">File extension</param>
    /// <returns>Markdown link</returns>
    public static string ResourceLink(ResourceEntity resource, Dictionary<string, string> fileNameByResourceId, string extension = "md")
    {
        var lookup = string.IsNullOrWhiteSpace(resource.Id) ? resource.Name : resource.Id;
        var fileName = fileNameByResourceId.TryGetValue(lookup, out var fn) ? fn : SanitizeFileName(lookup);
        
        var linkText = resource.Name;
        var href = $"resource-catalog/{fileName}.{extension}";
        
        return $"[{linkText}]({href})";
    }
}
