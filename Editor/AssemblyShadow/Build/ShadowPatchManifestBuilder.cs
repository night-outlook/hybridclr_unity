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
            var policy = ShadowFilteredInputPolicy.Apply(request.policy, frozenReceipt);
            using (var set = ShadowBaselineManifestBuilder.LoadSnapshot(request.currentCompileSnapshot, policy))
            {
                ShadowAssemblyPolicyValidator.ValidateCompiled(set, policy, DateTime.UtcNow).ThrowIfInvalid();
                var current = set.Assemblies.Values.ToArray();
                string bootstrapAbi = ShadowBaselineManifestBuilder.BootstrapHash(current);
                ShadowHash.Require(bootstrapAbi == baseline.bootstrapAbiHash, "BootstrapAbiChanged", "Fixed Bootstrap metadata/IL changed.");
                var roots = AssemblyReferenceGraph.DetectChangedRoots(baseline.assemblies, current, request.explicitChangedRoots);
                var graph = new AssemblyReferenceGraph(current, policy.dependencies, baseline.dependencyGraph);
                var closure = graph.ReverseClosure(roots);
                foreach (string assembly in closure)
                    ShadowHash.Require(baseline.shadowCandidates.Any(n => AssemblyIdentityUtil.CanonicalName(n) == AssemblyIdentityUtil.CanonicalName(assembly)), "BaselineCandidateMissing", assembly);
                var order = graph.LoadOrder(closure);
                var resourceAbi = UnitySerializedTypeAnalyzer.Analyze(set, baseline.shadowCandidates);
                var diff = ResourceAbiDiff.Compare(baselineAbi, resourceAbi);
                if (diff.level == ResourceAbiDiffLevel.None && roots.Length > 0) diff.level = ResourceAbiDiffLevel.CodeOnly;
                string[] affected = BundleImpactAnalyzer.GetAffectedBundles(diff, index);
                // A settings toggle must never silently authorize unsafe DLL-only output.
                // Analysis-only/rebuild manifests may still be produced for review.
                ShadowHash.Require(!request.dllOnly || (!diff.RequiresResourceRebuild && !index.hasUnknown), "ResourceRebuildRequired",
                    "DLL-only rejected. Affected bundles: " + string.Join(", ", affected) + ". " + string.Join("; ", diff.reasons));
                string temporary = ShadowArtifactWriter.Begin(request.outputDirectory);
                var entries = closure.Select(name =>
                {
                    var descriptor = set.Get(name);
                    var input = receipt.assemblies.Single(f => AssemblyIdentityUtil.CanonicalName(f.name) == AssemblyIdentityUtil.CanonicalName(name));
                    var original = baseline.assemblies.Single(d => AssemblyIdentityUtil.CanonicalName(d.name) == AssemblyIdentityUtil.CanonicalName(name));
                    string relative = "DLLs/" + descriptor.name + ".dll";
                    ShadowArtifactWriter.CopyVerified(ShadowHash.SafeChild(request.currentCompileSnapshot, input.path), temporary, relative, input.sha256);
                    var entry = new ShadowPatchAssembly { name = descriptor.name, dll = relative, sha256 = input.sha256, semanticHash = descriptor.semanticHash,
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
                    baselineResourceAbiHash = baseline.resourceAbiHash, resourceAbiHash = ResourceAbiHasher.Compute(resourceAbi), resourceChangeLevel = diff.level.ToString(),
                    dllOnly = request.dllOnly, resourceBundlesRequired = affected, resourceChangeReasons = diff.reasons,
                    changedRoots = roots, loadOrder = order, closure = entries, dependencyGraph = graph.Edges, deferredFacadeReferences = set.DeferredFacadeReferences.ToArray(),
                };
                ShadowArtifactWriter.Json(temporary, "patch-manifest.json", manifest);
                ShadowArtifactWriter.Json(temporary, "resource-abi.json", resourceAbi);
                ShadowArtifactWriter.Json(temporary, "resource-abi-diff.json", diff);
                ShadowArtifactWriter.Json(temporary, "compile-snapshot-receipt.json", receipt);
                ShadowArtifactWriter.Finish(temporary, request.outputDirectory, "patch-manifest.json");
                return manifest;
            }
        }
    }
}
