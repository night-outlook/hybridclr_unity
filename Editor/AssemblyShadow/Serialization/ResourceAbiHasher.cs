using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace HybridCLR.Editor.AssemblyShadow
{
    public static class ResourceAbiHasher
    {
        public const int SchemaVersion = 2;

        public static string Compute(ResourceAbiDescriptor descriptor)
        {
            if (descriptor == null) throw new ArgumentNullException("descriptor");
            var text = new StringBuilder();
            text.Append("resource-abi-schema:").Append(SchemaVersion).Append('\n');
            Append(text, "descriptor-schema", descriptor.schemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (var type in (descriptor.types ?? new ResourceAbiTypeDescriptor[0]).Where(t => t != null).OrderBy(t => t.typeKey ?? string.Empty, StringComparer.Ordinal))
            {
                Append(text, "type", type.typeKey);
                Append(text, "assembly", type.assembly);
                Append(text, "namespace", type.@namespace);
                Append(text, "name", type.type);
                Append(text, "base", type.baseChain);
                AppendMany(text, "interface", type.interfaces);
                Append(text, "callback", type.serializationCallback ? "1" : "0");
                Append(text, "callback-semantics", type.callbackSemanticHash);
                AppendMany(text, "serialize-reference-candidate", type.serializeReferenceCandidates);
                AppendMany(text, "referenced-type", type.referencedTypeKeys);
                AppendMany(text, "type-unknown-reason", type.unknownReasons);
                foreach (var field in (type.fields ?? new ResourceAbiFieldDescriptor[0]).Where(f => f != null)
                    .OrderBy(f => f.declaringType ?? string.Empty, StringComparer.Ordinal)
                    .ThenBy(f => f.name ?? string.Empty, StringComparer.Ordinal))
                {
                    Append(text, "field.declaring", field.declaringType);
                    Append(text, "field.name", field.name);
                    Append(text, "field.type", field.type);
                    Append(text, "field.shape", field.shape);
                    Append(text, "field.flags", field.flags);
                    AppendMany(text, "field.former", field.formerNames);
                    Append(text, "field.managed-reference", field.managedReferenceMode);
                    Append(text, "field.unknown", field.unknown ? "1" : "0");
                }
                Append(text, "type.unknown", type.hasUnknown ? "1" : "0");
            }
            AppendMany(text, "unknown", descriptor.unknowns);
            return "sha256:" + ShadowHash.Text(text.ToString());
        }

        private static void Append(StringBuilder builder, string key, string value)
        {
            value = value ?? string.Empty;
            builder.Append(key).Append('=').Append(value.Length).Append(':').Append(value).Append('\n');
        }

        private static void AppendMany(StringBuilder builder, string key, IEnumerable<string> values)
        {
            foreach (var value in (values ?? Enumerable.Empty<string>()).Where(v => v != null).Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal))
                Append(builder, key, value);
        }
    }
}
