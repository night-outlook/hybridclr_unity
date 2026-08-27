using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dnlib.DotNet;

namespace HybridCLR.Editor.AssemblyShadow
{
    /// <summary>Evidence that a fixed AOT consumer lost a compiler-only AssemblyRef during the captured Player link.</summary>
    public sealed class VerifiedLinkedRuntimeReferences
    {
        private sealed class Binding
        {
            internal string consumer, provider, compilerPath, compilerHash, linkedPath, linkedHash;
            internal string providerPath, providerHash, currentPath, currentHash, currentProviderPath, currentProviderHash;
            internal string consumerState, providerState, consumerRole, providerRole;
            internal ModuleDefMD consumerModule, providerModule;
        }

        private readonly CompiledAssemblySet boundSet;
        private readonly Binding[] bindings;
        private readonly string receiptPath, receiptHash, linkedReceiptPath, linkedReceiptHash, nativePath, nativeHash;
        private readonly IReadOnlyList<string> removedReferences;

        private VerifiedLinkedRuntimeReferences(CompiledAssemblySet set, string root, AssemblySnapshotReceipt receipt, Binding[] evidence)
        {
            boundSet = set; bindings = evidence;
            receiptPath = Path.Combine(root, AssemblySnapshot.ReceiptName); receiptHash = ShadowHash.File(receiptPath);
            linkedReceiptPath = Path.Combine(root, ShadowLinkedPlayerEvidence.DirectoryName, ShadowLinkedPlayerEvidence.ReceiptName);
            linkedReceiptHash = ShadowHash.File(linkedReceiptPath);
            nativePath = receipt.nativeLibraryPath; nativeHash = receipt.nativeLibrarySha256;
            SnapshotHash = receipt.snapshotHash; LinkedPlayerHash = receipt.linkedPlayerReceiptHash;
            removedReferences = Array.AsReadOnly(bindings.Select(b => EdgeKey(b.consumer, b.provider)).OrderBy(s => s, StringComparer.Ordinal).ToArray());
            ProvenanceHash = ShadowHash.Text("assembly-shadow-linked-runtime-references:1\n" + SnapshotHash + "\n" + LinkedPlayerHash + "\n" + nativeHash + "\n" +
                string.Join("\n", bindings.OrderBy(b => EdgeKey(b.consumer, b.provider), StringComparer.Ordinal).Select(b =>
                    EdgeKey(b.consumer, b.provider) + "|" + b.compilerHash + "|" + b.currentHash + "|" + b.linkedHash + "|" + b.providerHash + "|" +
                    b.currentProviderHash + "|" + b.consumerRole + "|" + b.providerRole).ToArray()));
        }

        public string SnapshotHash { get; private set; }
        public string LinkedPlayerHash { get; private set; }
        public string ProvenanceHash { get; private set; }
        public IReadOnlyList<string> RemovedReferences { get { return removedReferences; } }

