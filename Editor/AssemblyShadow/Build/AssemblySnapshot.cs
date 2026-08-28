using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using dnlib.DotNet;
using UnityEditor;
using UnityEditor.Build.Player;
using UnityEditor.Compilation;
using UnityEngine;

namespace HybridCLR.Editor.AssemblyShadow
{
    [Serializable]
    public sealed class SnapshotFile
    {
        public string name;
        public string path;
        public string sha256;
        public string pdbPath;
        public string pdbSha256;
        public string sourcePath;
    }

    [Serializable]
    public sealed class AssemblySnapshotReceipt
    {
        public int schemaVersion = 1;
        public string kind;
        public string snapshotHash;
        public string unityVersion;
        public string target;
        public string architecture;
        public string buildId;
        public string buildGuid;
        public string playerOutput;
        public string nativeLibraryPath;
        public string nativeLibrarySha256;
        public bool playerBuildSucceeded;
        public bool playerBuildFilterCaptured;
        public int playerBuildOptions;
        public string[] normalHotUpdateAssemblies = new string[0];
        public string[] extraScriptingDefines = new string[0];
        public ShadowSourcePins sourcePins;
        public SnapshotFile[] assemblies;
        public SnapshotFile[] references;
        public SnapshotFile[] filteredAssemblies = new SnapshotFile[0];
        public AssemblyCapability[] filteredAssemblyCapabilities = new AssemblyCapability[0];
        public LinkedPlayerReceipt linkedPlayerReceipt;
        public string linkedPlayerReceiptHash;
        public string[] linkerExcludedAssemblies = new string[0];
        public AssemblyCapability[] linkerExcludedAssemblyCapabilities = new AssemblyCapability[0];
    }

    public static class AssemblySnapshot
    {
        public const string ReceiptName = "assembly-snapshot.json";

        public static AssemblySnapshotReceipt ReadAndVerify(string root, bool requirePlayer)
        {
            string receiptPath = Path.Combine(root, ReceiptName);
            ShadowHash.Require(File.Exists(receiptPath), "SnapshotReceiptMissing", receiptPath);
            var result = JsonUtility.FromJson<AssemblySnapshotReceipt>(File.ReadAllText(receiptPath));
            ShadowHash.Require(result != null && result.schemaVersion == 1 && result.sourcePins != null, "SnapshotSchema", receiptPath);
            result.references = result.references ?? new SnapshotFile[0];
            result.filteredAssemblies = result.filteredAssemblies ?? new SnapshotFile[0];
            result.normalHotUpdateAssemblies = result.normalHotUpdateAssemblies ?? new string[0];
            result.filteredAssemblyCapabilities = result.filteredAssemblyCapabilities ?? new AssemblyCapability[0];
            ShadowHash.Require(!requirePlayer || (result.kind == "PlayerBuildInputs" && result.playerBuildSucceeded && result.playerBuildFilterCaptured && !string.IsNullOrWhiteSpace(result.buildGuid) &&
                !string.IsNullOrWhiteSpace(result.nativeLibrarySha256)), "NotPlayerSnapshot", "Baseline requires a successful Player build's captured IFilterBuildAssemblies inputs.");
            ShadowHash.Require(requirePlayer || result.kind == "CompilePlayerScripts", "NotCompileSnapshot", "Patch must use one captured CompilePlayerScripts output.");
            if (requirePlayer) ShadowLinkedPlayerEvidence.ReadAndVerify(root, result);
            else
            {
                ShadowHash.Require(ShadowLinkedPlayerEvidence.IsAbsent(result) && !Directory.Exists(Path.Combine(root, ShadowLinkedPlayerEvidence.DirectoryName)),
                    "UnexpectedLinkedEvidence", "Compiler-only snapshots cannot claim successful linked Player evidence.");
                // Unity inline serialization may materialize a null Serializable
                // class as an empty object. Normalize only proven claimless data.
                result.linkedPlayerReceipt = null;
            }
            ValidateSection(result.assemblies, "Assemblies");
            ValidateSection(result.filteredAssemblies, "Assemblies/Filtered");
            ValidateSection(result.references, "References");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in AllFiles(result))
            {
                string key = AssemblyIdentityUtil.CanonicalName(file.name);
                ShadowHash.Require(seen.Add(key), "DuplicateAssembly", file.name);
                ShadowHash.Require(ShadowHash.File(ShadowHash.SafeChild(root, file.path)) == file.sha256, "SnapshotHashMismatch", file.path);
                using (var module = ModuleDefMD.Load(ShadowHash.SafeChild(root, file.path)))
                    ShadowHash.Require(module.Assembly != null && AssemblyIdentityUtil.CanonicalName(module.Assembly.Name.String) == key,
                        "SnapshotIdentityMismatch", file.path);
                if (!string.IsNullOrEmpty(file.pdbPath))
                    ShadowHash.Require(ShadowHash.File(ShadowHash.SafeChild(root, file.pdbPath)) == file.pdbSha256, "SnapshotPdbMismatch", file.pdbPath);
            }
            ShadowHash.Require(result.assemblies != null && result.assemblies.Length > 0 && ComputeHash(result) == result.snapshotHash, "SnapshotSetMismatch", receiptPath);
            // Reject injected DLLs, not just modified files mentioned by the receipt.
            var expected = new HashSet<string>(AllFiles(result).Select(f => Path.GetFullPath(ShadowHash.SafeChild(root, f.path))), StringComparer.Ordinal);
            if (requirePlayer)
                expected.UnionWith(result.linkedPlayerReceipt.assemblies.Select(f => Path.GetFullPath(ShadowHash.SafeChild(root, ShadowLinkedPlayerEvidence.DirectoryName + "/" + f.path))));
            var actual = Directory.GetFiles(root, "*.dll", SearchOption.AllDirectories).Select(Path.GetFullPath);
            ShadowHash.Require(expected.SetEquals(actual), "SnapshotSetMismatch", "Snapshot contains undeclared or missing DLLs.");
            ShadowReflectionBindingEvidence.ReadAndVerify(root, result, requirePlayer);
            ShadowRawTypeAdmissionEvidence.ReadAndVerify(root, result, requirePlayer);
            return result;
        }

