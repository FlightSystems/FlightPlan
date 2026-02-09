using System.Text.RegularExpressions;

namespace FlightPlan.Reporting;

/// <summary>
/// Applies glossary/term hint decoration across a report document.
///
/// Goal: allow teams to maintain term definitions in YAML (terms:) without needing
/// to hard-code tooltip logic in every report generator.
///
/// Current behavior:
/// - KeyValueTable: if row.Key matches the last segment of a term key, and row.Value matches
///   a term value, the value is replaced with a renderer-friendly token.
/// - TableBlock: if a header matches a term key segment, the corresponding column cells are decorated.
///
/// Token format:
///   [[term|label|description]]
/// Renderers convert that token into HTML tooltips or Markdown inline text.
/// </summary>
public static class ReportTermHinting
{
    public static void AttachAndApply(ReportDocument document, Dictionary<string, object>? terms)
    {
        if (document is null) throw new ArgumentNullException(nameof(document));

        document.Terms = terms;
        Apply(document);
    }

    public static void Apply(ReportDocument document)
    {
        if (document is null) throw new ArgumentNullException(nameof(document));
        if (document.Terms is null || document.Terms.Count == 0) return;

        var index = TermIndex.TryCreate(document.Terms);
        if (index is null) return;

        foreach (var section in document.Sections)
        {
            ApplyToSection(section, index);
        }
    }

    private static void ApplyToSection(ReportSection section, TermIndex index)
    {
        foreach (var block in section.Blocks)
        {
            switch (block)
            {
                case KeyValueTableBlock kv:
                    ApplyToKeyValueTable(kv, index);
                    break;

                case TableBlock table:
                    ApplyToTable(table, index);
                    break;
            }
        }

        foreach (var child in section.Children)
        {
            ApplyToSection(child, index);
        }
    }

    private static void ApplyToKeyValueTable(KeyValueTableBlock table, TermIndex index)
    {
        for (var i = 0; i < table.Rows.Count; i++)
        {
            var row = table.Rows[i];
            if (string.IsNullOrWhiteSpace(row.Key) || string.IsNullOrWhiteSpace(row.Value))
                continue;

            var decorated = index.DecorateIfMatch(row.Key, row.Value);
            if (string.Equals(decorated, row.Value, StringComparison.Ordinal))
                continue;

            table.Rows[i] = new KeyValueRow { Key = row.Key, Value = decorated };
        }
    }

    private static void ApplyToTable(TableBlock table, TermIndex index)
    {
        if (table.Headers.Count == 0 || table.Rows.Count == 0) return;

        // Determine which columns are term-decoratable based on header text.
        var decorateColumns = new bool[table.Headers.Count];
        for (var i = 0; i < table.Headers.Count; i++)
        {
            decorateColumns[i] = index.IsMatchKey(table.Headers[i]);
        }

        if (!decorateColumns.Any(x => x)) return;

        foreach (var row in table.Rows)
        {
            for (var col = 0; col < decorateColumns.Length && col < row.Cells.Count; col++)
            {
                if (!decorateColumns[col]) continue;

                var cell = row.Cells[col];
                if (string.IsNullOrWhiteSpace(cell)) continue;

                row.Cells[col] = index.DecorateIfMatch(table.Headers[col], cell!);
            }
        }
    }

    private sealed class TermIndex
    {
        private readonly Dictionary<string, Dictionary<string, string>> _fieldToValues;
        private readonly Dictionary<string, Regex> _fieldToRegex;

        private TermIndex(
            Dictionary<string, Dictionary<string, string>> fieldToValues,
            Dictionary<string, Regex> fieldToRegex)
        {
            _fieldToValues = fieldToValues;
            _fieldToRegex = fieldToRegex;
        }

        public static TermIndex? TryCreate(Dictionary<string, object> terms)
        {
            var fieldToValues = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var (termKey, termValuesObj) in terms)
            {
                if (string.IsNullOrWhiteSpace(termKey)) continue;

                if (termValuesObj is not Dictionary<string, object> termValues)
                    continue;

                var normalizedField = NormalizeKey(GetFieldName(termKey));
                if (string.IsNullOrWhiteSpace(normalizedField))
                    continue;

                if (!fieldToValues.TryGetValue(normalizedField, out var values))
                {
                    values = new(StringComparer.OrdinalIgnoreCase);
                    fieldToValues[normalizedField] = values;
                }

                foreach (var (valueKey, defObj) in termValues)
                {
                    var value = (valueKey ?? string.Empty).Trim();
                    if (string.IsNullOrWhiteSpace(value)) continue;

                    var description = ExtractDescription(defObj);
                    if (string.IsNullOrWhiteSpace(description))
                        continue;

                    values[value] = description;
                }
            }

