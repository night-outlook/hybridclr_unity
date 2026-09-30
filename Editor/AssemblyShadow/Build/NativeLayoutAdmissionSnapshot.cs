using System;
using System.IO;
using System.Linq;

namespace HybridCLR.Editor.AssemblyShadow
{
    [Serializable]
    public sealed class NativeLayoutAdmissionSnapshotReport
    {
        public int schemaVersion = 1;
        public string profile = "NativeLayoutAdmissionV1";
        public string inputBasis = "LinkedPlayer";
        public string baselineSnapshotHash;
        public string linkedPlayerReceiptHash;
        public string baselineNativeLibrarySha256;
        public string targetSnapshotHash;
        public string[] targetLoadOrder;
        public NativeLayoutAdmissionReport[] assemblies;
        public bool nativeProofExecuted;
        public bool runtimeMustRevalidate = true;
        public bool pureInterpreterExpansionEnabled;
    }

    /// <summary>
    /// Pre-strip inputs determine semantic roots. Only the verified linked DLLs
    /// describe what was compiled into the baseline native Player. This report
    /// records an Editor screen, never a native allocation certificate.
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
            // This independently authenticates the actual linked membership,
            // byte hashes, MVIDs and corresponding native build receipt.
            ShadowLinkedPlayerEvidence.ReadAndVerify(baselineSnapshot, baseline);
            ShadowHash.Require(target.kind == "CompilePlayerScripts" && target.assemblies != null,
                "NativeLayoutInput", "Target bytes must come from one compiler snapshot.");
            string[] names = loadOrder.Select(AssemblyIdentityUtil.CanonicalName).ToArray();
            ShadowHash.Require(names.Distinct(StringComparer.Ordinal).Count() == names.Length,
                "NativeLayoutInput", "Duplicate target load identity.");
            string linkedRoot = Path.Combine(baselineSnapshot, ShadowLinkedPlayerEvidence.DirectoryName);
            var reports = names.Select(name =>
            {
                var linked = baseline.linkedPlayerReceipt.assemblies.Where(f => f.name == name).ToArray();
                var current = target.assemblies.Where(f => AssemblyIdentityUtil.CanonicalName(f.name) == name).ToArray();
                ShadowHash.Require(linked.Length == 1 && current.Length == 1,
                    "NativeLayoutInput", "Exactly one linked baseline and target DLL are required: " + name);
                byte[] before = File.ReadAllBytes(ShadowHash.SafeChild(linkedRoot, linked[0].path));
                byte[] after = File.ReadAllBytes(ShadowHash.SafeChild(targetSnapshot, current[0].path));
                ShadowHash.Require(ShadowHash.Bytes(before) == linked[0].sha256 && ShadowHash.Bytes(after) == current[0].sha256,
                    "InputChangedDuringBuild", "Layout inputs changed after snapshot verification: " + name);
                return NativeLayoutAdmissionValidator.Analyze(before, after);
            }).ToArray();
            // Analyze the complete set before selecting a deterministic failure.
            // Nothing is published and no output directory is created here.
            foreach (var report in reports) report.RequireEditorAdmission();
            return new NativeLayoutAdmissionSnapshotReport
            {
                baselineSnapshotHash = baseline.snapshotHash,
                linkedPlayerReceiptHash = baseline.linkedPlayerReceiptHash,
                baselineNativeLibrarySha256 = baseline.nativeLibrarySha256,
                targetSnapshotHash = target.snapshotHash,
                targetLoadOrder = (string[])loadOrder.Clone(),
                assemblies = reports,
                nativeProofExecuted = false,
                pureInterpreterExpansionEnabled = false,
            };
        }
    }
}