        public static string ComputeHash(AssemblySnapshotReceipt receipt)
        {
            // Compiler snapshots remain deterministic. Successful Player snapshots
            // additionally bind their exact build and linker provenance hash.
            var text = new StringBuilder("assembly-shadow-snapshot:1\n");
            text.Append(receipt.kind).Append('\n').Append(receipt.unityVersion).Append('\n').Append(receipt.target).Append('\n').Append(receipt.architecture).Append('\n');
            text.Append(receipt.sourcePins.RuntimeAbiHash()).Append('\n');
            text.Append(receipt.playerBuildFilterCaptured ? "filtered-proof" : "compiler-output").Append('\n').Append(receipt.playerBuildOptions).Append('\n');
            foreach (string name in ShadowHash.Sorted(receipt.normalHotUpdateAssemblies ?? new string[0])) text.Append("normal:").Append(name).Append('\n');
            foreach (var role in (receipt.filteredAssemblyCapabilities ?? new AssemblyCapability[0]).OrderBy(c => c.name, StringComparer.Ordinal))
                text.Append("filtered-role:").Append(AssemblyIdentityUtil.CanonicalName(role.name)).Append(':').Append((int)role.classification).Append(':')
                    .Append(role.isPrecompiled).Append(':').Append(role.isShadowCapable).Append(':').Append(role.isBootstrap).Append(':').Append(role.capabilityDeclared).Append('\n');
            text.Append("linked-player:").Append(receipt.linkedPlayerReceiptHash).Append('\n');
            foreach (string name in ShadowHash.Sorted(receipt.linkerExcludedAssemblies ?? new string[0])) text.Append("linker-excluded:").Append(name).Append('\n');
            foreach (var role in (receipt.linkerExcludedAssemblyCapabilities ?? new AssemblyCapability[0]).OrderBy(c => c.name, StringComparer.Ordinal))
                text.Append("linker-excluded-role:").Append(AssemblyIdentityUtil.CanonicalName(role.name)).Append(':').Append((int)role.classification).Append(':')
                    .Append(role.isPrecompiled).Append(':').Append(role.isShadowCapable).Append(':').Append(role.isBootstrap).Append(':').Append(role.capabilityDeclared).Append('\n');
            foreach (string define in (receipt.extraScriptingDefines ?? new string[0]).OrderBy(d => d, StringComparer.Ordinal)) text.Append(define).Append('\n');
            foreach (var file in AllFiles(receipt).OrderBy(f => f.path, StringComparer.Ordinal))
                text.Append(file.path).Append('\n').Append(file.sha256).Append('\n').Append(file.pdbPath).Append('\n').Append(file.pdbSha256).Append('\n');
            return ShadowHash.Text(text.ToString());
        }

