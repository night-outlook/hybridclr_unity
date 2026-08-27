using System;
using System.Collections.Generic;
using System.Linq;

namespace HybridCLR.Editor.AssemblyShadow
{
    public static class ShadowFilteredInputPolicy
    {
        // The caller must first ReadAndVerify the immutable Player snapshot.
        // This method verifies the receipt contract and derives a separate policy;
        // it never changes the user's source capability declarations.
        public static ShadowPolicyConfiguration Apply(ShadowPolicyConfiguration policy, AssemblySnapshotReceipt playerReceipt)
        {
            ShadowHash.Require(policy != null && playerReceipt != null && playerReceipt.schemaVersion == 1 &&
                playerReceipt.kind == "PlayerBuildInputs" && playerReceipt.playerBuildSucceeded && playerReceipt.playerBuildFilterCaptured &&
                !string.IsNullOrWhiteSpace(playerReceipt.buildGuid) && !string.IsNullOrWhiteSpace(playerReceipt.nativeLibrarySha256) &&
                playerReceipt.sourcePins != null, "FilterEvidenceMissing", "Build-filter roles require a successful, captured Player-build receipt.");
            ShadowHash.Require(AssemblySnapshot.ComputeHash(playerReceipt) == playerReceipt.snapshotHash,
                "FilterEvidenceHashMismatch", "Player filter evidence no longer matches the frozen receipt hash.");
            ShadowLinkedPlayerEvidence.ValidateReceipt(playerReceipt);
            var source = CapabilityMap(policy.assemblies, "SourceCapabilityInvalid");
            var stamped = CapabilityMap(playerReceipt.filteredAssemblyCapabilities, "FilteredCapabilityInvalid");
            var filtered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (SnapshotFile file in playerReceipt.filteredAssemblies ?? new SnapshotFile[0])
            {
                ShadowHash.Require(file != null && !string.IsNullOrWhiteSpace(file.name) && !string.IsNullOrWhiteSpace(file.path) &&
                    !string.IsNullOrWhiteSpace(file.sha256), "FilterEvidenceMissing", "A removed DLL is missing its captured identity, path or hash.");
                ShadowHash.Require(filtered.Add(AssemblyIdentityUtil.CanonicalName(file.name)), "DuplicateFilteredAssembly", file.name);
            }
            ShadowHash.Require(filtered.SetEquals(stamped.Keys), "FilteredCapabilitySetMismatch", "Every removed DLL needs exactly one original capability stamp.");
            var actual = new HashSet<string>((playerReceipt.assemblies ?? new SnapshotFile[0]).Select(file => AssemblyIdentityUtil.CanonicalName(file.name)), StringComparer.OrdinalIgnoreCase);
            ShadowHash.Require(!actual.Overlaps(filtered), "FilteredAssemblyStillInPlayer", "A captured input cannot be both retained and removed.");
            var linkerStamped = CapabilityMap(playerReceipt.linkerExcludedAssemblyCapabilities, "LinkerCapabilityInvalid");
            foreach (string name in playerReceipt.linkerExcludedAssemblies ?? new string[0])
            {
                ShadowHash.Require(filtered.Add(name), "DuplicateFilteredAssembly", name);
                stamped.Add(name, linkerStamped[name]);
            }
            var normal = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in playerReceipt.normalHotUpdateAssemblies ?? new string[0])
                ShadowHash.Require(!string.IsNullOrWhiteSpace(name) && normal.Add(AssemblyIdentityUtil.CanonicalName(name)),
                    "InvalidHotUpdateEvidence", "Recorded ordinary-hotupdate names are empty or duplicated.");
            var currentNormal = new HashSet<string>(source.Values.Where(item => item.classification == AssemblyClassification.NormalHotUpdate)
                .Select(item => AssemblyIdentityUtil.CanonicalName(item.name)), StringComparer.OrdinalIgnoreCase);
            ShadowHash.Require(normal.SetEquals(currentNormal), "HotUpdateRoleChanged", "Ordinary-hotupdate roles differ from the captured Player build.");
            foreach (string name in filtered)
            {
                AssemblyCapability current;
                ShadowHash.Require(source.TryGetValue(name, out current), "FilteredCapabilityMissing", "Removed DLL is absent from the current source policy: " + name);
                AssemblyCapability original = stamped[name];
                ShadowHash.Require(!original.isShadowCapable && !original.isBootstrap && !current.isShadowCapable && !current.isBootstrap,
                    "FilteredCandidatePromotion", "Candidate/bootstrap cannot be removed by a Player filter: " + name);
                ShadowHash.Require(SameRole(original, current), "FilteredRoleChanged", "Source capability changed since the captured Player filter: " + name);
                ShadowHash.Require((original.classification == AssemblyClassification.NormalHotUpdate) == normal.Contains(name),
                    "FilteredRoleChanged", "Removed DLL has inconsistent ordinary-hotupdate evidence: " + name);
                ShadowHash.Require(original.classification != AssemblyClassification.BuildFiltered,
                    "InvalidFilteredSourceRole", "Filter evidence must preserve the original compiler role: " + name);
            }
            foreach (AssemblyCapability capability in source.Values)
                ShadowHash.Require(capability.classification != AssemblyClassification.BuildFiltered, "InvalidFilteredSourceRole",
                    "BuildFiltered is a derived receipt role, not a source policy declaration: " + capability.name);
            var linkedNames = new HashSet<string>(playerReceipt.linkedPlayerReceipt.assemblies.Select(file => file.name), StringComparer.OrdinalIgnoreCase);
            var protectedNames = new HashSet<string>(playerReceipt.linkedPlayerReceipt.protectedAssemblies, StringComparer.OrdinalIgnoreCase);
            var currentProtected = new HashSet<string>(source.Values.Where(item => item.isShadowCapable || item.isBootstrap)
                .Select(item => AssemblyIdentityUtil.CanonicalName(item.name)), StringComparer.OrdinalIgnoreCase);
            ShadowHash.Require(protectedNames.SetEquals(currentProtected) && currentProtected.IsSubsetOf(linkedNames),
                "LinkedCandidateSetChanged", "Current candidate/bootstrap roles must match the frozen protected linked inventory.");

