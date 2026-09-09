using System;
using System.IO;
using System.Linq;
using UnityEngine;

namespace HybridCLR.Editor.AssemblyShadow
{
    public static class ShadowPatchManifestBuilder
    {
        public static ShadowPatchManifest Build(ShadowPatchBuildRequest request)
        {
            return (ShadowPatchManifest)BuildCore(request, null);
        }

        public static ShadowPatchManifestV2 BuildWithWarmup(ShadowPatchBuildRequest request, ShadowWarmupPlan warmup)
        {
            ShadowHash.Require(warmup != null, "WarmupPlan", "Schema 2 requires an explicit nonnull warmup plan.");
            return (ShadowPatchManifestV2)BuildCore(request, warmup);
        }

        private static object BuildCore(ShadowPatchBuildRequest request, ShadowWarmupPlan warmup)
        {
            ShadowHash.Require(request != null && request.policy != null && !string.IsNullOrWhiteSpace(request.patchId), "InvalidPatchRequest", "Patch ID and policy are required.");
            var baseline = ShadowArtifactWriter.ReadVerifiedManifest<ShadowBaselineManifest>(request.baselineManifestPath);
            ShadowHash.Require(baseline.schemaVersion == 1 && baseline.semanticHashSchema == 1, "ManifestSchema", request.baselineManifestPath);
            ShadowSourcePins.RequireCompatible(baseline.sourcePins, request.sourcePins);
            ShadowHash.Require(baseline.target == request.target.ToString() && baseline.architecture == request.architecture && baseline.unityVersion == Application.unityVersion,
                "PatchTargetMismatch", "Exact Unity, target and architecture must match the baseline.");
            var receipt = AssemblySnapshot.ReadAndVerify(request.currentCompileSnapshot, false);
            ShadowSourcePins.RequireCompatible(baseline.sourcePins, receipt.sourcePins);
            ShadowHash.Require(receipt.target == baseline.target && receipt.architecture == baseline.architecture && receipt.unityVersion == baseline.unityVersion, "CompileTargetMismatch", request.currentCompileSnapshot);
            string baselineRoot = Path.GetDirectoryName(request.baselineManifestPath);
            var frozenResources = ShadowResourceBaseline.ReadAndVerify(ShadowHash.SafeChild(baselineRoot, baseline.resourceBaselinePath), request.target, request.architecture);
            ShadowHash.Require(ShadowHash.File(Path.Combine(frozenResources.Root, ShadowResourceBaseline.ReceiptName)) == baseline.resourceBuildReceiptHash,
                "BaselineResourceReceiptMismatch", "Frozen resource build receipt changed.");
            string abiPath = Path.Combine(baselineRoot, "resource-abi.json");
            string indexPath = Path.Combine(baselineRoot, "resource-script-index.json");
            var baselineAbi = JsonUtility.FromJson<ResourceAbiDescriptor>(File.ReadAllText(abiPath));
            var index = JsonUtility.FromJson<ResourceScriptIndex>(File.ReadAllText(indexPath));
            ShadowHash.Require(ResourceAbiHasher.Compute(baselineAbi) == baseline.resourceAbiHash && ShadowHash.File(indexPath) == baseline.resourceIndexHash,
                "BaselineResourceHashMismatch", "Frozen resource ABI or index was modified.");
            ShadowResourceBaseline.RequirePlayerAbi(frozenResources, baselineAbi);
            ShadowHash.Require(ShadowHash.Text(JsonUtility.ToJson(frozenResources.Index, true)) == baseline.resourceIndexHash,
                "BaselineResourceIndexMismatch", "Baseline index does not match its frozen bundle build.");
            var frozenReceipt = AssemblySnapshot.ReadAndVerify(Path.Combine(baselineRoot, baseline.playerInputSnapshot), true);
            ShadowHash.Require(frozenReceipt.snapshotHash == baseline.playerInputSnapshotHash, "BaselineInputMismatch", baselineRoot);
            var policy = ShadowFilteredInputPolicy.ApplyPatch(request.policy, frozenReceipt, baseline.shadowCandidates, baseline.bootstrapAssemblies);
            ShadowReflectionBindingEvidence.RequirePolicy(policy, request.currentCompileSnapshot, receipt, false);
            ShadowHash.Require((policy.reflectionBindingConfigurationHash ?? "") == (baseline.reflectionBindingConfigurationHash ?? ""),
                "ReflectionBindingPolicyChanged", "Changing a fixed AOT reflection contract requires a new Player baseline.");
            using (var set = ShadowBaselineManifestBuilder.LoadSnapshot(request.currentCompileSnapshot, policy, receipt))
            {
                // A structural resource change is already sufficient to reject a
                // DLL-only request. Report that deployment decision before the
                // general compiled-policy pass, where a deliberately renamed
                // serialized type can also invalidate a Bootstrap type literal.
                // Resource-inclusive requests still receive the complete policy
                // validation below before any output directory is created.
                var resourceAbi = UnitySerializedTypeAnalyzer.Analyze(set, baseline.shadowCandidates);
                var diff = ResourceAbiDiff.Compare(baselineAbi, resourceAbi);
                string[] affected = BundleImpactAnalyzer.GetAffectedBundles(diff, index);
                ShadowHash.Require(!request.dllOnly || (!diff.RequiresResourceRebuild && !index.hasUnknown), "ResourceRebuildRequired",
                    "DLL-only rejected. Affected bundles: " + string.Join(", ", affected) + ". " + string.Join("; ", diff.reasons));
                ShadowReflectionBindingEvidence.AddCompiledDependencies(policy, set.Assemblies.Values);
                var linkedReferences = VerifiedLinkedRuntimeReferences.Verify(Path.Combine(baselineRoot, baseline.playerInputSnapshot), frozenReceipt, set, policy);
                ShadowReflectionBindingEvidence.ValidateCompiled(set, policy, request.currentCompileSnapshot, receipt, false, linkedReferences).ThrowIfInvalid();
                var current = set.Assemblies.Values.ToArray();
                string bootstrapAbi = ShadowBaselineManifestBuilder.BootstrapHash(current);
                ShadowHash.Require(bootstrapAbi == baseline.bootstrapAbiHash, "BootstrapAbiChanged", "Fixed Bootstrap metadata/IL changed.");
                var roots = AssemblyReferenceGraph.DetectChangedRoots(baseline.assemblies, current, request.explicitChangedRoots);
                var graph = new AssemblyReferenceGraph(current, policy.dependencies, baseline.dependencyGraph);
                var closure = graph.ReverseClosure(roots);
                foreach (string assembly in closure)
                    ShadowHash.Require(baseline.shadowCandidates.Any(n => AssemblyIdentityUtil.CanonicalName(n) == AssemblyIdentityUtil.CanonicalName(assembly)), "BaselineCandidateMissing", assembly);
                var order = graph.LoadOrder(closure);
                if (diff.level == ResourceAbiDiffLevel.None && roots.Length > 0) diff.level = ResourceAbiDiffLevel.CodeOnly;
                affected = BundleImpactAnalyzer.GetAffectedBundles(diff, index);
                // Warmup never authorizes a patch. It is checked only after the
                // complete existing policy/resource proof, before any output.
                var verifiedWarmup = warmup == null ? null : ShadowWarmupValidator.ValidateAndClone(warmup, set, closure);
                // R01 capacity admission is a new capability gate. Keep it after
                // the existing resource and compiled-policy checks so legacy
                // rejection ordering remains observable, then fail closed for a
                // baseline that cannot prove the native budget capability.
                ShadowHash.Require(baseline.nativeBudgetCapabilityVersion == MetadataCapacityProfile2.BudgetCapabilityVersion &&
                    baseline.metadataEncodingProfile2 != null && baseline.metadataCapacityReport2 != null,
                    "CapabilityMissing", "The baseline has no explicit profile 2 metadata capacity contract; rebuild it with the installed sparse native codec.");
                var profile = baseline.metadataEncodingProfile2;
                ShadowHash.Require(profile.IsValid(), "CapabilityMissing", "Baseline profile 2 metadata encoding identity is invalid.");
                ShadowHash.Require(string.Equals(profile.nativeSourceRevision, baseline.sourcePins.hybridclr.revision, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(profile.nativeCodecHeaderSha256, baseline.metadataCapacityReport2.nativeCodecHeaderSha256, StringComparison.OrdinalIgnoreCase),
                    "CapabilityMissing", "Baseline profile 2 metadata capacity is not bound to its native source pin and codec hash.");
                ShadowHash.Require(baseline.metadataCapacityReport2.profileVersion == MetadataCapacityProfile2.ProfileVersion &&
                    baseline.metadataCapacityReport2.nativeBudgetCapabilityVersion == MetadataCapacityProfile2.BudgetCapabilityVersion &&
                    baseline.metadataCapacityReport2.fitsPreliminary && baseline.metadataCapacityReport2.runtimeFinalizationRequired &&
                    !baseline.metadataCapacityReport2.finalPageFitKnown &&
                    baseline.metadataCapacityReport2.nativeSourceRevision == profile.nativeSourceRevision,
                    "CapabilityMissing", "Baseline metadata capacity report does not advertise the complete profile 2 preliminary contract.");
                // The runtime performs the reservation before its first Stage
                // call. The Editor report admits ordered identities and DLL
                // bytes only; native page finalization remains runtime-owned.
                var closureInputs = MetadataCapacityPlanner.ClosureInputs(request.currentCompileSnapshot, receipt, order);
                var profileInputs = closureInputs.Select(input => new MetadataCapacityProfile2Input
                {
                    name = input.name,
                    dllSize = input.dllSize,
                    sha256 = input.sha256,
                }).ToArray();
                var capacity = MetadataCapacityPlannerProfile2.PreliminaryPlan(profileInputs,
                    baseline.metadataCapacityReport2.reservedImageCountAfter);
                capacity.nativeSourceRevision = profile.nativeSourceRevision;
                capacity.nativeCodecHeaderSha256 = profile.nativeCodecHeaderSha256;
                ShadowHash.Require(capacity.admissionAccepted,
                    "MetadataCapacityExceeded", "Profile 2 preliminary metadata admission failed before publication at " +
                    (capacity.firstFailingAssembly ?? "<unknown>") + " (index " + capacity.firstFailingIndex + ", reason " + capacity.failureReason + ").");
                string temporary = ShadowArtifactWriter.Begin(request.outputDirectory);
                var entries = closure.Select(name =>
                {
                    var descriptor = set.Get(name);
                    var input = receipt.assemblies.Single(f => AssemblyIdentityUtil.CanonicalName(f.name) == AssemblyIdentityUtil.CanonicalName(name));
                    var original = baseline.assemblies.Single(d => AssemblyIdentityUtil.CanonicalName(d.name) == AssemblyIdentityUtil.CanonicalName(name));
                    string relative = "DLLs/" + descriptor.name + ".dll";
                    ShadowArtifactWriter.CopyVerified(ShadowHash.SafeChild(request.currentCompileSnapshot, input.path), temporary, relative, input.sha256);
                    var capacityInput = closureInputs.Single(c => AssemblyIdentityUtil.CanonicalName(c.name) == AssemblyIdentityUtil.CanonicalName(name));
                    var entry = new ShadowPatchAssembly { name = descriptor.name, dll = relative, sha256 = input.sha256, dllSize = capacityInput.dllSize, semanticHash = descriptor.semanticHash,
                        mvid = descriptor.mvid, baselineMvid = original.mvid, references = descriptor.references };
                    if (request.includePdb && !string.IsNullOrEmpty(input.pdbPath))
                    {
                        entry.pdb = "DLLs/" + descriptor.name + ".pdb"; entry.pdbSha256 = input.pdbSha256;
                        ShadowArtifactWriter.CopyVerified(ShadowHash.SafeChild(request.currentCompileSnapshot, input.pdbPath), temporary, entry.pdb, entry.pdbSha256);
                    }
                    return entry;
                }).ToArray();
                var manifest = new ShadowPatchManifest
                {
                    patchId = request.patchId, baselineBuildId = baseline.baselineBuildId, baselineManifestSha256 = ShadowHash.File(request.baselineManifestPath),
                    unityVersion = baseline.unityVersion, target = baseline.target, architecture = baseline.architecture, sourcePins = receipt.sourcePins,
                    runtimeAbiHash = baseline.runtimeAbiHash, compileSnapshotHash = receipt.snapshotHash, bootstrapAbiHash = bootstrapAbi,
                    reflectionBindingConfigurationSha256 = policy.reflectionBindingConfigurationSha256,
                    reflectionBindingConfigurationHash = policy.reflectionBindingConfigurationHash,
                    reflectionBindings = policy.reflectionBindings,
                    baselineResourceAbiHash = baseline.resourceAbiHash, resourceAbiHash = ResourceAbiHasher.Compute(resourceAbi), resourceChangeLevel = diff.level.ToString(),
                    dllOnly = request.dllOnly, resourceBundlesRequired = affected, resourceChangeReasons = diff.reasons,
                    changedRoots = roots, loadOrder = order, closure = entries, dependencyGraph = graph.Edges, deferredFacadeReferences = set.DeferredFacadeReferences.ToArray(),
                    nativeBudgetCapabilityVersion = MetadataCapacityProfile2.BudgetCapabilityVersion,
                    metadataEncodingProfile2 = profile, metadataCapacityReport2 = capacity,
                };
                object wireManifest = SelectWireManifest(manifest, verifiedWarmup);
                ShadowArtifactWriter.Json(temporary, "patch-manifest.json", wireManifest);
                ShadowArtifactWriter.Json(temporary, "resource-abi.json", resourceAbi);
                ShadowArtifactWriter.Json(temporary, "resource-abi-diff.json", diff);
                ShadowArtifactWriter.Json(temporary, "compile-snapshot-receipt.json", receipt);
                ShadowReflectionBindingEvidence.Copy(request.currentCompileSnapshot, temporary, receipt);
                ShadowArtifactWriter.Finish(temporary, request.outputDirectory, "patch-manifest.json");
                return wireManifest;
            }
        }

        internal static object SelectWireManifest(ShadowPatchManifest manifest, ShadowWarmupPlan verifiedWarmup)
        {
            return verifiedWarmup == null ? (object)manifest : new ShadowPatchManifestV2 { patch = manifest, warmup = verifiedWarmup };
        }
    }
}