        public static AssemblySnapshotReceipt Capture(string root, IEnumerable<string> assemblyPaths, IEnumerable<string> referencePaths,
            string kind, BuildTarget target, string architecture, ShadowSourcePins pins, IEnumerable<string> defines, IEnumerable<string> filteredAssemblyPaths = null)
        {
            ShadowHash.Require(!Directory.Exists(root), "SnapshotExists", "Snapshots are immutable: " + root);
            Directory.CreateDirectory(root);
            var seen = new Dictionary<string, string>(StringComparer.Ordinal);
            var assemblies = CopyFiles(root, "Assemblies", assemblyPaths, seen, null);
            var filtered = CopyFiles(root, "Assemblies/Filtered", filteredAssemblyPaths ?? new string[0], seen, null);
            var authoritativeInputs = new HashSet<string>(seen.Keys, StringComparer.Ordinal);
            var references = CopyFiles(root, "References", referencePaths, seen, authoritativeInputs);
            var receipt = new AssemblySnapshotReceipt
            {
                kind = kind, target = target.ToString(), architecture = architecture, unityVersion = Application.unityVersion,
                sourcePins = pins, extraScriptingDefines = ShadowHash.Sorted(defines ?? new string[0]), assemblies = assemblies, references = references, filteredAssemblies = filtered,
            };
            ShadowReflectionBindingEvidence.Capture(root, receipt);
            ShadowRawTypeAdmissionEvidence.Capture(root, receipt);
            receipt.snapshotHash = ComputeHash(receipt);
            WriteReceipt(root, receipt);
            return receipt;
        }

        public static IEnumerable<SnapshotFile> AllFiles(AssemblySnapshotReceipt receipt)
        {
            return (receipt.assemblies ?? new SnapshotFile[0]).Concat(receipt.filteredAssemblies ?? new SnapshotFile[0]).Concat(receipt.references ?? new SnapshotFile[0]);
        }

        private static void ValidateSection(IEnumerable<SnapshotFile> files, string section)
        {
            foreach (SnapshotFile file in files ?? new SnapshotFile[0])
                ShadowHash.Require(file != null && !string.IsNullOrWhiteSpace(file.name) && file.path == section + "/" + file.name + ".dll" &&
                    (string.IsNullOrEmpty(file.pdbPath) || file.pdbPath == section + "/" + file.name + ".pdb"),
                    "SnapshotSectionMismatch", "A captured DLL cannot be renamed or moved between input/reference/filter roles.");
        }