        public static VerifiedLinkedRuntimeReferences Verify(string root, AssemblySnapshotReceipt expectedPlayerReceipt,
            CompiledAssemblySet currentSet, ShadowPolicyConfiguration derivedPolicy)
        {
            ShadowHash.Require(currentSet != null && derivedPolicy != null && expectedPlayerReceipt != null,
                "LinkedRuntimeEvidenceMissing", "A loaded set, derived policy and expected Player receipt are required.");
            var receipt = AssemblySnapshot.ReadAndVerify(root, true);
            ShadowHash.Require(expectedPlayerReceipt.snapshotHash == AssemblySnapshot.ComputeHash(expectedPlayerReceipt) &&
                receipt.snapshotHash == expectedPlayerReceipt.snapshotHash && receipt.linkedPlayerReceiptHash == expectedPlayerReceipt.linkedPlayerReceiptHash &&
                receipt.buildGuid == expectedPlayerReceipt.buildGuid && receipt.nativeLibrarySha256 == expectedPlayerReceipt.nativeLibrarySha256 &&
                receipt.nativeLibraryPath == expectedPlayerReceipt.nativeLibraryPath && receipt.unityVersion == expectedPlayerReceipt.unityVersion &&
                receipt.target == expectedPlayerReceipt.target && receipt.architecture == expectedPlayerReceipt.architecture,
                "LinkedRuntimeSnapshotMismatch", "Expected and reverified Player/native identities differ.");
            ShadowHash.Require(File.Exists(receipt.nativeLibraryPath) && ShadowHash.File(receipt.nativeLibraryPath) == receipt.nativeLibrarySha256,
                "LinkedRuntimeNativeMismatch", "The captured native Player artifact is absent or changed.");

            var inputs = receipt.assemblies.ToDictionary(f => Canonical(f.name), StringComparer.OrdinalIgnoreCase);
            var linked = receipt.linkedPlayerReceipt.assemblies.ToDictionary(f => Canonical(f.name), StringComparer.OrdinalIgnoreCase);
            var excluded = new HashSet<string>(receipt.linkerExcludedAssemblies ?? new string[0], StringComparer.OrdinalIgnoreCase);
            var callbackFiltered = new HashSet<string>((receipt.filteredAssemblies ?? new SnapshotFile[0]).Select(f => Canonical(f.name)), StringComparer.OrdinalIgnoreCase);
            var protectedNames = new HashSet<string>(receipt.linkedPlayerReceipt.protectedAssemblies, StringComparer.OrdinalIgnoreCase);
            var normal = new HashSet<string>((receipt.normalHotUpdateAssemblies ?? new string[0]).Select(Canonical), StringComparer.OrdinalIgnoreCase);
            var originalRoles = receipt.linkerExcludedAssemblyCapabilities.ToDictionary(c => Canonical(c.name), StringComparer.OrdinalIgnoreCase);
            var evidence = new List<Binding>();
            foreach (var item in currentSet.Assemblies.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                string name = Canonical(item.Key); AssemblyDescriptor consumer = item.Value;
                AssemblyCapability role = Capability(derivedPolicy, name);
                SnapshotFile compiler; LinkedPlayerFile retained; ModuleDefMD module;
                // The authenticated protected union cannot distinguish a candidate from
                // Bootstrap. Exclude the entire union, even after a policy demotion/swap.
                if (protectedNames.Contains(name) || normal.Contains(name) || callbackFiltered.Contains(name) || excluded.Contains(name) ||
                    role == null || role.classification != AssemblyClassification.Runtime || role.isShadowCapable || role.isBootstrap ||
                    !SameRole(consumer, role) || !inputs.TryGetValue(name, out compiler) || !linked.TryGetValue(name, out retained) ||
                    !currentSet.Modules.TryGetValue(name, out module)) continue;
                string compilerPath = ShadowHash.SafeChild(root, compiler.path);
                // Only the frozen fixed AOT image executes. Recompilation may change
                // MVID/PDB bytes, but not full assembly identity or any semantic section.
                if (!MatchesCompiler(module, consumer, compilerPath, compiler.sha256, true)) continue;
                string linkedPath = ShadowHash.SafeChild(root, ShadowLinkedPlayerEvidence.DirectoryName + "/" + retained.path);
                using (var linkedModule = ModuleDefMD.Load(File.ReadAllBytes(linkedPath)))
                {
                    ShadowHash.Require(AssemblyNameComparer.CompareAll.Equals(module.Assembly, linkedModule.Assembly),
                        "LinkedRuntimeIdentityMismatch", name + " changed assembly identity during linking.");
                    var retainedReferences = new HashSet<string>(linkedModule.GetAssemblyRefs().Select(r => Canonical(r.Name.String)), StringComparer.OrdinalIgnoreCase);
                    foreach (string providerName in module.GetAssemblyRefs().Select(r => Canonical(r.Name.String)).Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        AssemblyCapability original, providerRole = Capability(derivedPolicy, providerName);
                        SnapshotFile providerInput; AssemblyDescriptor provider; ModuleDefMD providerModule;
                        if (!excluded.Contains(providerName) || callbackFiltered.Contains(providerName) || protectedNames.Contains(providerName) || normal.Contains(providerName) ||
                            retainedReferences.Contains(providerName) || !originalRoles.TryGetValue(providerName, out original) ||
                            original.classification != AssemblyClassification.Runtime || original.isShadowCapable || original.isBootstrap ||
                            providerRole == null || providerRole.classification != AssemblyClassification.BuildFiltered || providerRole.isShadowCapable || providerRole.isBootstrap ||
                            providerRole.isPrecompiled != original.isPrecompiled || providerRole.capabilityDeclared != original.capabilityDeclared ||
                            !inputs.TryGetValue(providerName, out providerInput) || !currentSet.Assemblies.TryGetValue(providerName, out provider) ||
                            !SameRole(provider, providerRole) || !currentSet.Modules.TryGetValue(providerName, out providerModule)) continue;
                        string providerPath = ShadowHash.SafeChild(root, providerInput.path);
                        if (!MatchesCompiler(providerModule, provider, providerPath, providerInput.sha256)) continue;
                        evidence.Add(new Binding { consumer = name, provider = providerName, compilerPath = compilerPath, compilerHash = compiler.sha256,
                            linkedPath = linkedPath, linkedHash = retained.sha256, providerPath = providerPath, providerHash = providerInput.sha256,
                            currentPath = consumer.filePath, currentHash = consumer.sha256, currentProviderPath = provider.filePath, currentProviderHash = provider.sha256,
                            consumerModule = module, providerModule = providerModule, consumerState = State(module, consumer), providerState = State(providerModule, provider),
                            consumerRole = Role(role), providerRole = Role(providerRole) });
                    }
                }
            }
            return new VerifiedLinkedRuntimeReferences(currentSet, root, receipt, evidence.ToArray());
        }

