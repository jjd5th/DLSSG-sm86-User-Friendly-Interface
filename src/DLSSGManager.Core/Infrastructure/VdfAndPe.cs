using System.Text;

namespace DLSSGManager.Core.Infrastructure;

public static class VdfReader
{
    public static Dictionary<string, object> Parse(string text)
    {
        var parser = new Parser(text);
        var root = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        while (parser.TryRead(out var key))
        {
            if (!parser.TryRead(out var next))
                break;
            if (next == "{")
                root[key] = parser.ReadObject();
            else
                root[key] = next;
        }
        return root;
    }

    private sealed class Parser
    {
        private readonly string _text;
        private int _index;

        public Parser(string text) => _text = text;

        public bool TryRead(out string token)
        {
            Skip();
            token = "";
            if (_index >= _text.Length)
                return false;
            if (_text[_index] == '{')
            {
                _index++;
                token = "{";
                return true;
            }
            if (_text[_index] == '}')
            {
                _index++;
                token = "}";
                return true;
            }
            if (_text[_index] == '"')
            {
                token = ReadQuoted();
                return true;
            }
            var start = _index;
            while (_index < _text.Length && !char.IsWhiteSpace(_text[_index]) && _text[_index] is not '{' and not '}')
                _index++;
            token = _text[start.._index];
            return token.Length > 0;
        }

        public Dictionary<string, object> ReadObject()
        {
            var map = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            while (TryRead(out var key))
            {
                if (key == "}")
                    break;
                if (!TryRead(out var next))
                    break;
                if (next == "{")
                    map[key] = ReadObject();
                else if (next != "}")
                    map[key] = next;
            }
            return map;
        }

        private string ReadQuoted()
        {
            _index++;
            var builder = new StringBuilder();
            while (_index < _text.Length)
            {
                var ch = _text[_index++];
                if (ch == '\\' && _index < _text.Length)
                {
                    builder.Append(_text[_index++]);
                    continue;
                }
                if (ch == '"')
                    break;
                builder.Append(ch);
            }
            return builder.ToString();
        }

        private void Skip()
        {
            while (_index < _text.Length)
            {
                if (char.IsWhiteSpace(_text[_index]))
                {
                    _index++;
                    continue;
                }
                if (_text[_index] == '/' && _index + 1 < _text.Length && _text[_index + 1] == '/')
                {
                    while (_index < _text.Length && _text[_index] != '\n')
                        _index++;
                    continue;
                }
                break;
            }
        }
    }
}

public static class PeImports
{
    public static IReadOnlyList<string> ImportDlls(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 64)
                return [];
            stream.Position = 0x3C;
            var peOffset = reader.ReadInt32();
            if (peOffset <= 0 || peOffset > stream.Length - 24)
                return [];
            stream.Position = peOffset;
            if (reader.ReadUInt32() != 0x4550)
                return [];
            stream.Position += 2;
            var sections = reader.ReadUInt16();
            stream.Position += 14;
            var optionalSize = reader.ReadUInt16();
            var optionalStart = stream.Position;
            var magic = reader.ReadUInt16();
            var is64 = magic == 0x20B;
            stream.Position = optionalStart + (is64 ? 112 : 96);
            if (stream.Position + 8 > stream.Length)
                return [];
            var importRva = reader.ReadUInt32();
            if (importRva == 0)
                return [];
            stream.Position = optionalStart + optionalSize;
            var sectionTable = new List<(uint Va, uint Raw, uint RawSize)>();
            for (var i = 0; i < sections; i++)
            {
                stream.Position += 8;
                var virtualSize = reader.ReadUInt32();
                var virtualAddress = reader.ReadUInt32();
                var rawSize = reader.ReadUInt32();
                var raw = reader.ReadUInt32();
                stream.Position += 16;
                sectionTable.Add((virtualAddress, raw, Math.Max(virtualSize, rawSize)));
            }

            long RvaToOffset(uint rva)
            {
                foreach (var section in sectionTable)
                {
                    if (rva >= section.Va && rva < section.Va + section.RawSize)
                        return section.Raw + (rva - section.Va);
                }
                return -1;
            }

            var importOffset = RvaToOffset(importRva);
            if (importOffset < 0)
                return [];
            var names = new List<string>();
            for (var i = 0; i < 64; i++)
            {
                stream.Position = importOffset + i * 20;
                if (stream.Position + 20 > stream.Length)
                    break;
                var original = reader.ReadUInt32();
                stream.Position += 8;
                var nameRva = reader.ReadUInt32();
                stream.Position += 4;
                if (original == 0 && nameRva == 0)
                    break;
                var nameOffset = RvaToOffset(nameRva);
                if (nameOffset < 0)
                    continue;
                stream.Position = nameOffset;
                var bytes = new List<byte>();
                for (var n = 0; n < 128; n++)
                {
                    var b = reader.ReadByte();
                    if (b == 0)
                        break;
                    bytes.Add(b);
                }
                if (bytes.Count > 0)
                    names.Add(Encoding.ASCII.GetString(bytes.ToArray()));
            }
            return names;
        }
        catch
        {
            return [];
        }
    }
}
