using FlightPlan.Models;

namespace FlightPlan.Reporting;

/// <summary>
/// Root report document produced by report generators.
/// This is renderer-agnostic and can be translated to Markdown, HTML, PDF, etc.
/// </summary>
public sealed class ReportDocument
{
    public string Title { get; init; } = string.Empty;
    public string? Subtitle { get; init; }
    public DateTime GeneratedAtUtc { get; init; } = DateTime.UtcNow;

    /// <summary>
    /// Optional shared terms dictionary (glossary) to support UI hints/tooltips in renderers.
    /// Typically sourced from CompiledFlightPlan.Terms.
    /// </summary>
    public Dictionary<string, object>? Terms { get; set; }

    public ReportMetadata Metadata { get; init; } = new();
    public List<Finding> Findings { get; init; } = [];
    public List<ReportSection> Sections { get; init; } = [];
}

// ------------------------------------------------------------
// Metadata
// ------------------------------------------------------------

public sealed class ReportMetadata
{
    public string? ApplicationName { get; init; }
    public string? Environment { get; init; }
    public string? Version { get; init; }

    public Dictionary<string, string> Tags { get; init; } = [];
}

// ------------------------------------------------------------
// Sections
// ------------------------------------------------------------

public sealed class ReportSection
{
    public string Heading { get; init; } = string.Empty;
    public int Level { get; init; } = 1; // Markdown-style: 1 = H1, 2 = H2, etc.
    public string? Anchor { get; init; }

    public List<IReportBlock> Blocks { get; init; } = [];
    public List<ReportSection> Children { get; init; } = [];
}

// ------------------------------------------------------------
// Blocks (content primitives)
// ------------------------------------------------------------

public interface IReportBlock
{
    ReportBlockKind Kind { get; }
}

public enum ReportBlockKind
{
    Paragraph,
    BulletList,
    Table,
    KeyValueTable,
    CodeBlock,
    Callout,
    Divider
}

// ------------------------------------------------------------
// Text / Paragraph
// ------------------------------------------------------------

public sealed class ParagraphBlock : IReportBlock
{
    public ReportBlockKind Kind => ReportBlockKind.Paragraph;

    public string Text { get; init; } = string.Empty;
    public TextEmphasis Emphasis { get; init; } = TextEmphasis.None;
}

public enum TextEmphasis
{
    None,
    Muted,
    Strong,
    Italic
}

// ------------------------------------------------------------
// Lists
// ------------------------------------------------------------

public sealed class BulletListBlock : IReportBlock
{
    public ReportBlockKind Kind => ReportBlockKind.BulletList;

    public List<ListItem> Items { get; init; } = [];
}

public sealed class ListItem
{
    public string Text { get; init; } = string.Empty;
    public List<ListItem> Children { get; init; } = [];
}

// ------------------------------------------------------------
// Tables
// ------------------------------------------------------------

public sealed class TableBlock : IReportBlock
{
    public ReportBlockKind Kind => ReportBlockKind.Table;

    public List<string> Headers { get; init; } = [];
    public List<TableRow> Rows { get; init; } = [];
    public TableAlignment Alignment { get; init; } = TableAlignment.Auto;
}

public sealed class TableRow
{
    public List<string?> Cells { get; init; } = [];
}

public enum TableAlignment
{
    Auto,
    Left,
    Center,
    Right
}

/// <summary>
/// Specialized table for "label/value" style data
/// </summary>
public sealed class KeyValueTableBlock : IReportBlock
{
    public ReportBlockKind Kind => ReportBlockKind.KeyValueTable;

    public List<KeyValueRow> Rows { get; init; } = [];
}

public sealed class KeyValueRow
{
    public string Key { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
}

// ------------------------------------------------------------
// Code blocks
// ------------------------------------------------------------

public sealed class CodeBlock : IReportBlock
{
    public ReportBlockKind Kind => ReportBlockKind.CodeBlock;

    public string Code { get; init; } = string.Empty;
    public string? Language { get; init; } // yaml, json, bash, etc.
}

// ------------------------------------------------------------
// Callouts / Notices
// ------------------------------------------------------------

public sealed class CalloutBlock : IReportBlock
{
    public ReportBlockKind Kind => ReportBlockKind.Callout;

    public CalloutKind CalloutType { get; init; } = CalloutKind.Info;
    public string Title { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

public enum CalloutKind
{
    Info,
    Warning,
    Error,
    Success
}

// ------------------------------------------------------------
// Divider
// ------------------------------------------------------------

public sealed class DividerBlock : IReportBlock
{
    public ReportBlockKind Kind => ReportBlockKind.Divider;
}