        // Only compiled validation can consume this proof. Plain definitions and
        // explicitly declared/reflection edges never receive a waiver predicate.
        internal HashSet<string> ValidateFor(CompiledAssemblySet set, ShadowPolicyConfiguration policy)
        {
            ShadowHash.Require(object.ReferenceEquals(set, boundSet), "LinkedRuntimeProofSetMismatch", "Linked-reference proof belongs to another loaded set.");
            ShadowHash.Require(Unchanged(receiptPath, receiptHash) && Unchanged(linkedReceiptPath, linkedReceiptHash) && Unchanged(nativePath, nativeHash),
                "LinkedRuntimeEvidenceChanged", "Sealed Player/native evidence changed after proof creation.");
            foreach (Binding binding in bindings)
            {
                ModuleDefMD consumerModule, providerModule; AssemblyDescriptor consumer, provider;
                ShadowHash.Require(set.Modules.TryGetValue(binding.consumer, out consumerModule) && object.ReferenceEquals(consumerModule, binding.consumerModule) &&
                    set.Modules.TryGetValue(binding.provider, out providerModule) && object.ReferenceEquals(providerModule, binding.providerModule) &&
                    set.Assemblies.TryGetValue(binding.consumer, out consumer) && set.Assemblies.TryGetValue(binding.provider, out provider) &&
                    State(consumerModule, consumer) == binding.consumerState && State(providerModule, provider) == binding.providerState &&
                    Role(Capability(policy, binding.consumer)) == binding.consumerRole && Role(Capability(policy, binding.provider)) == binding.providerRole &&
                    Unchanged(binding.compilerPath, binding.compilerHash) && Unchanged(binding.linkedPath, binding.linkedHash) && Unchanged(binding.providerPath, binding.providerHash) &&
                    Unchanged(binding.currentPath, binding.currentHash) && Unchanged(binding.currentProviderPath, binding.currentProviderHash),
                    "LinkedRuntimeProofChanged", "Loaded metadata, role or linked bytes changed: " + EdgeKey(binding.consumer, binding.provider));
            }
            return new HashSet<string>(removedReferences, StringComparer.Ordinal);
        }

