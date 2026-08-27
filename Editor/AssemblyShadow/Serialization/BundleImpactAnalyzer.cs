using System;
using System.Collections.Generic;
using System.Linq;

namespace HybridCLR.Editor.AssemblyShadow
{
    public static class BundleImpactAnalyzer
    {
        public static string[] GetAffectedBundles(ResourceAbiDiff diff, ResourceScriptIndex index)
        {
            if (diff == null) throw new ArgumentNullException("diff");
            if (index == null) throw new ArgumentNullException("index");
            var all = new HashSet<string>((index.bundles ?? new string[0]).Where(v => !string.IsNullOrEmpty(v)), StringComparer.Ordinal);
            if (diff.level == ResourceAbiDiffLevel.UnknownRequiresReview || diff.hasUnknown || index.hasUnknown || index.schemaVersion != 2)
                return all.OrderBy(v => v, StringComparer.Ordinal).ToArray();
            if (diff.level == ResourceAbiDiffLevel.None || diff.level == ResourceAbiDiffLevel.CodeOnly) return new string[0];
            var changed = new HashSet<string>(diff.changedTypes ?? new string[0], StringComparer.Ordinal);
            foreach (var entry in index.entries ?? new ResourceScriptIndexEntry[0])
            {
                if (entry == null || !changed.Contains(entry.typeKey)) continue;
                foreach (var bundle in entry.bundleNames ?? new string[0]) if (!string.IsNullOrEmpty(bundle)) all.Add(bundle);
            }
            // A changed serialized type with no index entry cannot be proven unused.
            var indexedTypes = new HashSet<string>((index.entries ?? new ResourceScriptIndexEntry[0]).Where(e => e != null).Select(e => e.typeKey), StringComparer.Ordinal);
            if (changed.Any(type => !indexedTypes.Contains(type)))
                return all.OrderBy(v => v, StringComparer.Ordinal).ToArray();
            return all.Where(bundle => (index.entries ?? new ResourceScriptIndexEntry[0]).Any(e => e != null && changed.Contains(e.typeKey) && (e.bundleNames ?? new string[0]).Contains(bundle)))
                .OrderBy(v => v, StringComparer.Ordinal).ToArray();
        }
    }
}
