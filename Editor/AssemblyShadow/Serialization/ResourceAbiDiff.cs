using System;
using System.Collections.Generic;
using System.Linq;

namespace HybridCLR.Editor.AssemblyShadow
{
    [Serializable]
    public sealed class ResourceAbiDiff
    {
        public ResourceAbiDiffLevel level;
        public string[] changedTypes = new string[0];
        public string[] reasons = new string[0];
        public bool hasUnknown;

        public bool RequiresResourceRebuild
        {
            get { return level == ResourceAbiDiffLevel.ResourceRebuildRequired || level == ResourceAbiDiffLevel.UnknownRequiresReview; }
        }

        public static ResourceAbiDiff Compare(ResourceAbiDescriptor baseline, ResourceAbiDescriptor current)
        {
            return ResourceAbiDiffComparer.Compare(baseline, current);
        }
    }

    public enum ResourceAbiDiffLevel
    {
        None = 0,
        CodeOnly = 1,
        ResourceCompatible = 2,
        ResourceRebuildRequired = 3,
        UnknownRequiresReview = 4,
    }

    public static class ResourceAbiDiffComparer
    {
        public static ResourceAbiDiff Compare(ResourceAbiDescriptor baseline, ResourceAbiDescriptor current)
        {
            if (baseline == null) throw new ArgumentNullException("baseline");
            if (current == null) throw new ArgumentNullException("current");
            var result = new ResourceAbiDiff();
            var reasons = new HashSet<string>(StringComparer.Ordinal);
            var changed = new HashSet<string>(StringComparer.Ordinal);
            if (baseline.schemaVersion != current.schemaVersion)
                reasons.Add("resource ABI schema changed from " + baseline.schemaVersion + " to " + current.schemaVersion + ".");
            bool callbackUnknown = false;
            bool unknown = HasUnknown(baseline) || HasUnknown(current) ||
                baseline.schemaVersion != ResourceAbiHasher.SchemaVersion || current.schemaVersion != ResourceAbiHasher.SchemaVersion;
            if (baseline.schemaVersion != ResourceAbiHasher.SchemaVersion || current.schemaVersion != ResourceAbiHasher.SchemaVersion)
                reasons.Add("unsupported resource ABI schema requires a new baseline.");
            foreach (var reason in (baseline.unknowns ?? new string[0]).Concat(current.unknowns ?? new string[0])) reasons.Add(reason);
            var oldTypes = MapTypes(baseline.types);
            var newTypes = MapTypes(current.types);
            foreach (var key in oldTypes.Keys.Union(newTypes.Keys).OrderBy(k => k, StringComparer.Ordinal))
            {
                ResourceAbiTypeDescriptor oldType, newType;
                if (!oldTypes.TryGetValue(key, out oldType))
                {
                    if (newTypes[key].serializationCallback) callbackUnknown = true;
                    changed.Add(key); reasons.Add("resource type added: " + key); continue;
                }
                if (!newTypes.TryGetValue(key, out newType))
                {
                    if (oldType.serializationCallback) callbackUnknown = true;
                    changed.Add(key); reasons.Add("resource type removed or renamed: " + key); continue;
                }
                if ((oldType.serializationCallback || newType.serializationCallback) &&
                    (oldType.serializationCallback != newType.serializationCallback || string.IsNullOrEmpty(oldType.callbackSemanticHash) ||
                     string.IsNullOrEmpty(newType.callbackSemanticHash) || !string.Equals(oldType.callbackSemanticHash, newType.callbackSemanticHash, StringComparison.Ordinal)))
                {
                    callbackUnknown = true;
                    changed.Add(key);
                    reasons.Add("serialization callback implementation changed or is unproven: " + key);
                }
                if (!TypeEqual(oldType, newType))
                {
                    changed.Add(key);
                    AddTypeReasons(oldType, newType, reasons);
                }
            }
            result.changedTypes = changed.OrderBy(v => v, StringComparer.Ordinal).ToArray();
            result.reasons = reasons.OrderBy(v => v, StringComparer.Ordinal).ToArray();
            unknown |= callbackUnknown;
            result.hasUnknown = unknown;
            if (callbackUnknown)
                reasons.Add("serialization callback body compatibility cannot be proven statically.");
            if (unknown && !callbackUnknown && (baseline.unknowns ?? new string[0]).Length + (current.unknowns ?? new string[0]).Length > 0)
                reasons.Add("unsupported or unresolved serialized type requires review.");
            result.reasons = reasons.OrderBy(v => v, StringComparer.Ordinal).ToArray();
            if (unknown)
            {
                result.level = ResourceAbiDiffLevel.UnknownRequiresReview;
            }
            else if (changed.Count != 0)
            {
                result.level = ResourceAbiDiffLevel.ResourceRebuildRequired;
            }
            else if (!string.Equals(ResourceAbiHasher.Compute(baseline), ResourceAbiHasher.Compute(current), StringComparison.Ordinal))
            {
                result.level = ResourceAbiDiffLevel.ResourceCompatible;
                result.reasons = new[] { "ABI metadata changed without a serialized layout change." };
            }
            else
            {
                result.level = ResourceAbiDiffLevel.None;
            }
            return result;
        }