        internal static string EdgeKey(string consumer, string provider) { return Canonical(consumer) + " -> " + Canonical(provider); }
        private static string Canonical(string name) { return AssemblyIdentityUtil.CanonicalName(name); }
        private static bool Unchanged(string path, string hash) { return File.Exists(path) && ShadowHash.File(path) == hash; }
        private static AssemblyCapability Capability(ShadowPolicyConfiguration policy, string name)
        {
            var matches = (policy == null ? new AssemblyCapability[0] : policy.assemblies ?? new AssemblyCapability[0])
                .Where(c => c != null && Canonical(c.name) == name).ToArray();
            return matches.Length == 1 ? matches[0] : null;
        }
        private static string Role(AssemblyCapability role)
        {
            return role == null ? null : Canonical(role.name) + ":" + (int)role.classification + ":" + role.isShadowCapable + ":" + role.isBootstrap + ":" + role.isPrecompiled + ":" + role.capabilityDeclared;
        }
        private static bool SameRole(AssemblyDescriptor descriptor, AssemblyCapability role)
        {
            return descriptor != null && role != null && Canonical(descriptor.name) == Canonical(role.name) && descriptor.classification == role.classification &&
                descriptor.isShadowCapable == role.isShadowCapable && descriptor.isBootstrap == role.isBootstrap &&
                descriptor.isPrecompiled == role.isPrecompiled && descriptor.capabilityDeclared == role.capabilityDeclared;
        }
        private static bool MatchesCompiler(ModuleDefMD module, AssemblyDescriptor descriptor, string path, string hash, bool allowEquivalentFixedAot = false)
        {
            if (module.Assembly == null || (!allowEquivalentFixedAot && descriptor.sha256 != hash) ||
                ShadowHash.Bytes(module.Metadata.PEImage.CreateReader().ToArray()) != descriptor.sha256 ||
                !Unchanged(descriptor.filePath, descriptor.sha256) || !Unchanged(path, hash)) return false;
            using (var current = ModuleDefMD.Load(File.ReadAllBytes(descriptor.filePath)))
                if (module.Assembly.FullName != current.Assembly.FullName || module.Mvid != current.Mvid || Fingerprint(module) != Fingerprint(current)) return false;
            using (var captured = ModuleDefMD.Load(File.ReadAllBytes(path)))
                return module.Assembly.FullName == captured.Assembly.FullName && (allowEquivalentFixedAot || module.Mvid == captured.Mvid) &&
                    Fingerprint(module) == Fingerprint(captured) &&
                    Canonical(descriptor.name) == Canonical(module.Assembly.Name.String) && descriptor.mvid == module.Mvid.ToString() &&
                    descriptor.semanticHash == AssemblySemanticHasher.Compute(module).semanticHash &&
                    References(module).SequenceEqual((descriptor.references ?? new string[0]).Select(Canonical).OrderBy(n => n, StringComparer.Ordinal));
        }
        private static string[] References(ModuleDefMD module)
        { return module.GetAssemblyRefs().Select(r => Canonical(r.Name.String)).Distinct().OrderBy(n => n, StringComparer.Ordinal).ToArray(); }
        private static string State(ModuleDefMD module, AssemblyDescriptor descriptor)
        {
            return ShadowHash.Text(module.Assembly.FullName + "\n" + module.Mvid + "\n" + ShadowHash.Bytes(module.Metadata.PEImage.CreateReader().ToArray()) + "\n" +
                Fingerprint(module) + "\n" + descriptor.name + "\n" + descriptor.mvid + "\n" + descriptor.filePath + "\n" + descriptor.sha256 + "\n" +
                descriptor.semanticHash + "\n" + (int)descriptor.classification + ":" + descriptor.isShadowCapable + ":" + descriptor.isBootstrap + ":" + descriptor.isPrecompiled + ":" + descriptor.capabilityDeclared + "\n" +
                string.Join("\n", descriptor.references ?? new string[0]));
        }
        private static string Fingerprint(ModuleDefMD module)
        { return AssemblySemanticHasher.Compute(module, new SemanticHashOptions { IgnoredAttributeNames = new string[0] }).semanticHash; }
    }
}