            if (fieldToValues.Count == 0) return null;

            // Precompile matchers per field so we can decorate terms inside longer free-text values.
            // Boundary rule: term must not be preceded/followed by an alphanumeric character.
            // This avoids matching "structured" inside "unstructured".
            var fieldToRegex = new Dictionary<string, Regex>(StringComparer.OrdinalIgnoreCase);
            foreach (var (field, values) in fieldToValues)
            {
                var termsForField = values.Keys
                    .Where(t => !string.IsNullOrWhiteSpace(t))
                    .OrderByDescending(t => t.Length)
                    .Select(Regex.Escape)
                    .ToArray();

                if (termsForField.Length == 0) continue;

                var pattern = $@"(?<![A-Za-z0-9])(?:{string.Join("|", termsForField)})(?![A-Za-z0-9])";
                fieldToRegex[field] = new Regex(
                    pattern,
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
            }

            return new TermIndex(fieldToValues, fieldToRegex);
        }

        public bool IsMatchKey(string key)
        {
            var normalized = NormalizeKey(key);
            return !string.IsNullOrWhiteSpace(normalized) && _fieldToValues.ContainsKey(normalized);
        }

        public string DecorateIfMatch(string key, string value)
        {
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(value))
                return value;

            // Idempotence: don't re-wrap.
            if (value.Contains("[[term|", StringComparison.Ordinal))
                return value;

            // Avoid interfering with markdown links.
            if (value.Contains("](", StringComparison.Ordinal))
                return value;

            var normalizedKey = NormalizeKey(key);
            if (string.IsNullOrWhiteSpace(normalizedKey))
                return value;

            if (!_fieldToValues.TryGetValue(normalizedKey, out var values))
                return value;

            var candidate = Unquote(value.Trim());
            if (values.TryGetValue(candidate, out var exactDescription))
                return BuildToken(label: candidate, description: exactDescription);

            // Free-text match: decorate term values that appear inside a longer string.
            if (!_fieldToRegex.TryGetValue(normalizedKey, out var regex))
                return value;

            if (!regex.IsMatch(value))
                return value;

            return regex.Replace(value, m =>
            {
                // Prefer matched text as label (preserve casing), but look up description case-insensitively.
                return values.TryGetValue(m.Value, out var desc)
                    ? BuildToken(label: m.Value, description: desc)
                    : m.Value;
            });
        }

        private static string BuildToken(string label, string description)
        {
            var safeLabel = (label ?? string.Empty).Replace('|', '/').Replace("]]", "] ]");
            var safeDescription = (description ?? string.Empty).Replace('|', '/').Replace("]]", "] ]");
            return $"[[term|{safeLabel}|{safeDescription}]]";
        }

        private static string GetFieldName(string termKey)
        {
            // Prefer the last segment after '.' so term keys can be scoped (e.g., deployment.rolloutStrategy).
            var parts = termKey.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return parts.Length == 0 ? termKey : parts[^1];
        }

        private static string NormalizeKey(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return string.Empty;

            Span<char> buffer = stackalloc char[text.Length];
            var len = 0;
            foreach (var ch in text)
            {
                if (char.IsLetterOrDigit(ch))
                {
                    buffer[len++] = char.ToLowerInvariant(ch);
                }
            }

            return new string(buffer[..len]);
        }

        private static string Unquote(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return value;

            var trimmed = value.Trim();
            if (trimmed.Length >= 2)
            {
                if ((trimmed.StartsWith('"') && trimmed.EndsWith('"')) ||
                    (trimmed.StartsWith('\'') && trimmed.EndsWith('\'')))
                {
                    return trimmed[1..^1].Trim();
                }
            }

            return trimmed;
        }

        private static string? ExtractDescription(object? defObj)
        {
            if (defObj is null) return null;

            // Allow shorthand: value: "some description"
            if (defObj is string s)
                return string.IsNullOrWhiteSpace(s) ? null : s;

            // Or expanded map: { description: "...", ... }
            if (defObj is Dictionary<string, object> map)
            {
                if (map.TryGetValue("description", out var descObj) && descObj is not null)
                {
                    var desc = descObj.ToString();
                    return string.IsNullOrWhiteSpace(desc) ? null : desc;
                }
            }

            return null;
        }
    }
}
