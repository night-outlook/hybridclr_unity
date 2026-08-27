using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;

namespace HybridCLR.Editor.AssemblyShadow
{
    /// <summary>Byte-bound compiler framework evidence. Only the target verifier can mint a production proof.</summary>
    public sealed class VerifiedTargetFrameworkReferences
    {
        private readonly Dictionary<string, string> hashesByIdentity;
        private readonly IReadOnlyList<string> providers;

        internal VerifiedTargetFrameworkReferences(string unityVersion, string target, string architecture,
            string apiCompatibilityLevel, string snapshotHash, IDictionary<string, string> verifiedProviders)
        {
            UnityVersion = unityVersion;
            Target = target;
            Architecture = architecture;
            ApiCompatibilityLevel = apiCompatibilityLevel;
            SnapshotHash = snapshotHash;
            hashesByIdentity = new Dictionary<string, string>(verifiedProviders, StringComparer.Ordinal);
            providers = Array.AsReadOnly(hashesByIdentity.OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => p.Key + " | sha256=" + p.Value).ToArray());
            ProvenanceHash = ShadowHash.Text("assembly-shadow-target-framework:1\n" + UnityVersion + "\n" + Target + "\n" +
                Architecture + "\n" + ApiCompatibilityLevel + "\n" + SnapshotHash + "\n" + string.Join("\n", providers.ToArray()));
        }

        public string UnityVersion { get; private set; }
        public string Target { get; private set; }
        public string Architecture { get; private set; }
        public string ApiCompatibilityLevel { get; private set; }
        public string SnapshotHash { get; private set; }
        public string ProvenanceHash { get; private set; }
        public IReadOnlyList<string> Providers { get { return providers; } }

        internal bool Contains(IAssembly assembly, string loadedSha256)
        {
            string expected;
            return assembly != null && hashesByIdentity.TryGetValue(assembly.FullName, out expected) &&
                string.Equals(expected, loadedSha256, StringComparison.Ordinal);
        }
    }
}
