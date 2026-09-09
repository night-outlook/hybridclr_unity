using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace HybridCLR.Editor.AssemblyShadow
{
    public static class ShadowBaselineManifestBuilder
    {
        public static ShadowBaselineManifest Build(ShadowBaselineBuildRequest request)
        {
            ShadowHash.Require(request != null && request.policy != null, "InvalidBaselineRequest", "Policy is required.");
            ShadowHash.Require(!string.IsNullOrWhiteSpace(request.resourceBaselinePath), "ResourceReceiptRequired", "Build and freeze baseline resources before associating them with a Player.");
            var receipt = AssemblySnapshot.ReadAndVerify(request.playerInputSnapshot, true);
            var policy = ShadowFilteredInputPolicy.Apply(request.policy, receipt);
            ShadowReflectionBindingEvidence.RequirePolicy(policy, request.playerInputSnapshot, receipt, true);
            ShadowSourcePins.RequireSameBuildSources(receipt.sourcePins, request.sourcePins);
            ShadowHash.Require(receipt.buildId == request.buildId && receipt.target == request.target.ToString() && receipt.architecture == request.architecture && receipt.unityVersion == Application.unityVersion,
                "BaselineIdentityMismatch", "Build ID/Unity/target/architecture must match the successful Player input snapshot.");
            // The capture is only useful if its corresponding native artifact still exists.
            ShadowHash.Require(File.Exists(receipt.nativeLibraryPath) && ShadowHash.File(receipt.nativeLibraryPath) == receipt.nativeLibrarySha256, "PlayerArtifactMismatch", receipt.nativeLibraryPath);
            var frozenResources = ShadowResourceBaseline.ReadAndVerify(request.resourceBaselinePath, request.target, request.architecture);
            using (var set = LoadSnapshot(request.playerInputSnapshot, policy, receipt))
            {
                ShadowReflectionBindingEvidence.AddCompiledDependencies(policy, set.Assemblies.Values);
                var linkedReferences = VerifiedLinkedRuntimeReferences.Verify(request.playerInputSnapshot, receipt, set, policy);
                ShadowReflectionBindingEvidence.ValidateCompiled(set, policy, request.playerInputSnapshot, receipt, true, linkedReferences).ThrowIfInvalid();
                var graph = new AssemblyReferenceGraph(set.Assemblies.Values, policy.dependencies);
                string[] candidates = set.Assemblies.Values.Where(a => a.isShadowCapable).Select(a => a.name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
                string[] bootstrap = set.Assemblies.Values.Where(a => a.isBootstrap).Select(a => a.name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
                ShadowHash.Require(candidates.Length > 0 && bootstrap.Length > 0, "BaselineCandidatesMissing", "Baseline needs candidates and a fixed Bootstrap.");
                foreach (var candidate in request.policy.assemblies.Where(a => a.isShadowCapable))
                    ShadowHash.Require(candidates.Any(n => AssemblyIdentityUtil.CanonicalName(n) == AssemblyIdentityUtil.CanonicalName(candidate.name)), "CandidateFilteredOut", candidate.name);
                var resourceAbi = UnitySerializedTypeAnalyzer.Analyze(set, candidates);
                ShadowResourceBaseline.RequirePlayerAbi(frozenResources, resourceAbi);
                var index = frozenResources.Index;
                var bundles = frozenResources.Receipt.bundles;
                if (request.resources != null)
                {
                    ToBuildMap(request.resources);
                    var expected = request.resources.bundles.OrderBy(b => b.name, StringComparer.Ordinal).Select(b => b.name + ":" + string.Join("|", ShadowHash.Sorted(b.assets)));
                    var captured = bundles.OrderBy(b => b.name, StringComparer.Ordinal).Select(b => b.name + ":" + string.Join("|", ShadowHash.Sorted(b.assets)));
                    ShadowHash.Require(expected.SequenceEqual(captured), "ResourceBuildMapMismatch", "Current resource map differs from the frozen resource build.");
                }
                // Capacity admission is derived from the verified Player snapshot
                // and the installed native helper. It is completed before the
                // immutable publication directory is created.
                var ordinaryInputs = MetadataCapacityPlanner.OrdinaryInputs(request.playerInputSnapshot, receipt);
                var profile = MetadataEncodingProfile.CreateCurrentV2(receipt.sourcePins);
                var profileInputs = ordinaryInputs.Select(input => new MetadataCapacityProfile2Input
                {
                    name = input.name,
                    dllSize = input.dllSize,
                    sha256 = input.sha256,
                }).ToArray();
                var capacity = MetadataCapacityPlannerProfile2.PreliminaryPlan(profileInputs, 0);
                capacity.nativeSourceRevision = profile.nativeSourceRevision;
                capacity.nativeCodecHeaderSha256 = profile.nativeCodecHeaderSha256;
                ShadowHash.Require(capacity.admissionAccepted,
                    "MetadataCapacityExceeded", "Profile 2 preliminary metadata admission failed before publication at " +
                    (capacity.firstFailingAssembly ?? "<unknown>") + " (index " + capacity.firstFailingIndex + ", reason " + capacity.failureReason + ").");
                string temporary = ShadowArtifactWriter.Begin(request.outputDirectory);
                var descriptors = set.Assemblies.Values.OrderBy(a => a.name, StringComparer.Ordinal).Select(a => CloneForManifest(a,
                    "PlayerInputs/" + AssemblySnapshot.AllFiles(receipt).Single(f => AssemblyIdentityUtil.CanonicalName(f.name) == AssemblyIdentityUtil.CanonicalName(a.name)).path)).ToArray();
                var manifest = new ShadowBaselineManifest
                {
                    baselineBuildId = request.buildId, unityVersion = receipt.unityVersion, target = receipt.target, architecture = receipt.architecture,
                    sourcePins = receipt.sourcePins, runtimeAbiHash = receipt.sourcePins.RuntimeAbiHash(), shadowCandidates = candidates, bootstrapAssemblies = bootstrap,
                    bootstrapAbiHash = BootstrapHash(descriptors), resourceAbiHash = ResourceAbiHasher.Compute(resourceAbi),
                    resourceIndexHash = ShadowHash.Text(JsonUtility.ToJson(index, true)), policyHash = ShadowHash.Text(JsonUtility.ToJson(policy, true)),
                    reflectionBindingConfigurationSha256 = policy.reflectionBindingConfigurationSha256,
                    reflectionBindingConfigurationHash = policy.reflectionBindingConfigurationHash,
                    reflectionBindings = policy.reflectionBindings,
                    resourceBuildReceiptHash = ShadowHash.File(Path.Combine(frozenResources.Root, ShadowResourceBaseline.ReceiptName)),
                    playerInputSnapshotHash = receipt.snapshotHash, playerBuildGuid = receipt.buildGuid, nativeLibrarySha256 = receipt.nativeLibrarySha256,
                    nativeBudgetCapabilityVersion = MetadataCapacityProfile2.BudgetCapabilityVersion,
                    metadataEncodingProfile2 = profile, metadataCapacityReport2 = capacity,
                    assemblies = descriptors, dependencyGraph = graph.Edges, deferredFacadeReferences = set.DeferredFacadeReferences.ToArray(), bundles = bundles,
                };
                foreach (var descriptor in descriptors) ShadowArtifactWriter.Json(temporary, "assemblies/" + descriptor.name + ".json", descriptor);
                ShadowArtifactWriter.Json(temporary, "resource-abi.json", resourceAbi);
                ShadowArtifactWriter.Json(temporary, "resource-script-index.json", index);
                ShadowArtifactWriter.Json(temporary, "source-pins.json", receipt.sourcePins);
                ShadowArtifactWriter.Json(temporary, "policy.json", policy);
                ShadowArtifactWriter.Json(temporary, "baseline-manifest.json", manifest);
                CopySnapshot(request.playerInputSnapshot, temporary, receipt);
                CopyResourceEvidence(frozenResources.Root, temporary);
                ShadowArtifactWriter.Finish(temporary, request.outputDirectory, "baseline-manifest.json");
                return manifest;
            }
        }

        internal static AssetBundleBuild[] ToBuildMap(ShadowResourceBuildMap map)
        {
            ShadowHash.Require(map.schemaVersion == 1 && map.bundles != null && map.bundles.Length > 0, "ResourceMapSchema", "A versioned nonempty resource build map is required.");
            ShadowHash.Require(map.bundles.Select(b => b.name).Distinct(StringComparer.Ordinal).Count() == map.bundles.Length, "DuplicateBundle", "Bundle names must be unique.");
            return map.bundles.Select(b => new AssetBundleBuild { assetBundleName = b.name, assetNames = b.assets }).ToArray();
        }

        internal static CompiledAssemblySet LoadSnapshot(string root, ShadowPolicyConfiguration policy, AssemblySnapshotReceipt verifiedReceipt)
        {
            var framework = TargetFrameworkReferenceVerifier.Verify(root, verifiedReceipt);
            return DnlibAssemblyLoader.Load(Path.Combine(root, "Assemblies"), new[] { Path.Combine(root, "References") }, policy.assemblies,
                targetFrameworkReferences: framework);
        }

        internal static string BootstrapHash(IEnumerable<AssemblyDescriptor> descriptors)
        {
            return ShadowHash.Text("bootstrap-abi:1\n" + string.Join("\n", descriptors.Where(d => d.isBootstrap).OrderBy(d => d.name, StringComparer.Ordinal)
                .Select(d => AssemblyIdentityUtil.CanonicalName(d.name) + ":" + d.semanticHash).ToArray()));
        }

        private static AssemblyDescriptor CloneForManifest(AssemblyDescriptor descriptor, string path)
        {
            var clone = JsonUtility.FromJson<AssemblyDescriptor>(JsonUtility.ToJson(descriptor)); clone.filePath = path; return clone;
        }

        private static void CopySnapshot(string source, string destination, AssemblySnapshotReceipt receipt)
        {
            foreach (var file in AssemblySnapshot.AllFiles(receipt))
            {
                ShadowArtifactWriter.CopyVerified(ShadowHash.SafeChild(source, file.path), destination, "PlayerInputs/" + file.path, file.sha256);
                if (!string.IsNullOrEmpty(file.pdbPath))
                    ShadowArtifactWriter.CopyVerified(ShadowHash.SafeChild(source, file.pdbPath), destination, "PlayerInputs/" + file.pdbPath, file.pdbSha256);
            }
            ShadowArtifactWriter.Json(destination, "PlayerInputs/" + AssemblySnapshot.ReceiptName, receipt);
            ShadowLinkedPlayerEvidence.Copy(source, Path.Combine(destination, "PlayerInputs"), receipt);
            ShadowReflectionBindingEvidence.Copy(source, Path.Combine(destination, "PlayerInputs"), receipt);
        }

        private static void CopyResourceEvidence(string source, string destination)
        {
            foreach (string path in Directory.GetFiles(source, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
                ShadowArtifactWriter.CopyVerified(path, destination, "ResourceInputs/" + Path.GetRelativePath(source, path).Replace('\\', '/'), ShadowHash.File(path));
        }
    }
}
