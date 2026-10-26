using System.Text;

namespace DLSSGManager.Core.Configuration;

public sealed class IniDocument
{
    private readonly List<Line> _lines = [];

    public static IniDocument Parse(string? text)
    {
        var doc = new IniDocument();
        var current = "";
        foreach (var raw in (text ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var trimmed = raw.Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']') && trimmed.Length > 2)
            {
                current = trimmed[1..^1].Trim();
                doc._lines.Add(new Line(LineKind.Section, current, "", raw));
                continue;
            }
            if (trimmed.Length == 0)
            {
                doc._lines.Add(new Line(LineKind.Blank, current, "", raw));
                continue;
            }
            if (trimmed.StartsWith(';') || trimmed.StartsWith('#'))
            {
                doc._lines.Add(new Line(LineKind.Comment, current, "", raw));
                continue;
            }
            var eq = raw.IndexOf('=');
            if (eq <= 0)
            {
                doc._lines.Add(new Line(LineKind.Comment, current, "", raw));
                continue;
            }
            var key = raw[..eq].Trim();
            var value = raw[(eq + 1)..];
            var comment = "";
            var semi = IndexOfUnquoted(value, ';');
            if (semi >= 0)
            {
                comment = value[semi..];
                value = value[..semi];
            }
            doc._lines.Add(new Line(LineKind.Entry, current, key, value.Trim(), comment));
        }
        return doc;
    }

    public string? Get(string section, string key)
    {
        foreach (var line in _lines)
        {
            if (line.Kind == LineKind.Entry &&
                line.Section.Equals(section, StringComparison.OrdinalIgnoreCase) &&
                line.Key.Equals(key, StringComparison.OrdinalIgnoreCase))
                return line.Value;
        }
        return null;
    }

    public void Set(string section, string key, string value)
    {
        var existing = _lines.FirstOrDefault(line =>
            line.Kind == LineKind.Entry &&
            line.Section.Equals(section, StringComparison.OrdinalIgnoreCase) &&
            line.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.Value = value;
            return;
        }

        var sectionIndex = _lines.FindIndex(line =>
            line.Kind == LineKind.Section && line.Section.Equals(section, StringComparison.OrdinalIgnoreCase));
        if (sectionIndex < 0)
        {
            if (_lines.Count > 0 && _lines[^1].Kind != LineKind.Blank)
                _lines.Add(new Line(LineKind.Blank, "", "", ""));
            _lines.Add(new Line(LineKind.Section, section, "", $"[{section}]"));
            _lines.Add(new Line(LineKind.Entry, section, key, value));
            return;
        }

        var insert = sectionIndex + 1;
        while (insert < _lines.Count && _lines[insert].Kind != LineKind.Section)
            insert++;
        _lines.Insert(insert, new Line(LineKind.Entry, section, key, value));
    }

    public bool Remove(string section, string key)
    {
        var index = _lines.FindIndex(line =>
            line.Kind == LineKind.Entry &&
            line.Section.Equals(section, StringComparison.OrdinalIgnoreCase) &&
            line.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
            return false;
        _lines.RemoveAt(index);
        return true;
    }

    public void ReplaceWith(IniDocument other)
    {
        _lines.Clear();
        _lines.AddRange(other._lines.Select(line => line.Clone()));
    }

    public override string ToString()
    {
        var builder = new StringBuilder();
        foreach (var line in _lines)
        {
            builder.Append(line.Kind switch
            {
                LineKind.Entry => $"{line.Key}={line.Value}{(string.IsNullOrEmpty(line.Comment) ? "" : " " + line.Comment.TrimStart())}",
                _ => line.Raw
            });
            builder.Append("\r\n");
        }
        return builder.ToString();
    }

    private static int IndexOfUnquoted(string value, char ch)
    {
        var quote = false;
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] == '"')
                quote = !quote;
            else if (value[i] == ch && !quote)
                return i;
        }
        return -1;
    }

    private enum LineKind { Blank, Comment, Section, Entry }

    private sealed class Line
    {
        public Line(LineKind kind, string section, string key, string rawOrValue, string comment = "")
        {
            Kind = kind;
            Section = section;
            Key = key;
            if (kind == LineKind.Entry)
                Value = rawOrValue;
            else
                Raw = rawOrValue;
            Comment = comment;
        }

        public LineKind Kind { get; }
        public string Section { get; }
        public string Key { get; }
        public string Value { get; set; } = "";
        public string Raw { get; } = "";
        public string Comment { get; }

        public Line Clone() => new(Kind, Section, Key, Kind == LineKind.Entry ? Value : Raw, Comment);
    }
}