        private static SnapshotFile[] CopyFiles(string root, string section, IEnumerable<string> paths, Dictionary<string, string> seen, ISet<string> authoritativeInputs)
        {
            var result = new List<SnapshotFile>();
            Directory.CreateDirectory(Path.Combine(root, section));
            foreach (string source in paths.Select(Path.GetFullPath).Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal))
            {
                ShadowHash.Require(File.Exists(source), "SnapshotInputMissing", source);
                string name;
                using (var module = ModuleDefMD.Load(source))
                {
                    ShadowHash.Require(module.Assembly != null, "NotAssembly", source);
                    name = module.Assembly.Name.String;
                }
                string key = AssemblyIdentityUtil.CanonicalName(name);
                string prior;
                if (seen.TryGetValue(key, out prior))
                {
                    // A compiler reference to an assembly already captured as a Player
                    // input does not introduce a second logical module.
                    // Two distinct reference locations are ambiguous even if they
                    // currently have identical bytes. Only actual captured inputs
                    // may override their compiler reference copies.
                    ShadowHash.Require(authoritativeInputs != null && authoritativeInputs.Contains(key), "DuplicateAssembly", name + ": " + prior + " and " + source);
                    continue;
                }
                seen.Add(key, source);
                string relative = section + "/" + name + ".dll";
                File.Copy(source, ShadowHash.SafeChild(root, relative), false);
                var file = new SnapshotFile { name = name, path = relative, sha256 = ShadowHash.File(source), sourcePath = source };
                string pdb = Path.ChangeExtension(source, ".pdb");
                if (File.Exists(pdb))
                {
                    file.pdbPath = section + "/" + name + ".pdb";
                    File.Copy(pdb, ShadowHash.SafeChild(root, file.pdbPath), false);
                    file.pdbSha256 = ShadowHash.File(pdb);
                }
                result.Add(file);
            }
            return result.OrderBy(f => f.name, StringComparer.Ordinal).ToArray();
        }

        internal static void WriteReceipt(string root, AssemblySnapshotReceipt receipt)
        {
            File.WriteAllText(Path.Combine(root, ReceiptName), JsonUtility.ToJson(receipt, true), new UTF8Encoding(false));
        }

        public static string[] TargetCompilerReferences()
        {
            // This is the Player compiler's explicit reference list, never assemblies
            // discovered by enumerating the running Editor AppDomain.
            return CompilationPipeline.GetAssemblies(AssembliesType.PlayerWithoutTestAssemblies).SelectMany(a => a.compiledAssemblyReferences)
                .Where(p => p.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal).ToArray();
        }

        public static string Compile(string root, BuildTarget target, string architecture, ShadowSourcePins pins, ShadowPolicyConfiguration policy, string[] defines)
        {
            ShadowAssemblyPolicyValidator.ValidateBeforeCompile(policy, target).ThrowIfInvalid();
            string output = Path.Combine(root, "CompilerOutput");
            ShadowHash.Require(!Directory.Exists(root), "SnapshotExists", root);
            Directory.CreateDirectory(output);
            string[] compilationDefines = ShadowReflectionBindingEvidence.CompilationDefines(defines);
            var settings = new ScriptCompilationSettings
            {
                group = BuildPipeline.GetBuildTargetGroup(target), target = target,
                options = ScriptCompilationOptions.DevelopmentBuild, extraScriptingDefines = compilationDefines,
            };
            var compilation = PlayerBuildInterface.CompilePlayerScripts(settings, output);
            ShadowHash.Require(compilation.assemblies != null && compilation.assemblies.Count > 0, "CompileFailed", "No Player assemblies emitted.");
            string[] references = TargetCompilerReferences();
            var emitted = compilation.assemblies.Select(p => File.Exists(p) ? Path.GetFullPath(p) : Path.GetFullPath(Path.Combine(output, p))).ToArray();
            string outputRoot = Path.GetFullPath(output).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            ShadowHash.Require(emitted.All(p => p.StartsWith(outputRoot, StringComparison.Ordinal)), "CompileOutputEscaped", "CompilePlayerScripts returned a DLL outside this compilation output.");
            var names = new HashSet<string>(emitted.Select(Path.GetFileNameWithoutExtension).Select(AssemblyIdentityUtil.CanonicalName), StringComparer.Ordinal);
            var plugins = references.Where(p => !names.Contains(AssemblyIdentityUtil.CanonicalName(p)) && policy.assemblies.Any(a => a.isPrecompiled &&
                (a.classification == AssemblyClassification.Runtime || a.classification == AssemblyClassification.NormalHotUpdate) &&
                AssemblyIdentityUtil.CanonicalName(a.name) == AssemblyIdentityUtil.CanonicalName(p)));
            string snapshot = Path.Combine(root, "Snapshot");
            var receipt = Capture(snapshot, emitted.Concat(plugins), references, "CompilePlayerScripts", target, architecture, pins, compilationDefines);
            ShadowReflectionBindingEvidence.RequirePolicy(policy, snapshot, receipt, false);
            return snapshot;
        }
    }
}