        private static Dictionary<string, ResourceAbiTypeDescriptor> MapTypes(IEnumerable<ResourceAbiTypeDescriptor> types)
        {
            var result = new Dictionary<string, ResourceAbiTypeDescriptor>(StringComparer.Ordinal);
            foreach (var type in types ?? Enumerable.Empty<ResourceAbiTypeDescriptor>())
            {
                if (type == null || string.IsNullOrEmpty(type.typeKey)) continue;
                result[type.typeKey] = type;
            }
            return result;
        }

        private static bool HasUnknown(ResourceAbiDescriptor descriptor)
        {
            var types = descriptor.types ?? new ResourceAbiTypeDescriptor[0];
            return (descriptor.unknowns ?? new string[0]).Length != 0 ||
                types.Any(t => t == null || string.IsNullOrEmpty(t.typeKey) || t.hasUnknown || (t.unknownReasons ?? new string[0]).Length != 0 ||
                    (t.fields ?? new ResourceAbiFieldDescriptor[0]).Any(f => f == null || f.unknown)) ||
                types.Where(t => t != null).GroupBy(t => t.typeKey, StringComparer.Ordinal).Any(g => g.Count() > 1);
        }

        private static bool TypeEqual(ResourceAbiTypeDescriptor left, ResourceAbiTypeDescriptor right)
        {
            return string.Equals(left.typeKey, right.typeKey, StringComparison.Ordinal) &&
                string.Equals(left.assembly, right.assembly, StringComparison.Ordinal) &&
                string.Equals(left.@namespace, right.@namespace, StringComparison.Ordinal) &&
                string.Equals(left.type, right.type, StringComparison.Ordinal) &&
                string.Equals(left.baseChain, right.baseChain, StringComparison.Ordinal) &&
                SetEqual(left.interfaces, right.interfaces) &&
                left.serializationCallback == right.serializationCallback &&
                string.Equals(left.callbackSemanticHash, right.callbackSemanticHash, StringComparison.Ordinal) &&
                SetEqual(left.referencedTypeKeys, right.referencedTypeKeys) &&
                SetEqual(left.serializeReferenceCandidates, right.serializeReferenceCandidates) &&
                FieldSetEqual(left.fields, right.fields);
        }

        private static bool FieldSetEqual(IEnumerable<ResourceAbiFieldDescriptor> left, IEnumerable<ResourceAbiFieldDescriptor> right)
        {
            var l = (left ?? Enumerable.Empty<ResourceAbiFieldDescriptor>()).Where(f => f != null).Select(FieldSignature).OrderBy(v => v, StringComparer.Ordinal);
            var r = (right ?? Enumerable.Empty<ResourceAbiFieldDescriptor>()).Where(f => f != null).Select(FieldSignature).OrderBy(v => v, StringComparer.Ordinal);
            return l.SequenceEqual(r, StringComparer.Ordinal);
        }

        private static string FieldSignature(ResourceAbiFieldDescriptor field)
        {
            return string.Join("|", new[] { field.declaringType, field.name, field.type, field.shape, field.flags,
                string.Join(",", (field.formerNames ?? new string[0]).Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal)),
                field.managedReferenceMode, field.unknown ? "1" : "0" });
        }

        private static bool SetEqual(IEnumerable<string> left, IEnumerable<string> right)
        {
            return new HashSet<string>(left ?? Enumerable.Empty<string>(), StringComparer.Ordinal)
                .SetEquals(right ?? Enumerable.Empty<string>());
        }

        private static void AddTypeReasons(ResourceAbiTypeDescriptor oldType, ResourceAbiTypeDescriptor newType, ISet<string> reasons)
        {
            if (!string.Equals(oldType.baseChain, newType.baseChain, StringComparison.Ordinal)) reasons.Add("base chain changed: " + oldType.typeKey);
            if (!SetEqual(oldType.interfaces, newType.interfaces)) reasons.Add("interfaces changed: " + oldType.typeKey);
            if (!FieldSetEqual(oldType.fields, newType.fields)) reasons.Add("serialized field layout changed: " + oldType.typeKey);
            if (oldType.serializationCallback != newType.serializationCallback) reasons.Add("serialization callback contract changed: " + oldType.typeKey);
            if (!SetEqual(oldType.serializeReferenceCandidates, newType.serializeReferenceCandidates)) reasons.Add("SerializeReference concrete type set changed: " + oldType.typeKey);
            if (!SetEqual(oldType.referencedTypeKeys, newType.referencedTypeKeys)) reasons.Add("serialized dependency graph changed: " + oldType.typeKey);
        }
    }

    [Serializable]
    public sealed class ResourceScriptIndex
    {
        public int schemaVersion = 2;
        public ResourceScriptIndexEntry[] entries = new ResourceScriptIndexEntry[0];
        public string[] bundles = new string[0];
        public bool hasUnknown;
        public string[] unknowns = new string[0];
    }

    [Serializable]
    public sealed class ResourceScriptIndexEntry
    {
        public string typeKey;
        public string[] assetGuids = new string[0];
        public string[] assetPaths = new string[0];
        public string[] bundleNames = new string[0];
    }
}
