using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;

namespace HybridCLR.Editor.AssemblyShadow
{
    // Schema-2 presence/type checking precedes construction. JsonUtility's
    // false/zero defaults must never stand in for a missing warmup contract.
    internal sealed class ShadowWarmupJson
    {
        private readonly string text;
        private int position;
        private ShadowWarmupJson(string text) { this.text = text; }

        internal static Dictionary<string, object> Parse(byte[] bytes)
        {
            Check(bytes != null && bytes.Length > 0 && bytes.Length <= 16 * 1024 * 1024, "Expected bounded UTF-8 manifest bytes.");
            string text;
            try { text = new UTF8Encoding(false, true).GetString(bytes); }
            catch (DecoderFallbackException) { throw new ShadowBuildException("WarmupManifestSchema", "Invalid UTF-8 manifest."); }
            var parser = new ShadowWarmupJson(text);
            var result = parser.Read(0) as Dictionary<string, object>; parser.Space();
            Check(result != null && parser.position == text.Length, "Expected exactly one JSON object.");
            return result;
        }

        internal static T Convert<T>(Dictionary<string, object> value) where T : class
        { return (T)ConvertValue(value, typeof(T), 0); }

        private static object ConvertValue(object value, Type type, int depth)
        {
            Check(depth <= 32, "Oversized DTO graph.");
            if (type == typeof(string)) { Check(value == null || value is string, "Expected string."); return value; }
            if (type == typeof(int) || type == typeof(bool))
            { Check(value != null && value.GetType() == type, "Wrong primitive JSON type."); return value; }
            if (type == typeof(uint) || type == typeof(ulong))
            {
                Check(value is ulong || (value is int && (int)value >= 0), "Expected unsigned integer.");
                ulong number = value is ulong ? (ulong)value : (ulong)(int)value;
                if (type == typeof(ulong)) return number;
                Check(number <= uint.MaxValue, "UInt32 JSON integer overflow.");
                return (uint)number;
            }
            if (value == null && (type == typeof(MetadataEncodingProfile) || type == typeof(MetadataCapacityReport)))
                return null; // Absent R01 capability on a legacy schema-2 patch.
            if (type.IsArray)
            {
                var values = value as List<object>; Check(values != null && type.GetArrayRank() == 1, "Expected explicit array.");
                Type element = type.GetElementType(); Array array = Array.CreateInstance(element, values.Count);
                for (int index = 0; index < values.Count; ++index) array.SetValue(ConvertValue(values[index], element, depth + 1), index);
                return array;
            }
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
            var members = value as Dictionary<string, object>;
            Check(type.IsSerializable && type.Namespace == typeof(ShadowWarmupPlan).Namespace && members != null &&
                members.Keys.All(name => fields.Any(field => field.Name == name)) &&
                fields.All(field => members.ContainsKey(field.Name) || IsLegacyExtension(type, field.Name)),
                "Missing/unknown DTO field or null object.");
            if (type == typeof(ShadowPatchManifest))
            {
                string[] extension = { "nativeBudgetCapabilityVersion", "metadataEncodingProfile", "metadataCapacityReport" };
                int present = extension.Count(members.ContainsKey);
                Check(present == 0 || present == extension.Length, "Partial R01 metadata capability declaration.");
            }
            object instance = Activator.CreateInstance(type);
            foreach (FieldInfo field in fields)
                if (members.ContainsKey(field.Name)) field.SetValue(instance, ConvertValue(members[field.Name], field.FieldType, depth + 1));
            return instance;
        }

        private static bool IsLegacyExtension(Type type, string name)
        {
            return (type == typeof(ShadowPatchManifest) &&
                (name == "nativeBudgetCapabilityVersion" || name == "metadataEncodingProfile" || name == "metadataCapacityReport")) ||
                (type == typeof(ShadowPatchAssembly) && name == "dllSize");
        }

        private void Space() { while (position < text.Length && " \r\n\t".IndexOf(text[position]) >= 0) ++position; }
        private bool Take(char value) { Space(); if (position < text.Length && text[position] == value) { ++position; return true; } return false; }
        private bool Literal(string value)
        {
            if (position + value.Length > text.Length || string.CompareOrdinal(text, position, value, 0, value.Length) != 0) return false;
            position += value.Length; return true;
        }

        private object Read(int depth)
        {
            Check(depth <= 32, "Oversized JSON nesting."); Space(); Check(position < text.Length, "Truncated JSON.");
            if (text[position] == '"') return String();
            if (Take('{'))
            {
                var result = new Dictionary<string, object>(StringComparer.Ordinal); if (Take('}')) return result;
                do
                {
                    Check(result.Count < 4096, "Oversized JSON object."); string key = String();
                    Check(Take(':') && !result.ContainsKey(key), "Missing colon or duplicate JSON property.");
                    result.Add(key, Read(depth + 1));
                } while (Take(','));
                Check(Take('}'), "Unclosed JSON object."); return result;
            }
            if (Take('['))
            {
                var result = new List<object>(); if (Take(']')) return result;
                do { Check(result.Count < 16384, "Oversized JSON array."); result.Add(Read(depth + 1)); } while (Take(','));
                Check(Take(']'), "Unclosed JSON array."); return result;
            }
            if (Literal("true")) return true;
            if (Literal("false")) return false;
            if (Literal("null")) return null;
            int start = position; if (text[position] == '-') ++position;
            Check(position < text.Length && text[position] >= '0' && text[position] <= '9', "Expected Int32 JSON number.");
            if (text[position] == '0') ++position;
            else while (position < text.Length && text[position] >= '0' && text[position] <= '9') ++position;
            string token = text.Substring(start, position - start);
            int number;
            if (int.TryParse(token, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out number)) return number;
            ulong unsigned;
            Check(ulong.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out unsigned), "JSON integer overflow.");
            return unsigned;
        }

        private string String()
        {
            Check(Take('"'), "Expected JSON string."); var value = new StringBuilder(); bool ended = false;
            while (position < text.Length)
            {
                char current = text[position++]; if (current == '"') { ended = true; break; }
                Check(current >= 32, "Unescaped JSON control character.");
                if (current == '\\')
                {
                    Check(position < text.Length, "Truncated JSON escape."); current = text[position++];
                    switch (current)
                    {
                        case '"': case '\\': case '/': break;
                        case 'b': current = '\b'; break; case 'f': current = '\f'; break; case 'n': current = '\n'; break;
                        case 'r': current = '\r'; break; case 't': current = '\t'; break;
                        case 'u':
                            Check(position + 4 <= text.Length, "Truncated Unicode escape."); ushort code;
                            Check(ushort.TryParse(text.Substring(position, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out code), "Invalid Unicode escape.");
                            position += 4; current = (char)code; break;
                        default: Check(false, "Unknown JSON escape."); break;
                    }
                }
                value.Append(current); Check(value.Length <= 1024 * 1024, "Oversized JSON string.");
            }
            Check(ended, "Unclosed JSON string."); string result = value.ToString();
            for (int index = 0; index < result.Length; ++index)
                if (char.IsSurrogate(result[index]))
                { Check(char.IsHighSurrogate(result[index]) && index + 1 < result.Length && char.IsLowSurrogate(result[index + 1]), "Unpaired JSON surrogate."); ++index; }
            return result;
        }

        private static void Check(bool value, string message) { ShadowHash.Require(value, "WarmupManifestSchema", message); }
    }
}
