using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HybridCLR.AssemblyShadow.CodeGen;
using dnlib.DotNet;
using UnityEngine;

namespace HybridCLR.Editor.AssemblyShadow
{
    [Serializable]
    public sealed class NativeLayoutIdentityEvidence
    {
        public string profile = NativeLayoutIdentityContext.Profile;
        public string targetFrameworkProvenanceHash, linkedRetargetingEvidenceHash;
        public string runtimeFacadeSha256, compilerFacadeSha256;
        public NativeLayoutIdentityFile[] linkedInventory, compilerInventory;
        public NativeLayoutResolvedType[] linkedResolutions, compilerResolutions;
    }

    [Serializable]
    public sealed class NativeLayoutAdmissionSnapshotReport
    {
        public int schemaVersion = 2;
        public string profile = "NativeLayoutAdmissionV1";
        public string inputBasis = "LinkedPlayer";
        public string baselineSnapshotHash;
        public string linkedPlayerReceiptHash;
        public string baselineNativeLibrarySha256;
        public string targetSnapshotHash;
        public string[] targetLoadOrder;
        public NativeLayoutAdmissionReport[] assemblies;
        public NativeLayoutIdentityEvidence identityEvidence;
        public bool nativeProofExecuted;
        public bool runtimeMustRevalidate = true;
        public bool pureInterpreterExpansionEnabled;
    }

    /// <summary>
    /// Pre-strip inputs determine semantic roots. Verified linked DLLs describe
    /// the baseline native Player. Nominal identity may be compared across a
    /// captured, verified forwarding boundary; physical layout remains native-owned.
    /// </summary>
    public static class NativeLayoutAdmissionSnapshot
    {
        public const string ReportName = "native-layout-admission-v1.json";

