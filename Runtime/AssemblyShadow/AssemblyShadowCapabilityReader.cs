using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace HybridCLR
{
    // Capability negotiation runs before UnityEngine is ready. Keep this parser
    // independent of JsonUtility and validate the complete JSON document so an
    // unknown field cannot hide malformed or duplicated native evidence.
    internal static class AssemblyShadowCapabilityReader
    {
        internal struct Snapshot
        {
            internal int schemaVersion;
            internal bool enabled;
            internal int metadataBudgetCapabilityVersion;
            internal int recoveryCapabilityVersion;
            internal bool hasSchemaVersion;
            internal bool hasEnabled;
            internal bool hasMetadataBudgetCapabilityVersion;
            internal bool hasRecoveryCapabilityVersion;
        }

        internal static bool TryRead(string json, out Snapshot snapshot)
        {
            snapshot = new Snapshot();
            if (string.IsNullOrEmpty(json) || json.Length > Parser.MaxJsonCharacters)
                return false;

            try
            {
                new Parser(json).ReadDocument(ref snapshot);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private sealed class Parser
        {
            internal const int MaxJsonCharacters = 4 * 1024 * 1024;
            private const int MaxDepth = 64;
            private const int MaxContainerItems = 16384;
            private const int MaxNodes = 100000;
            private const int MaxStringCharacters = 1024 * 1024;
            private const int MaxNumberCharacters = 1024;

            private readonly string json;
            private int position;
            private int nodes;

            internal Parser(string json) { this.json = json; }

            internal void ReadDocument(ref Snapshot snapshot)
            {
                WhiteSpace();
                if (position >= json.Length || json[position] != '{')
                    throw Invalid("Capability diagnostics must be a JSON object.");
                ReadRootObject(ref snapshot, 0);
                WhiteSpace();
                if (position != json.Length)
                    throw Invalid("Trailing JSON content.");
            }

            private void ReadRootObject(ref Snapshot snapshot, int depth)
            {
                BeginContainer(depth);
                Expect('{');
                var fields = new HashSet<string>(StringComparer.Ordinal);
                if (Take('}')) return;

                int itemCount = 0;
                while (true)
                {
                    if (++itemCount > MaxContainerItems) throw Invalid("Too many JSON object fields.");
                    string field = ReadString();
                    if (!fields.Add(field)) throw Invalid("Duplicate JSON field: " + field);
                    Expect(':');
                    switch (field)
                    {
                        case "schemaVersion":
                            snapshot.schemaVersion = ReadInt32();
                            snapshot.hasSchemaVersion = true;
                            break;
                        case "enabled":
                            snapshot.enabled = ReadBoolean();
                            snapshot.hasEnabled = true;
                            break;
                        case "metadataBudgetCapabilityVersion":
                            snapshot.metadataBudgetCapabilityVersion = ReadInt32();
                            snapshot.hasMetadataBudgetCapabilityVersion = true;
                            break;
                        case "recoveryCapabilityVersion":
                            snapshot.recoveryCapabilityVersion = ReadInt32();
                            snapshot.hasRecoveryCapabilityVersion = true;
                            break;
                        default:
                            SkipValue(depth + 1);
                            break;
                    }

                    if (Take('}')) return;
                    Expect(',');
                }
            }

            private void SkipValue(int depth)
            {
                BeginNode();
                WhiteSpace();
                if (position >= json.Length) throw Invalid("Missing JSON value.");
                switch (json[position])
                {
                    case '{': ReadObject(depth); return;
                    case '[': ReadArray(depth); return;
                    case '"': ReadString(); return;
                    case 't': ExpectWord("true"); return;
                    case 'f': ExpectWord("false"); return;
                    case 'n': ExpectWord("null"); return;
                    default:
                        if (json[position] == '-' || IsDigit(json[position]))
                        {
                            ReadNumber();
                            return;
                        }
                        throw Invalid("Invalid JSON value.");
                }
            }

            private void ReadObject(int depth)
            {
                BeginContainer(depth);
                Expect('{');
                var fields = new HashSet<string>(StringComparer.Ordinal);
                if (Take('}')) return;
                int itemCount = 0;
                while (true)
                {
                    if (++itemCount > MaxContainerItems) throw Invalid("Too many JSON object fields.");
                    string field = ReadString();
                    if (!fields.Add(field)) throw Invalid("Duplicate JSON field: " + field);
                    Expect(':');
                    SkipValue(depth + 1);
                    if (Take('}')) return;
                    Expect(',');
                }
            }

            private void ReadArray(int depth)
            {
                BeginContainer(depth);
                Expect('[');
                if (Take(']')) return;
                int itemCount = 0;
                while (true)
                {
                    if (++itemCount > MaxContainerItems) throw Invalid("Too many JSON array items.");
                    SkipValue(depth + 1);
                    if (Take(']')) return;
                    Expect(',');
                }
            }

            private int ReadInt32()
            {
                string token = ReadNumber();
                if (token.IndexOf('.') >= 0 || token.IndexOf('e') >= 0 || token.IndexOf('E') >= 0)
                    throw Invalid("Capability version must be an integer.");
                int value;
                if (!int.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value))
                    throw Invalid("Capability version is outside Int32 range.");
                return value;
            }

            private bool ReadBoolean()
            {
                WhiteSpace();
                if (TakeWord("true")) return true;
                if (TakeWord("false")) return false;
                throw Invalid("Expected a Boolean token.");
            }

            private string ReadNumber()
            {
                WhiteSpace();
                int start = position;
                if (TakeRaw('-')) { }

                if (position >= json.Length) throw Invalid("Incomplete JSON number.");
                if (TakeRaw('0'))
                {
                    if (position < json.Length && IsDigit(json[position]))
                        throw Invalid("Leading zero in JSON number.");
                }
                else
                {
                    if (position >= json.Length || json[position] < '1' || json[position] > '9')
                        throw Invalid("Invalid JSON number.");
                    while (position < json.Length && IsDigit(json[position])) position++;
                }

                if (TakeRaw('.'))
                {
                    int fractionStart = position;
                    while (position < json.Length && IsDigit(json[position])) position++;
                    if (position == fractionStart) throw Invalid("JSON number requires fraction digits.");
                }

                if (position < json.Length && (json[position] == 'e' || json[position] == 'E'))
                {
                    position++;
                    if (position < json.Length && (json[position] == '+' || json[position] == '-')) position++;
                    int exponentStart = position;
                    while (position < json.Length && IsDigit(json[position])) position++;
                    if (position == exponentStart) throw Invalid("JSON number requires exponent digits.");
                }

                if (position - start > MaxNumberCharacters)
                    throw Invalid("JSON number is too large.");
                return json.Substring(start, position - start);
            }

            private string ReadString()
            {
                WhiteSpace();
                if (!Take('"')) throw Invalid("Expected a JSON string.");
                var value = new StringBuilder();
                while (position < json.Length)
                {
                    char token = json[position++];
                    if (token == '"') return value.ToString();
                    if (token < 0x20) throw Invalid("Invalid character in JSON string.");
                    if (char.IsHighSurrogate(token))
                    {
                        if (position >= json.Length || !char.IsLowSurrogate(json[position]))
                            throw Invalid("Unpaired high surrogate in JSON string.");
                        value.Append(token).Append(json[position++]);
                        if (value.Length > MaxStringCharacters) throw Invalid("JSON string is too large.");
                        continue;
                    }
                    if (char.IsLowSurrogate(token)) throw Invalid("Unpaired low surrogate in JSON string.");
                    if (token != '\\')
                    {
                        value.Append(token);
                    }
                    else
                    {
                        if (position >= json.Length) throw Invalid("Unterminated JSON escape.");
                        char escaped = json[position++];
                        switch (escaped)
                        {
                            case '"': value.Append('"'); break;
                            case '\\': value.Append('\\'); break;
                            case '/': value.Append('/'); break;
                            case 'b': value.Append('\b'); break;
                            case 'f': value.Append('\f'); break;
                            case 'n': value.Append('\n'); break;
                            case 'r': value.Append('\r'); break;
                            case 't': value.Append('\t'); break;
                            case 'u': AppendUnicodeEscape(value); break;
                            default: throw Invalid("Unknown JSON escape.");
                        }
                    }
                    if (value.Length > MaxStringCharacters) throw Invalid("JSON string is too large.");
                }
                throw Invalid("Unterminated JSON string.");
            }

            private void AppendUnicodeEscape(StringBuilder value)
            {
                char high = ReadHexChar();
                if (high >= 0xD800 && high <= 0xDBFF)
                {
                    if (!TakeRaw('\\') || !TakeRaw('u')) throw Invalid("Unpaired high surrogate in JSON string.");
                    char low = ReadHexChar();
                    if (low < 0xDC00 || low > 0xDFFF) throw Invalid("Invalid surrogate pair in JSON string.");
                    value.Append(high).Append(low);
                    return;
                }
                if (high >= 0xDC00 && high <= 0xDFFF)
                    throw Invalid("Unpaired low surrogate in JSON string.");
                value.Append(high);
            }

            private char ReadHexChar()
            {
                if (position + 4 > json.Length) throw Invalid("Incomplete Unicode escape.");
                int value = 0;
                for (int index = 0; index < 4; ++index)
                {
                    int digit = Hex(json[position++]);
                    if (digit < 0) throw Invalid("Invalid Unicode escape.");
                    value = (value << 4) | digit;
                }
                return (char)value;
            }

            private void BeginContainer(int depth)
            {
                if (depth > MaxDepth) throw Invalid("JSON nesting exceeds the bound.");
                BeginNode();
            }

            private void BeginNode()
            {
                if (++nodes > MaxNodes) throw Invalid("JSON resource bound exceeded.");
            }

            private void Expect(char token)
            {
                if (!Take(token)) throw Invalid("Expected '" + token + "'.");
            }

            private bool Take(char token)
            {
                WhiteSpace();
                return TakeRaw(token);
            }

            private bool TakeRaw(char token)
            {
                if (position < json.Length && json[position] == token)
                {
                    position++;
                    return true;
                }
                return false;
            }

            private bool TakeWord(string word)
            {
                if (position + word.Length > json.Length || string.CompareOrdinal(json, position, word, 0, word.Length) != 0)
                    return false;
                position += word.Length;
                return true;
            }

            private void ExpectWord(string word)
            {
                if (!TakeWord(word)) throw Invalid("Invalid JSON literal.");
            }

            private void WhiteSpace()
            {
                while (position < json.Length && (json[position] == ' ' || json[position] == '\t' || json[position] == '\r' || json[position] == '\n'))
                    position++;
            }

            private static bool IsDigit(char value) { return value >= '0' && value <= '9'; }

            private static int Hex(char value)
            {
                if (value >= '0' && value <= '9') return value - '0';
                if (value >= 'a' && value <= 'f') return value - 'a' + 10;
                if (value >= 'A' && value <= 'F') return value - 'A' + 10;
                return -1;
            }

            private static FormatException Invalid(string message) { return new FormatException(message); }
        }
    }
}
