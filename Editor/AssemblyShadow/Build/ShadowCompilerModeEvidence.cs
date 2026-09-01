using System;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using UnityEditor.Build.Player;

namespace HybridCLR.Editor.AssemblyShadow
{
    [Serializable] public sealed class ShadowCompilerModeReceipt
    {
        public int schemaVersion = 1;
        public string kind = "CompilePlayerScriptsMode";
        public bool developmentBuild;
        public int compilerOptions;
        public string unityVersion, target, architecture, snapshotHash, snapshotReceiptSha256;
        public string[] extraScriptingDefines;
    }

    /// <summary>Opt-in compiler invocation provenance. The sealed schema-1 snapshot is unchanged.</summary>
    public static class ShadowCompilerModeEvidence
    {
        public const string ReceiptName = "compiler-mode.json";
        internal static void Capture(string root, AssemblySnapshotReceipt snapshot, ScriptCompilationOptions options, bool development)
        {
            Require(snapshot != null && snapshot.kind == "CompilePlayerScripts", "Compile-only snapshot required.");
            Require(options == (development ? ScriptCompilationOptions.DevelopmentBuild : ScriptCompilationOptions.None), "Unexpected actual compiler options.");
            var receipt = new ShadowCompilerModeReceipt { developmentBuild = development, compilerOptions = (int)options,
                unityVersion = snapshot.unityVersion, target = snapshot.target, architecture = snapshot.architecture, snapshotHash = snapshot.snapshotHash,
                snapshotReceiptSha256 = ShadowHash.File(Path.Combine(root, AssemblySnapshot.ReceiptName)), extraScriptingDefines = snapshot.extraScriptingDefines.ToArray() };
            byte[] bytes = Bytes(receipt);
            using (var file = new FileStream(Path.Combine(root, ReceiptName), FileMode.CreateNew, FileAccess.Write, FileShare.None)) file.Write(bytes, 0, bytes.Length);
        }
        public static ShadowCompilerModeReceipt ReadAndVerify(string snapshotRoot, bool expectedDevelopmentBuild)
        {
            var snapshot = AssemblySnapshot.ReadAndVerify(snapshotRoot, false);
            return Verify(snapshotRoot, snapshot, expectedDevelopmentBuild);
        }
        internal static ShadowCompilerModeReceipt ReadAndVerify(string snapshotRoot, AssemblySnapshotReceipt snapshot)
        {
            var receipt = Read(snapshotRoot);
            return Verify(snapshotRoot, snapshot, receipt, receipt.developmentBuild);
        }
        internal static ShadowCompilerModeReceipt Verify(string snapshotRoot, AssemblySnapshotReceipt snapshot, bool expectedDevelopmentBuild)
        { return Verify(snapshotRoot, snapshot, Read(snapshotRoot), expectedDevelopmentBuild); }
        private static ShadowCompilerModeReceipt Read(string snapshotRoot)
        {
            string path = Path.Combine(snapshotRoot, ReceiptName); Require(File.Exists(path), "Explicit compiler-mode provenance is missing.");
            byte[] bytes = File.ReadAllBytes(path); Require(bytes.Length > 0 && bytes.Length <= 1024 * 1024, "Compiler-mode receipt size is invalid.");
            ShadowCompilerModeReceipt receipt;
            try { using (var stream = new MemoryStream(bytes)) receipt = (ShadowCompilerModeReceipt)new DataContractJsonSerializer(typeof(ShadowCompilerModeReceipt)).ReadObject(stream); }
            catch (Exception error) { throw new ShadowBuildException("CompilerModeEvidence", "Malformed compiler-mode receipt: " + error.GetType().Name); }
            Require(receipt != null && Bytes(receipt).SequenceEqual(bytes), "Noncanonical/missing/unknown compiler-mode fields.");
            return receipt;
        }
        private static ShadowCompilerModeReceipt Verify(string snapshotRoot, AssemblySnapshotReceipt snapshot,
            ShadowCompilerModeReceipt receipt, bool expectedDevelopmentBuild)
        {
            Require(receipt.schemaVersion == 1 && receipt.kind == "CompilePlayerScriptsMode" && receipt.developmentBuild == expectedDevelopmentBuild &&
                receipt.compilerOptions == (int)(expectedDevelopmentBuild ? ScriptCompilationOptions.DevelopmentBuild : ScriptCompilationOptions.None), "Compiler mode differs.");
            Require(snapshot != null && snapshot.kind == "CompilePlayerScripts" && receipt.snapshotHash == snapshot.snapshotHash &&
                receipt.snapshotReceiptSha256 == ShadowHash.File(Path.Combine(snapshotRoot, AssemblySnapshot.ReceiptName)) &&
                receipt.unityVersion == snapshot.unityVersion && receipt.target == snapshot.target && receipt.architecture == snapshot.architecture &&
                receipt.extraScriptingDefines != null && receipt.extraScriptingDefines.SequenceEqual(snapshot.extraScriptingDefines), "Compiler mode is not bound to this exact snapshot.");
            return receipt;
        }
        private static byte[] Bytes(ShadowCompilerModeReceipt value)
        { using (var stream = new MemoryStream()) { new DataContractJsonSerializer(typeof(ShadowCompilerModeReceipt)).WriteObject(stream, value); return stream.ToArray(); } }
        private static void Require(bool value, string message) { ShadowHash.Require(value, "CompilerModeEvidence", message); }
    }
}