            ShadowPolicyConfiguration derived = Clone(policy);
            foreach (AssemblyCapability capability in derived.assemblies)
            {
                string name = AssemblyIdentityUtil.CanonicalName(capability.name);
                if (filtered.Contains(name)) capability.classification = normal.Contains(name) ? AssemblyClassification.NormalHotUpdate : AssemblyClassification.BuildFiltered;
            }
            return derived;
        }

        private static Dictionary<string, AssemblyCapability> CapabilityMap(IEnumerable<AssemblyCapability> items, string code)
        {
            var result = new Dictionary<string, AssemblyCapability>(StringComparer.OrdinalIgnoreCase);
            foreach (AssemblyCapability item in items ?? new AssemblyCapability[0])
            {
                ShadowHash.Require(item != null && !string.IsNullOrWhiteSpace(item.name), code, "Capability evidence has no assembly name.");
                string name = AssemblyIdentityUtil.CanonicalName(item.name);
                ShadowHash.Require(!result.ContainsKey(name), code, "Duplicate capability: " + name); result.Add(name, item);
            }
            return result;
        }

        private static bool SameRole(AssemblyCapability first, AssemblyCapability second)
        {
            return first.classification == second.classification && first.isPrecompiled == second.isPrecompiled &&
                first.isShadowCapable == second.isShadowCapable && first.isBootstrap == second.isBootstrap && first.capabilityDeclared == second.capabilityDeclared;
        }

        private static ShadowPolicyConfiguration Clone(ShadowPolicyConfiguration source)
        {
            var result = new ShadowPolicyConfiguration
            {
                assemblies = (source.assemblies ?? new AssemblyCapability[0]).Select(item => new AssemblyCapability
                { name = item.name, classification = item.classification, isShadowCapable = item.isShadowCapable, isBootstrap = item.isBootstrap,
                    isPrecompiled = item.isPrecompiled, capabilityDeclared = item.capabilityDeclared }).ToArray(),
                rejectUnknownReflectionDependencies = source.rejectUnknownReflectionDependencies,
                enforceResourceAbi = source.enforceResourceAbi,
                reflectionBindingConfigurationSha256 = source.reflectionBindingConfigurationSha256,
                reflectionBindingConfigurationHash = source.reflectionBindingConfigurationHash,
                reflectionBindings = (source.reflectionBindings ?? new ShadowReflectionBindingDeclaration[0]).Select(item => item == null ? null :
                    new ShadowReflectionBindingDeclaration { id = item.id, consumer = item.consumer, typeName = item.typeName,
                        methodSignature = item.methodSignature, originalMethodHash = item.originalMethodHash, operationIndex = item.operationIndex,
                        allowedTypes = (item.allowedTypes ?? new string[0]).ToArray(), providers = (item.providers ?? new string[0]).ToArray(), reason = item.reason }).ToArray(),
                allowedInternalEditorAssemblies = (source.allowedInternalEditorAssemblies ?? new string[0]).ToArray(),
                dependencies = source.dependencies == null ? null : new ShadowDependencyConfiguration
                {
                    schemaVersion = source.dependencies.schemaVersion,
                    runtimeDependencies = (source.dependencies.runtimeDependencies ?? new DeclaredRuntimeDependency[0]).Select(item => item == null ? null :
                        new DeclaredRuntimeDependency { consumer = item.consumer, provider = item.provider, kind = item.kind, evidence = item.evidence, callSite = item.callSite }).ToArray(),
                    resourceDependencies = (source.dependencies.resourceDependencies ?? new DeclaredResourceDependency[0]).Select(item => item == null ? null :
                        new DeclaredResourceDependency { bundle = item.bundle, assembly = item.assembly }).ToArray(),
                    bootstrapEntrypoints = (source.dependencies.bootstrapEntrypoints ?? new BootstrapEntrypointDeclaration[0]).Select(item => item == null ? null :
                        new BootstrapEntrypointDeclaration { consumer = item.consumer, provider = item.provider, typeName = item.typeName, method = item.method,
                            reason = item.reason, callSite = item.callSite, target = item.target }).ToArray(),
                },
                extensibilityWhitelist = source.extensibilityWhitelist == null ? null : new ExtensibilityWhitelist
                {
                    schemaVersion = source.extensibilityWhitelist.schemaVersion,
                    entries = (source.extensibilityWhitelist.entries ?? new ExtensibilityWhitelistEntry[0]).Select(item => item == null ? null :
                        new ExtensibilityWhitelistEntry { provider = item.provider, consumer = item.consumer, owner = item.owner, reason = item.reason,
                            reviewer = item.reviewer, expires = item.expires }).ToArray(),
                },
            };
            return result;
        }
    }
}
