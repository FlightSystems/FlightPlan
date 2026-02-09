using System.Text;
using System.Text.RegularExpressions;
using FlightPlan.Models;

namespace FlightPlan.Services;

/// <summary>
/// Chunks markdown documentation into semantic sections
/// </summary>
public class MarkdownChunker
{
    public List<FlightPlanChunk> ChunkMarkdownFiles(string docsDirectory)
    {
        var chunks = new List<FlightPlanChunk>();
        
        if (!Directory.Exists(docsDirectory))
        {
            Console.WriteLine($"⚠️  Docs directory not found: {docsDirectory}");
            return chunks;
        }

        // Only index top-level markdown files (not subdirectories like service-catalog/)
        var markdownFiles = Directory.GetFiles(docsDirectory, "*.md", SearchOption.TopDirectoryOnly);
        Console.WriteLine($"   Found {markdownFiles.Length} markdown files");

        foreach (var file in markdownFiles)
        {
            try
            {
                var fileChunks = ChunkMarkdownFile(file);
                chunks.AddRange(fileChunks);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"⚠️  Failed to chunk {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        return chunks;
    }

    private List<FlightPlanChunk> ChunkMarkdownFile(string filePath)
    {
        var chunks = new List<FlightPlanChunk>();
        var content = File.ReadAllText(filePath);
        var fileName = Path.GetFileName(filePath);
        var relativePath = GetRelativePath(filePath);

        // Split by headings (## or ###)
        var sections = SplitIntoSections(content);

        foreach (var section in sections)
        {
            if (string.IsNullOrWhiteSpace(section.Content))
                continue;

            var chunkId = $"doc:{fileName}:{NormalizeHeading(section.Heading)}";
            
            chunks.Add(new FlightPlanChunk
            {
                Id = chunkId,
                Type = "documentation",
                Content = BuildDocumentationChunk(section.Heading, section.Content),
                SourceFile = relativePath,
                SourceSection = section.Heading,
                Metadata = new Dictionary<string, string>
                {
                    ["type"] = "documentation",
                    ["file"] = fileName,
                    ["section"] = section.Heading,
                    ["source"] = relativePath
                }
            });
        }

        return chunks;
    }

    private string GetRelativePath(string fullPath)
    {
        // Try to make path relative to docs/flightplan/ for cleaner citations
        var docsIndex = fullPath.IndexOf("docs/flightplan/", StringComparison.OrdinalIgnoreCase);
        if (docsIndex >= 0)
        {
            return fullPath.Substring(docsIndex);
        }
        return Path.GetFileName(fullPath);
    }

    private List<MarkdownSection> SplitIntoSections(string content)
    {
        var sections = new List<MarkdownSection>();
        var lines = content.Split('\n');
        
        var currentHeading = "Introduction";
        var currentContent = new StringBuilder();

        foreach (var line in lines)
        {
            // Check for heading
            var headingMatch = Regex.Match(line, @"^(#{1,3})\s+(.+)$");
            
            if (headingMatch.Success)
            {
                // Save previous section
                if (currentContent.Length > 0)
                {
                    sections.Add(new MarkdownSection
                    {
                        Heading = currentHeading,
                        Content = currentContent.ToString().Trim()
                    });
                    currentContent.Clear();
                }
                
                // Start new section
                currentHeading = headingMatch.Groups[2].Value.Trim();
            }
            else
            {
                currentContent.AppendLine(line);
            }
        }

        // Add final section
        if (currentContent.Length > 0)
        {
            sections.Add(new MarkdownSection
            {
                Heading = currentHeading,
                Content = currentContent.ToString().Trim()
            });
        }

        return sections;
    }

    private string BuildDocumentationChunk(string heading, string content)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {heading}");
        sb.AppendLine();
        
        // Truncate very long sections to avoid overwhelming the embedding model
        const int maxLength = 6000; // ~1500 tokens - reduced to be safer
        if (content.Length > maxLength)
        {
            sb.AppendLine(content.Substring(0, maxLength));
            sb.AppendLine();
            sb.AppendLine($"[Content truncated - original length: {content.Length} characters]");
        }
        else
        {
            sb.AppendLine(content);
        }
        
        return sb.ToString();
    }

    private string NormalizeHeading(string heading)
    {
        // Convert heading to slug-like format for chunk ID
        return heading
            .ToLower()
            .Replace(" ", "-")
            .Replace(":", "")
            .Replace(",", "")
            .Replace("?", "")
            .Replace("!", "")
            .Replace("(", "")
            .Replace(")", "");
    }

    private class MarkdownSection
    {
        public string Heading { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
    }
}