        public static NativeLayoutAdmissionSnapshotReport AnalyzeAndRequire(string baselineSnapshot,
            AssemblySnapshotReceipt baseline, string targetSnapshot, AssemblySnapshotReceipt target,
            string[] loadOrder)
        {
            ShadowHash.Require(baseline != null && target != null && loadOrder != null,
                "NativeLayoutInput", "Two verified snapshots and a target load order are required.");
            // Do not accept a caller-supplied inventory merely because a prior
            // stage authenticated another object. Both complete receipts are re-read.
            var verifiedBaseline = AssemblySnapshot.ReadAndVerify(baselineSnapshot, true);
            var verifiedTarget = AssemblySnapshot.ReadAndVerify(targetSnapshot, false);
            ShadowHash.Require(baseline.snapshotHash == AssemblySnapshot.ComputeHash(baseline) &&
                target.snapshotHash == AssemblySnapshot.ComputeHash(target) &&
                baseline.snapshotHash == verifiedBaseline.snapshotHash && target.snapshotHash == verifiedTarget.snapshotHash &&
                baseline.unityVersion == target.unityVersion && baseline.target == target.target && baseline.architecture == target.architecture,
                "NativeLayoutInput", "Reverified source domains, Unity target and architecture must match.");
            baseline = verifiedBaseline; target = verifiedTarget;
            var targetFramework = TargetFrameworkReferenceVerifier.Verify(targetSnapshot, target);
            string[] names = loadOrder.Select(AssemblyIdentityUtil.CanonicalName).ToArray();
            ShadowHash.Require(names.Distinct(StringComparer.Ordinal).Count() == names.Length,
                "NativeLayoutInput", "Duplicate target load identity.");
            var linkedFiles = baseline.linkedPlayerReceipt.assemblies.Select(f => new SnapshotFile
            { name = f.name, path = ShadowLinkedPlayerEvidence.DirectoryName + "/" + f.path, sha256 = f.sha256 }).ToArray();
            var before = Capture(baselineSnapshot, linkedFiles);
            var after = Capture(targetSnapshot, target.assemblies.Concat(target.references));
            byte[] runtimeFacade = null; string facadeHash = null, compilerHash = null;
            if (!string.IsNullOrEmpty(baseline.linkedPlayerReceipt.reflectionBindingEvidenceHash))
            {
                // ReadAndVerify above has independently rederived this entire
                // captured linked proof, including its facade and destinations.
                string path = Regular(baselineSnapshot, ShadowReflectionBindingLinkedEvidence.ReceiptPath);
                ShadowHash.Require(ShadowHash.File(path) == baseline.linkedPlayerReceipt.reflectionBindingEvidenceHash,
                    "NativeLayoutResolutionProofChanged", path);
                var proof = JsonUtility.FromJson<ReflectionBindingLinkedReceipt>(File.ReadAllText(path));
                ShadowHash.Require(proof != null && proof.mappingPolicyVersion == CapturedReflectionRetargetingProfile.PolicyVersion &&
                    proof.facadePath == ShadowReflectionBindingLinkedEvidence.FacadePath && proof.unityVersion == target.unityVersion &&
                    proof.target == target.target && proof.architecture == target.architecture && proof.buildGuid == baseline.buildGuid,
                    "NativeLayoutResolutionProofChanged", "Linked retargeting proof has a different source/target binding.");
                runtimeFacade = File.ReadAllBytes(Regular(baselineSnapshot, proof.facadePath)); facadeHash = proof.facadeSha256;
                ShadowHash.Require(ShadowHash.Bytes(runtimeFacade) == facadeHash, "NativeLayoutResolutionFacadeHash", proof.facadePath);
                var references = target.references.Where(f => AssemblyIdentityUtil.CanonicalName(f.name) == "netstandard").ToArray();
                ShadowHash.Require(references.Length == 1, "NativeLayoutResolutionFrameworkMissing", "The compiler facade must be a captured reference, never a primary assembly.");
                byte[] compilerFacade = after[AssemblyIdentityUtil.CanonicalName(references[0].name)]; compilerHash = references[0].sha256;
                using (var module = ModuleDefMD.Load(compilerFacade, new ModuleCreationOptions { TryToLoadPdbFromDisk = false }))
                    ShadowHash.Require(module.Assembly != null && module.Assembly.FullName == NativeLayoutIdentityContext.NetstandardIdentity &&
                        targetFramework.Contains(module.Assembly, compilerHash), "NativeLayoutResolutionFrameworkMismatch",
                        "The actual captured compiler facade must match Unity's verified target framework bytes.");
            }
            using (var linked = NativeLayoutIdentityContext.Load(before.Values))
            using (var compiler = runtimeFacade == null ? NativeLayoutIdentityContext.Load(after.Values) :
                NativeLayoutIdentityContext.LoadCompiler(after.Values, linked, runtimeFacade, facadeHash, compilerHash))
            {
                var reports = names.Select(name =>
                {
                    ShadowHash.Require(before.ContainsKey(name) && target.assemblies.Count(f => AssemblyIdentityUtil.CanonicalName(f.name) == name) == 1,
                        "NativeLayoutInput", "Exactly one linked baseline and target assembly are required: " + name);
                    return NativeLayoutAdmissionValidator.Analyze(before[name], after[name], linked, compiler);
                }).ToArray();
                foreach (var report in reports) report.RequireEditorAdmission();
                // Before returning a publishable report, catch concurrent input
                // replacement. The resolver itself used only privately owned bytes.
                Recheck(baselineSnapshot, linkedFiles);
                Recheck(targetSnapshot, target.assemblies.Concat(target.references));
                if (runtimeFacade != null)
                {
                    ShadowHash.Require(ShadowHash.File(Regular(baselineSnapshot, ShadowReflectionBindingLinkedEvidence.ReceiptPath)) ==
                        baseline.linkedPlayerReceipt.reflectionBindingEvidenceHash, "NativeLayoutResolutionProofChanged", "Captured proof changed during comparison.");
                    ShadowHash.Require(ShadowHash.File(Regular(baselineSnapshot, ShadowReflectionBindingLinkedEvidence.FacadePath)) == facadeHash,
                        "NativeLayoutResolutionFacadeHash", "Captured facade changed during comparison.");
                }
                return new NativeLayoutAdmissionSnapshotReport
                {
                    baselineSnapshotHash = baseline.snapshotHash, linkedPlayerReceiptHash = baseline.linkedPlayerReceiptHash,
                    baselineNativeLibrarySha256 = baseline.nativeLibrarySha256, targetSnapshotHash = target.snapshotHash,
                    targetLoadOrder = (string[])loadOrder.Clone(), assemblies = reports,
                    identityEvidence = new NativeLayoutIdentityEvidence
                    {
                        targetFrameworkProvenanceHash = targetFramework.ProvenanceHash,
                        linkedRetargetingEvidenceHash = baseline.linkedPlayerReceipt.reflectionBindingEvidenceHash,
                        runtimeFacadeSha256 = facadeHash, compilerFacadeSha256 = compilerHash,
                        linkedInventory = linked.Files, compilerInventory = compiler.Files,
                        linkedResolutions = linked.Resolutions, compilerResolutions = compiler.Resolutions,
                    },
                    nativeProofExecuted = false, pureInterpreterExpansionEnabled = false,
                };
            }
        }

        private static Dictionary<string, byte[]> Capture(string root, IEnumerable<SnapshotFile> files)
        {
            var result = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var file in files)
            {
                string name = AssemblyIdentityUtil.CanonicalName(file.name);
                ShadowHash.Require(!result.ContainsKey(name), "NativeLayoutResolutionAmbiguous", name);
                string path = Regular(root, file.path);
                byte[] bytes = File.ReadAllBytes(path);
                ShadowHash.Require(ShadowHash.Bytes(bytes) == file.sha256, "InputChangedDuringBuild", path);
                result.Add(name, bytes);
            }
            return result;
        }
        private static void Recheck(string root, IEnumerable<SnapshotFile> files)
        {
            foreach (var file in files)
                ShadowHash.Require(ShadowHash.File(Regular(root, file.path)) == file.sha256, "InputChangedDuringBuild", file.path);
        }
        private static string Regular(string root, string relative)
        {
            string path = ShadowHash.SafeChild(root, relative);
            for (string at = Path.GetFullPath(path); at != null; at = Path.GetDirectoryName(at))
                ShadowHash.Require((File.GetAttributes(at) & System.IO.FileAttributes.ReparsePoint) == 0, "NativeLayoutResolutionPath", "No linked input paths: " + at);
            return path;
        }
    }
}
