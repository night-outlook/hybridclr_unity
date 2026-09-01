using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace HybridCLR.AssemblyShadow.CodeGen
{
    // Deliberately small JSON grammar: objects, arrays, strings, Int32 and bool.
    // Presence and duplicate checks precede DTO construction; false/zero never
    // stand in for omitted fields. No serializer-specific unknown-field behavior.
    internal sealed class RawTypeAdmissionJson
    {
        private readonly string text;
        private int position;
        private RawTypeAdmissionJson(string text) { this.text = text; }
        internal static RawTypeAdmissionConfiguration Parse(byte[] bytes)
        {
            try
            {
                BindingChecks.Require(bytes != null && bytes.Length > 0 && bytes.Length <= 8 * 1024 * 1024, "InvalidRawAdmissionJson", "Expected bounded UTF-8 JSON.");
                var parser = new RawTypeAdmissionJson(new UTF8Encoding(false, true).GetString(bytes));
                var root = parser.Read(0) as Dictionary<string, object>; parser.Space(); parser.Check(parser.position == parser.text.Length);
                Exact(root, "schemaVersion", "policy", "sites");
                int schemaVersion = Get<int>(root, "schemaVersion");
                var entries = Get<List<object>>(root, "sites");
                var sites = new List<RawTypeAdmissionSite>();
                foreach (object entry in entries)
                {
                    var site = entry as Dictionary<string, object>;
                    if (schemaVersion == 1)
                        Exact(site, "id", "consumerAssembly", "declaringType", "methodSignature", "methodHash", "operationIndex", "operationSignature", "providerAssemblyIdentity", "typeName", "throwOnError", "ignoreCase", "reason");
                    else if (schemaVersion == 2)
                        Exact(site, "id", "consumerAssembly", "declaringType", "methodSignature", "compilerVariants", "operationSignature", "providerAssemblyIdentity", "typeName", "throwOnError", "ignoreCase", "reason");
                    else BindingChecks.Require(false, "InvalidRawAdmissionJson", "Unsupported raw admission schema.");
                    RawTypeAdmissionMethodVariant[] variants = null;
                    if (schemaVersion == 2)
                    {
                        variants = Get<List<object>>(site, "compilerVariants").Select(item =>
                        {
                            var variant = item as Dictionary<string, object>;
                            Exact(variant, "compilerMode", "methodHash", "operationIndex");
                            return new RawTypeAdmissionMethodVariant { compilerMode = Get<string>(variant, "compilerMode"),
                                methodHash = Get<string>(variant, "methodHash"), operationIndex = Get<int>(variant, "operationIndex") };
                        }).ToArray();
                    }
                    sites.Add(new RawTypeAdmissionSite { id = Get<string>(site, "id"), consumerAssembly = Get<string>(site, "consumerAssembly"),
                        declaringType = Get<string>(site, "declaringType"), methodSignature = Get<string>(site, "methodSignature"),
                        methodHash = schemaVersion == 1 ? Get<string>(site, "methodHash") : null,
                        operationIndex = schemaVersion == 1 ? (int?)Get<int>(site, "operationIndex") : null, compilerVariants = variants,
                        operationSignature = Get<string>(site, "operationSignature"),
                        providerAssemblyIdentity = Get<string>(site, "providerAssemblyIdentity"), typeName = Get<string>(site, "typeName"),
                        throwOnError = Get<bool>(site, "throwOnError"), ignoreCase = Get<bool>(site, "ignoreCase"), reason = Get<string>(site, "reason") });
                }
                return new RawTypeAdmissionConfiguration { schemaVersion = schemaVersion, policy = Get<string>(root, "policy"), sites = sites.ToArray() };
            }
            catch (ReflectionBindingException) { throw; }
            catch (Exception error) { throw new ReflectionBindingException("InvalidRawAdmissionJson", error.Message); }
        }
        private static void Exact(Dictionary<string, object> value, params string[] names)
        { BindingChecks.Require(value != null && value.Count == names.Length && names.All(value.ContainsKey), "InvalidRawAdmissionJson", "Missing or unknown JSON field."); }
        private static T Get<T>(Dictionary<string, object> value, string name)
        { BindingChecks.Require(value[name] is T, "InvalidRawAdmissionJson", "Wrong JSON type for " + name); return (T)value[name]; }
        private void Check(bool value) { BindingChecks.Require(value, "InvalidRawAdmissionJson", "Malformed JSON at offset " + position); }
        private void Space() { while (position < text.Length && " \r\n\t".IndexOf(text[position]) >= 0) ++position; }
        private bool Take(char c) { Space(); if (position < text.Length && text[position] == c) { ++position; return true; } return false; }
        private object Read(int depth)
        {
            Check(depth <= 6); Space(); Check(position < text.Length);
            if (text[position] == '"') return String();
            if (Take('{'))
            {
                var result = new Dictionary<string, object>(StringComparer.Ordinal);
                if (Take('}')) return result;
                do { string key = String(); Check(Take(':') && !result.ContainsKey(key)); result.Add(key, Read(depth + 1)); } while (Take(','));
                Check(Take('}')); return result;
            }
            if (Take('['))
            {
                var result = new List<object>(); if (Take(']')) return result;
                do { Check(result.Count < 4096); result.Add(Read(depth + 1)); } while (Take(','));
                Check(Take(']')); return result;
            }
            if (position + 4 <= text.Length && text.Substring(position, 4) == "true") { position += 4; return true; }
            if (position + 5 <= text.Length && text.Substring(position, 5) == "false") { position += 5; return false; }
            int start = position;
            if (text[position] == '-') ++position;
            Check(position < text.Length && text[position] >= '0' && text[position] <= '9');
            if (text[position] == '0') ++position;
            else while (position < text.Length && text[position] >= '0' && text[position] <= '9') ++position;
            int number; Check(int.TryParse(text.Substring(start, position - start), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out number)); return number;
        }
        private string String()
        {
            Check(Take('"')); var value = new StringBuilder(); bool ended = false;
            while (position < text.Length)
            {
                char c = text[position++]; if (c == '"') { ended = true; break; } Check(c >= 32);
                if (c == '\\')
                {
                    Check(position < text.Length); c = text[position++];
                    switch (c)
                    {
                        case '"': case '\\': case '/': break;
                        case 'b': c = '\b'; break; case 'f': c = '\f'; break; case 'n': c = '\n'; break;
                        case 'r': c = '\r'; break; case 't': c = '\t'; break;
                        case 'u':
                            Check(position + 4 <= text.Length); ushort code;
                            Check(ushort.TryParse(text.Substring(position, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out code));
                            position += 4; c = (char)code; break;
                        default: Check(false); break;
                    }
                }
                value.Append(c); Check(value.Length <= 16384);
            }
            Check(ended); string result = value.ToString();
            for (int index = 0; index < result.Length; index++)
                if (char.IsSurrogate(result[index])) { Check(char.IsHighSurrogate(result[index]) && index + 1 < result.Length && char.IsLowSurrogate(result[index + 1])); ++index; }
            return result;
        }
    }
}
