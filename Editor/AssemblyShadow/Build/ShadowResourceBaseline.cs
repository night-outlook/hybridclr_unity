using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using dnlib.DotNet;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Compilation;
using UnityEngine;

namespace HybridCLR.Editor.AssemblyShadow
{
    public sealed class ShadowResourceBuildRequest
    {
        public string outputDirectory;
        public BuildTarget target;
        public string architecture;
        public ShadowSourcePins sourcePins;
        public ShadowPolicyConfiguration policy;
        public ShadowResourceBuildMap resources;
        /// <summary>
        /// Exact user defines for the compiler snapshot which owns these new
        /// resource bytes. Structural replacement bundles must be built in an
        /// Editor domain compiled with the same set.
        /// </summary>
        public string[] extraScriptingDefines = new string[0];
    }

    [Serializable]
    public sealed class ShadowResourceSource
    {
        public string path;
        public string snapshotPath;
        public string sha256;
        public string metaSnapshotPath;
        public string metaSha256;
        public string guid;
        public bool builtin;
        public string[] dependencies = new string[0];
    }

    [Serializable]
    public sealed class ShadowResourceScript
    {
        public string path;
        public string guid;
        public long localId;
        public string assembly;
        public string @namespace;
        public string type;
    }

    [Serializable]
    public sealed class ShadowResourceProofFile
    {
        public string path;
        public string sha256;
    }

    [Serializable]
    public sealed class ShadowBuiltinResourceProof
    {
        public int schemaVersion = 1;
        public string unityVersion, virtualPath, guid, backingPath, backingSha256;
        public ShadowBuiltinModuleProof[] modules;
        public ShadowBuiltinObjectProof[] objects;
    }

    [Serializable]
    public sealed class ShadowBuiltinModuleProof
    {
        public string assemblyName, path, sha256;
    }

    [Serializable]
    public sealed class ShadowBuiltinObjectProof
    {
        public string name, typeName, assemblyName, guid, serializedSha256;
        public long localId;
        public bool persistent;
    }

    [Serializable]
    public sealed class ShadowResourceBaselineReceipt
    {
        public int schemaVersion = 1;
        public string provenance;
        public string unityVersion;
        public string target;
        public string architecture;
        public string compilerSnapshotPath = "CompilerInputs";
        public string compilerSnapshotHash;
        public bool compilerSnapshotIsPlayer;
        public string[] compilerDefines = new string[0];
        public string[] editorScriptingDefines = new string[0];
        public string metadataAssemblyDirectory = "ResourceAssemblies";
        public ShadowResourceProofFile[] metadataAssemblies = new ShadowResourceProofFile[0];
        public string resourceAbiPath = "resource-abi.json";
        public string resourceAbiHash;
        public string resourceAbiFileSha256;
        public string resourceIndexPath = "resource-script-index.json";
        public string resourceIndexHash;
        public string bundleDirectory = "Bundles";
        public string[] candidateAssemblies;
        public ShadowResourceBuildMap buildMap;
        public ShadowBundleArtifact[] bundles;
        public ShadowResourceSource[] sources;
        public string sourceSetHash;
        public ShadowResourceScript[] scripts;
        public ShadowDependencyConfiguration dependencies;
        // Historical imports are possible only through a separately audited importer.
        // These are provenance checksums, not an M09 signature or trust authority.
        public string originalManifestPath;
        public string originalManifestSha256;
        public string originalSourceAuditPath;
        public string originalSourceAuditSha256;
        public ShadowResourceProofFile[] reconstructionProof = new ShadowResourceProofFile[0];
    }

    public sealed class VerifiedShadowResourceBaseline
    {
        public string Root { get; private set; }
        public ShadowResourceBaselineReceipt Receipt { get; private set; }
        public ResourceAbiDescriptor ResourceAbi { get; private set; }
        public ResourceScriptIndex Index { get; private set; }
        internal VerifiedShadowResourceBaseline(string root, ShadowResourceBaselineReceipt receipt, ResourceAbiDescriptor abi, ResourceScriptIndex index)
        { Root = root; Receipt = receipt; ResourceAbi = abi; Index = index; }
    }

    /// <summary>Owns creation of new resource bytes. There is intentionally no Freeze(oldBundles, currentAbi) API.</summary>
    public static class ShadowResourceBaseline
    {
        public const string ReceiptName = "resource-build-receipt.json";
        public const string FreshBuildProvenance = "CompilePlayerScriptsAndBuildAssetBundles";
        public const string M01Provenance = "M01AuditedFrozenSourceReconstruction";

        public static string Build(ShadowResourceBuildRequest request)
        {
            ShadowHash.Require(request != null && request.policy != null && request.sourcePins != null && request.resources != null,
                "InvalidResourceBuildRequest", "A target, policy, source pins and resource build map are required.");
            var builds = ValidateMap(request.resources);
            ShadowHash.Require(EditorUserBuildSettings.activeBuildTarget == request.target, "ResourceTargetMismatch", "Switch the active target before building resources.");
            ShadowHash.Require(request.sourcePins.unityVersion == Application.unityVersion && request.sourcePins.target == request.target.ToString() &&
                request.sourcePins.architecture == request.architecture, "ResourceTargetMismatch", "Resource source pins must match the current Unity target.");
            string[] compilerDefines = ShadowHash.Sorted(request.extraScriptingDefines ?? new string[0]);
            ShadowHash.Require(compilerDefines.All(value => Regex.IsMatch(value ?? "", "^[A-Za-z_][A-Za-z_0-9]*$")),
                "ResourceCompilerDefines", "Resource compiler defines must be finite user symbols.");
            string[] editorDefines = CurrentEditorDefines(request.target);
            ShadowHash.Require(compilerDefines.All(editorDefines.Contains), "ResourceEditorDomainMismatch",
                "Structural resource compiler defines must also be active in this fresh Editor domain.");
            string temporary = ShadowArtifactWriter.Begin(request.outputDirectory);
            var paths = InputPaths(builds);
            var sources = paths.OrderBy(p => p, StringComparer.Ordinal).Select(p => CaptureSource(p, temporary)).ToArray();
            string compileRoot = Path.GetFullPath("_temp/AssemblyShadow/ResourceCompile-" + Guid.NewGuid().ToString("N"));
            string compiled = AssemblySnapshot.Compile(compileRoot, request.target, request.architecture, request.sourcePins, request.policy, compilerDefines);
            var input = AssemblySnapshot.ReadAndVerify(compiled, false);
            var framework = TargetFrameworkReferenceVerifier.Verify(compiled, input);
            string candidatesJson = JsonUtility.ToJson(request.policy, true);
            string[] candidates = request.policy.assemblies.Where(a => a.isShadowCapable).Select(a => a.name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            ShadowHash.Require(candidates.Length > 0, "ResourceCandidatesMissing", "Resource build has no candidate assemblies.");
            // Resource ABI analysis has candidate roots, not every compiler output.
            // Unused test/compiler-only DLLs remain explicit resolver references;
            // only a successful Player build may classify runtime membership.
            var metadata = CopyResourceMetadata(compiled, temporary, input, candidates);
            ResourceAbiDescriptor abi;
            using (var set = DnlibAssemblyLoader.Load(Path.Combine(temporary, "ResourceAssemblies"),
                new[] { Path.Combine(compiled, "Assemblies"), Path.Combine(compiled, "References") }, request.policy.assemblies,
                targetFrameworkReferences: framework))
            {
                foreach (var name in candidates) set.Get(name);
                abi = UnitySerializedTypeAnalyzer.Analyze(set, candidates);
            }
            RequireProvenAbi(abi);
            var scripts = CaptureScripts(sources, temporary);
            var reader = new FrozenResourceAssetReader(temporary, sources, scripts);
            var index = AssetScriptReferenceIndexer.Build(builds, abi, request.policy.dependencies, reader);
            RequireProvenIndex(index);
            string bundlesRoot = Path.Combine(temporary, "Bundles");
            Directory.CreateDirectory(bundlesRoot);
            var built = BuildPipeline.BuildAssetBundles(bundlesRoot, builds,
                BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.StrictMode | BuildAssetBundleOptions.ForceRebuildAssetBundle, request.target);
            ShadowHash.Require(built != null, "ResourceBuildFailed", "Unity returned no AssetBundle manifest.");
            ShadowHash.Require(new HashSet<string>(built.GetAllAssetBundles(), StringComparer.Ordinal).SetEquals(builds.Select(b => b.assetBundleName)),
                "ResourceBundleSetMismatch", "Unity's produced bundle set differs from the requested build map.");
            foreach (var source in sources) RequireSourceUnchanged(source);
            ShadowHash.Require(paths.SetEquals(InputPaths(builds)), "ResourceInputsChanged", "Compiler/resource source inventory changed during resource build.");
            ShadowHash.Require(candidatesJson == JsonUtility.ToJson(request.policy, true), "ResourceInputsChanged", "Policy changed during resource build.");
            CopyCompilerSnapshot(compiled, Path.Combine(temporary, "CompilerInputs"), input);
            var receipt = new ShadowResourceBaselineReceipt
            {
                provenance = FreshBuildProvenance, unityVersion = Application.unityVersion, target = request.target.ToString(), architecture = request.architecture,
                compilerSnapshotHash = input.snapshotHash, compilerDefines = compilerDefines, editorScriptingDefines = editorDefines,
                candidateAssemblies = candidates, buildMap = PortableMap(request.resources),
                bundles = builds.Select(b => new ShadowBundleArtifact { name = b.assetBundleName, assets = ShadowHash.Sorted(b.assetNames),
                    sha256 = ShadowHash.File(Path.Combine(bundlesRoot, b.assetBundleName)) }).OrderBy(b => b.name, StringComparer.Ordinal).ToArray(),
                sources = sources, sourceSetHash = ComputeSourceSetHash(sources), scripts = scripts, dependencies = request.policy.dependencies,
                metadataAssemblies = metadata,
                resourceAbiHash = ResourceAbiHasher.Compute(abi), resourceIndexHash = ShadowHash.Text(JsonUtility.ToJson(index, true)),
            };
            ShadowArtifactWriter.Json(temporary, receipt.resourceAbiPath, abi);
            receipt.resourceAbiFileSha256 = ShadowHash.File(Path.Combine(temporary, receipt.resourceAbiPath));
            ShadowArtifactWriter.Json(temporary, receipt.resourceIndexPath, index);
            ShadowArtifactWriter.Json(temporary, ReceiptName, receipt);
            ShadowArtifactWriter.Finish(temporary, request.outputDirectory, ReceiptName);
            ReadAndVerify(request.outputDirectory, request.target, request.architecture);
            return Path.GetFullPath(request.outputDirectory);
        }

        public static VerifiedShadowResourceBaseline ReadAndVerify(string root, BuildTarget target, string architecture)
        {
            root = Path.GetFullPath(root);
            var receipt = ShadowArtifactWriter.ReadVerifiedManifest<ShadowResourceBaselineReceipt>(Path.Combine(root, ReceiptName));
            ShadowHash.Require(receipt.schemaVersion == 1 && receipt.unityVersion == Application.unityVersion && receipt.target == target.ToString() &&
                receipt.architecture == architecture, "ResourceReceiptIdentity", "Resource receipt schema/Unity/target/architecture mismatch.");
            ShadowHash.Require(receipt.provenance == FreshBuildProvenance || receipt.provenance == M01Provenance, "ResourceProvenanceMissing", "No recognized resource build provenance.");
            var builds = ValidateMap(receipt.buildMap);
            ShadowHash.Require(receipt.sources != null && receipt.scripts != null && receipt.bundles != null && receipt.candidateAssemblies != null &&
                receipt.candidateAssemblies.Length > 0, "ResourceReceiptIncomplete", root);
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var bundle in receipt.bundles)
            {
                ShadowHash.Require(bundle != null && names.Add(bundle.name), "ResourceBundleSetMismatch", "Null or duplicate resource bundle.");
                var definition = builds.SingleOrDefault(b => b.assetBundleName == bundle.name);
                ShadowHash.Require(definition.assetNames != null && new HashSet<string>(definition.assetNames, StringComparer.Ordinal).SetEquals(bundle.assets ?? new string[0]),
                    "ResourceBundleSetMismatch", bundle.name);
                VerifyFile(root, receipt.bundleDirectory + "/" + bundle.name, bundle.sha256, "ResourceBundleHashMismatch");
            }
            ShadowHash.Require(names.SetEquals(builds.Select(b => b.assetBundleName)), "ResourceBundleSetMismatch", "Resource bundle set is incomplete.");
            ShadowHash.Require(receipt.sourceSetHash == ComputeSourceSetHash(receipt.sources), "ResourceSourceSetMismatch", root);
            foreach (var source in receipt.sources)
            {
                VerifyFile(root, source.snapshotPath, source.sha256, "ResourceSourceHashMismatch");
                if (source.builtin) VerifyBuiltinSource(root, source);
                if (!string.IsNullOrEmpty(source.metaSnapshotPath))
                {
                    VerifyFile(root, source.metaSnapshotPath, source.metaSha256, "ResourceSourceHashMismatch");
                    ShadowHash.Require(MetaGuid(File.ReadAllText(ShadowHash.SafeChild(root, source.metaSnapshotPath))) == source.guid,
                        "ResourceSourceGuidMismatch", source.path);
                }
            }
            var input = AssemblySnapshot.ReadAndVerify(ShadowHash.SafeChild(root, receipt.compilerSnapshotPath), receipt.compilerSnapshotIsPlayer);
            var framework = TargetFrameworkReferenceVerifier.Verify(ShadowHash.SafeChild(root, receipt.compilerSnapshotPath), input);
            ShadowHash.Require(input.snapshotHash == receipt.compilerSnapshotHash && input.unityVersion == receipt.unityVersion && input.target == receipt.target &&
                input.architecture == receipt.architecture, "ResourceCompilerMismatch", "Resource compiler receipt identity changed.");
            if (receipt.provenance == FreshBuildProvenance)
            {
                string[] recordedDefines = ShadowHash.Sorted(receipt.compilerDefines ?? new string[0]);
                string[] recordedEditorDefines = ShadowHash.Sorted(receipt.editorScriptingDefines ?? new string[0]);
                ShadowHash.Require(!receipt.compilerSnapshotIsPlayer && recordedDefines.SequenceEqual(receipt.compilerDefines ?? new string[0], StringComparer.Ordinal) &&
                    recordedEditorDefines.SequenceEqual(receipt.editorScriptingDefines ?? new string[0], StringComparer.Ordinal) &&
                    recordedDefines.All(recordedEditorDefines.Contains) &&
                    recordedDefines.All(value => Regex.IsMatch(value ?? "", "^[A-Za-z_][A-Za-z_0-9]*$")) &&
                    ShadowReflectionBindingEvidence.UserDefines(input.extraScriptingDefines).SequenceEqual(recordedDefines, StringComparer.Ordinal) &&
                    receipt.metadataAssemblyDirectory == "ResourceAssemblies",
                    "ResourceCompilerMismatch", "New resources require one fresh CompilePlayerScripts snapshot with the exact recorded user defines.");
                RequireFreshMetadataInputs(receipt, input);
            }
            else VerifyM01Proof(root, receipt);
            VerifyMetadataFiles(root, receipt);
            VerifyFile(root, receipt.resourceAbiPath, receipt.resourceAbiFileSha256, "ResourceAbiHashMismatch");
            var abi = JsonUtility.FromJson<ResourceAbiDescriptor>(File.ReadAllText(ShadowHash.SafeChild(root, receipt.resourceAbiPath)));
            RequireProvenAbi(abi);
            ShadowHash.Require(ResourceAbiHasher.Compute(abi) == receipt.resourceAbiHash, "ResourceAbiHashMismatch", receipt.resourceAbiPath);
            using (var set = DnlibAssemblyLoader.Load(ShadowHash.SafeChild(root, receipt.metadataAssemblyDirectory),
                new[] { ShadowHash.SafeChild(root, receipt.compilerSnapshotPath + "/Assemblies"), ShadowHash.SafeChild(root, receipt.compilerSnapshotPath + "/References") }, null,
                targetFrameworkReferences: framework))
            {
                foreach (string candidate in receipt.candidateAssemblies) set.Get(candidate);
                foreach (var script in receipt.scripts)
                {
                    ModuleDefMD module;
                    string name = string.IsNullOrEmpty(script.@namespace) ? script.type : script.@namespace + "." + script.type;
                    ShadowHash.Require(set.Modules.TryGetValue(AssemblyIdentityUtil.CanonicalName(script.assembly), out module) &&
                        module.GetTypes().Count(t => t.FullName == name) == 1, "ResourceScriptIdentity", "Script identity is absent or ambiguous in target metadata: " + script.path);
                }
                var fromCompiler = UnitySerializedTypeAnalyzer.Analyze(set, receipt.candidateAssemblies);
                RequireProvenAbi(fromCompiler);
                ShadowHash.Require(ResourceAbiHasher.Compute(fromCompiler) == receipt.resourceAbiHash, "ResourceCompilerAbiMismatch", "Stored resource ABI is not the captured compiler input ABI.");
            }
            VerifyFile(root, receipt.resourceIndexPath, receipt.resourceIndexHash, "ResourceIndexHashMismatch");
            var index = JsonUtility.FromJson<ResourceScriptIndex>(File.ReadAllText(ShadowHash.SafeChild(root, receipt.resourceIndexPath)));
            RequireProvenIndex(index);
            var rebuiltIndex = AssetScriptReferenceIndexer.Build(builds, abi, receipt.dependencies, new FrozenResourceAssetReader(root, receipt.sources, receipt.scripts));
            RequireProvenIndex(rebuiltIndex);
            ShadowHash.Require(ShadowHash.Text(JsonUtility.ToJson(rebuiltIndex, true)) == receipt.resourceIndexHash, "ResourceIndexProvenanceMismatch",
                "Resource index is not derived from the frozen source bytes and script identities.");
            return new VerifiedShadowResourceBaseline(root, receipt, abi, index);
        }

        public static void RequirePlayerAbi(VerifiedShadowResourceBaseline frozen, ResourceAbiDescriptor playerAbi)
        {
            if (frozen == null) throw new ArgumentNullException("frozen");
            RequireProvenAbi(playerAbi);
            ShadowHash.Require(ResourceAbiHasher.Compute(playerAbi) == frozen.Receipt.resourceAbiHash, "ResourcePlayerAbiMismatch",
                "Player serialization ABI differs from the compiler/source inputs which produced the frozen resources. Build matching new resources.");
        }

        private static ShadowResourceProofFile[] CopyResourceMetadata(string compiled, string root, AssemblySnapshotReceipt input, IEnumerable<string> candidates)
        {
            var result = new List<ShadowResourceProofFile>();
            foreach (string name in candidates)
            {
                var file = input.assemblies.SingleOrDefault(a => AssemblyIdentityUtil.CanonicalName(a.name) == AssemblyIdentityUtil.CanonicalName(name));
                ShadowHash.Require(file != null, "ResourceCandidateMissing", "Candidate was not emitted by this compiler snapshot: " + name);
                string path = "ResourceAssemblies/" + file.name + ".dll";
                ShadowArtifactWriter.CopyVerified(ShadowHash.SafeChild(compiled, file.path), root, path, file.sha256);
                result.Add(new ShadowResourceProofFile { path = path, sha256 = file.sha256 });
            }
            return result.OrderBy(f => f.path, StringComparer.Ordinal).ToArray();
        }

        private static void RequireFreshMetadataInputs(ShadowResourceBaselineReceipt receipt, AssemblySnapshotReceipt input)
        {
            var candidates = new HashSet<string>(receipt.candidateAssemblies.Select(AssemblyIdentityUtil.CanonicalName), StringComparer.Ordinal);
            var metadata = receipt.metadataAssemblies ?? new ShadowResourceProofFile[0];
            ShadowHash.Require(metadata.Length == candidates.Count && candidates.SetEquals(metadata.Select(f => AssemblyIdentityUtil.CanonicalName(f.path))),
                "ResourceCompilerMismatch", "Resource metadata must contain exactly the declared candidate roots.");
            foreach (var file in metadata)
            {
                var compiled = input.assemblies.SingleOrDefault(a => AssemblyIdentityUtil.CanonicalName(a.name) == AssemblyIdentityUtil.CanonicalName(file.path));
                ShadowHash.Require(compiled != null && file.path == "ResourceAssemblies/" + compiled.name + ".dll" && file.sha256 == compiled.sha256,
                    "ResourceCompilerMismatch", "Resource metadata bytes differ from their fresh compiler input: " + file.path);
            }
        }

        public static string ComputeSourceSetHash(IEnumerable<ShadowResourceSource> sources)
        {
            var entries = (sources ?? new ShadowResourceSource[0]).ToArray();
            ShadowHash.Require(entries.All(s => s != null && !string.IsNullOrEmpty(s.path)) && entries.Select(s => s.path).Distinct(StringComparer.Ordinal).Count() == entries.Length,
                "ResourceSourceSetMismatch", "Null or duplicate resource source path.");
            return ShadowHash.Text("resource-source-set:1\n" + string.Join("\n", entries.OrderBy(s => s.path, StringComparer.Ordinal).Select(s =>
                JsonUtility.ToJson(s)).ToArray()));
        }

        private static string[] CurrentEditorDefines(BuildTarget target)
        {
            string serialized = PlayerSettings.GetScriptingDefineSymbols(NamedBuildTarget.FromBuildTargetGroup(BuildPipeline.GetBuildTargetGroup(target))) ?? "";
            string[] values = serialized.Length == 0 ? new string[0] : serialized.Split(';');
            ShadowHash.Require(values.All(value => Regex.IsMatch(value, "^[A-Za-z_][A-Za-z_0-9]*$")) &&
                values.Distinct(StringComparer.Ordinal).Count() == values.Length,
                "ResourceEditorDomainMismatch", "Current Editor scripting defines are not a canonical finite set.");
            return ShadowHash.Sorted(values);
        }

        public static AssetBundleBuild[] ValidateMap(ShadowResourceBuildMap map)
        {
            ShadowHash.Require(map != null && map.schemaVersion == 1 && map.bundles != null && map.bundles.Length > 0, "ResourceMapSchema", "A nonempty versioned resource map is required.");
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var bundle in map.bundles)
            {
                ShadowHash.Require(bundle != null && !string.IsNullOrWhiteSpace(bundle.name) && !bundle.name.Contains("/") && !bundle.name.Contains("\\") &&
                    bundle.name != "." && bundle.name != ".." && names.Add(bundle.name), "ResourceMapSchema", "Bundle names must be unique file names.");
                ShadowHash.Require(bundle.assets != null && bundle.assets.Length > 0 && bundle.assets.All(a => !string.IsNullOrWhiteSpace(a) && !Path.IsPathRooted(a) &&
                    !a.Split('/').Contains("..")) && bundle.assets.Distinct(StringComparer.Ordinal).Count() == bundle.assets.Length, "ResourceMapSchema", bundle.name);
            }
            return map.bundles.OrderBy(b => b.name, StringComparer.Ordinal).Select(b => new AssetBundleBuild { assetBundleName = b.name, assetNames = ShadowHash.Sorted(b.assets) }).ToArray();
        }

        private static ShadowResourceBuildMap PortableMap(ShadowResourceBuildMap map)
        { return new ShadowResourceBuildMap { bundleDirectory = "Bundles", bundles = ValidateMap(map).Select(b => new ShadowBundleDefinition { name = b.assetBundleName, assets = b.assetNames }).ToArray() }; }

        private static HashSet<string> InputPaths(AssetBundleBuild[] builds)
        {
            var paths = new HashSet<string>(builds.SelectMany(b => b.assetNames), StringComparer.Ordinal);
            foreach (string path in paths.ToArray()) paths.UnionWith(AssetDatabase.GetDependencies(path, true));
            foreach (var assembly in CompilationPipeline.GetAssemblies(AssembliesType.PlayerWithoutTestAssemblies))
            {
                paths.UnionWith(assembly.sourceFiles);
                string definition = CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(assembly.name);
                if (!string.IsNullOrEmpty(definition)) paths.Add(definition);
            }
            foreach (string path in Directory.GetFiles("ProjectSettings", "*", SearchOption.TopDirectoryOnly)) paths.Add(path.Replace('\\', '/'));
            return paths;
        }

        private static string SourceFile(string path)
        {
            if (File.Exists(path) || !path.StartsWith("Packages/", StringComparison.Ordinal)) return path;
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(path);
            ShadowHash.Require(package != null && path.StartsWith("Packages/" + package.name + "/", StringComparison.Ordinal), "ResourceSourceMissing", path);
            return Path.Combine(package.resolvedPath, path.Substring(("Packages/" + package.name + "/").Length));
        }

        private static ShadowResourceSource CaptureSource(string path, string root)
        {
            string relative = "Sources/" + (Path.IsPathRooted(path) ? "External/" + ShadowHash.Text(path) + "/" + Path.GetFileName(path) : path.Replace('\\', '/'));
            var source = new ShadowResourceSource { path = path, snapshotPath = relative, guid = AssetDatabase.AssetPathToGUID(path) };
            string physical = SourceFile(path);
            if (File.Exists(physical))
            {
                source.sha256 = ShadowHash.File(physical);
                ShadowArtifactWriter.CopyVerified(physical, root, relative, source.sha256);
                if (File.Exists(physical + ".meta"))
                {
                    source.metaSnapshotPath = relative + ".meta"; source.metaSha256 = ShadowHash.File(physical + ".meta");
                    source.guid = MetaGuid(File.ReadAllText(physical + ".meta"));
                    ShadowArtifactWriter.CopyVerified(physical + ".meta", root, source.metaSnapshotPath, source.metaSha256);
                }
                if (!string.IsNullOrEmpty(source.guid)) source.dependencies = ShadowHash.Sorted(AssetDatabase.GetDependencies(path, true));
            }
            else
            {
                source.builtin = true;
                string payload = BuiltinPayload(path, out source.guid, root);
                source.sha256 = ShadowHash.Text(payload);
                string destination = ShadowHash.SafeChild(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)); File.WriteAllText(destination, payload, new UTF8Encoding(false));
            }
            return source;
        }

        private static string BuiltinPayload(string path, out string guid, string captureRoot = null)
        {
            string folder = Path.GetDirectoryName(path.Replace('\\', '/'));
            ShadowHash.Require(folder == "Library" || folder == "Resources", "ResourceBuiltinUnproven", "Not a Unity resource container: " + path);
            string backing = Path.Combine(EditorApplication.applicationContentsPath, "Resources", Path.GetFileName(path));
            ShadowHash.Require(File.Exists(backing), "ResourceBuiltinUnproven", "No engine backing bytes for " + path);
            guid = AssetDatabase.AssetPathToGUID(path);
            ShadowHash.Require(!string.IsNullOrEmpty(guid) && AssetDatabase.GUIDToAssetPath(guid) == path,
                "ResourceBuiltinUnproven", "Builtin container lacks a round-trip AssetDatabase identity: " + path);
            var objects = AssetDatabase.LoadAllAssetsAtPath(path);
            ShadowHash.Require(objects != null && objects.Length > 0, "ResourceSourceMissing", path);
            var identities = new HashSet<long>();
            var modules = new Dictionary<string, ShadowBuiltinModuleProof>(StringComparer.Ordinal);
            var payload = new List<ShadowBuiltinObjectProof>();
            var proof = new ShadowBuiltinResourceProof { unityVersion = Application.unityVersion, virtualPath = path, guid = guid, backingSha256 = ShadowHash.File(backing) };
            proof.backingPath = "BuiltinProof/Resources/" + proof.backingSha256 + "/" + Path.GetFileName(backing);
            if (captureRoot != null) CopyBuiltinProof(backing, captureRoot, proof.backingPath, proof.backingSha256);
            foreach (var asset in objects)
            {
                string identity = null; long localId = 0;
                bool identified = asset != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out identity, out localId);
                ShadowHash.Require(asset != null && !(asset is MonoBehaviour) && identified && identity == guid && localId != 0 &&
                    EditorUtility.IsPersistent(asset) && AssetDatabase.GetAssetPath(asset) == path && identities.Add(localId),
                    "ResourceBuiltinUnproven", path + ": " + (asset == null ? "null object" :
                        "name=" + asset.name + ", type=" + asset.GetType().AssemblyQualifiedName + ", assetPath=" + AssetDatabase.GetAssetPath(asset) +
                        ", persistent=" + EditorUtility.IsPersistent(asset) + ", identified=" + identified + ", guid=" + identity + ", localId=" + localId));
                // ScriptableObject is also the base of native engine objects such as GUISkin.
                // The concrete type and its complete Unity base chain must come from the
                // running installation's actual engine modules; a project subclass cannot pass.
                for (Type type = asset.GetType(); type != null && type != typeof(object); type = type.BaseType)
                {
                    string modulePath = RequireEngineModule(type);
                    string name = type.Assembly.GetName().Name;
                    if (modules.ContainsKey(name)) continue;
                    string hash = ShadowHash.File(modulePath);
                    var module = new ShadowBuiltinModuleProof { assemblyName = name, path = "BuiltinProof/Modules/" + hash + "/" + name + ".dll", sha256 = hash };
                    modules.Add(name, module);
                    if (captureRoot != null) CopyBuiltinProof(modulePath, captureRoot, module.path, hash);
                }
                payload.Add(new ShadowBuiltinObjectProof { name = asset.name, typeName = asset.GetType().FullName.Replace('+', '/'),
                    assemblyName = asset.GetType().Assembly.GetName().Name, guid = identity, localId = localId, persistent = true,
                    serializedSha256 = ShadowHash.Text(EditorJsonUtility.ToJson(asset)) });
            }
            proof.modules = modules.Values.OrderBy(m => m.assemblyName, StringComparer.Ordinal).ToArray();
            proof.objects = payload.OrderBy(o => o.localId).ToArray();
            return JsonUtility.ToJson(proof, true);
        }

        private static string RequireEngineModule(Type type)
        {
            string name = type.Assembly.GetName().Name;
            string expected = Path.GetFullPath(Path.Combine(EditorApplication.applicationContentsPath, "Managed/UnityEngine", name + ".dll"));
            ShadowHash.Require(IsInstallationModule(name) && !type.Assembly.IsDynamic &&
                !string.IsNullOrEmpty(type.Assembly.Location) && Path.GetFullPath(type.Assembly.Location) == expected && File.Exists(expected),
                "ResourceBuiltinTypeUnproven", "Builtin concrete/base type is not defined by the running Unity engine: " + type.AssemblyQualifiedName);
            return expected;
        }

        private static bool IsInstallationModule(string name)
        {
            // The native MonoScript wrapper lives in UnityEditor.CoreModule, including
            // for builtin Editor assets. Its bytes still have to come from the running
            // installation's Managed/UnityEngine directory, not a project/package DLL.
            return name == "UnityEngine" || name.StartsWith("UnityEngine.", StringComparison.Ordinal) ||
                name == "UnityEditor" || name.StartsWith("UnityEditor.", StringComparison.Ordinal);
        }

        private static void CopyBuiltinProof(string source, string root, string relative, string hash)
        {
            string destination = ShadowHash.SafeChild(root, relative);
            if (File.Exists(destination))
                ShadowHash.Require(ShadowHash.File(destination) == hash && ShadowHash.File(source) == hash, "ResourceBuiltinProofMismatch", relative);
            else ShadowArtifactWriter.CopyVerified(source, root, relative, hash);
        }

        /// <summary>Revalidates portable engine bytes and type provenance without loading a current Editor asset.</summary>
        public static void VerifyBuiltinSource(string root, ShadowResourceSource source)
        {
            ShadowHash.Require(source != null && source.builtin, "ResourceBuiltinProofMissing", "Expected a builtin source receipt.");
            VerifyFile(root, source.snapshotPath, source.sha256, "ResourceSourceHashMismatch");
            var proof = JsonUtility.FromJson<ShadowBuiltinResourceProof>(File.ReadAllText(ShadowHash.SafeChild(root, source.snapshotPath)));
            ShadowHash.Require(proof != null && proof.schemaVersion == 1 && proof.unityVersion == Application.unityVersion && proof.virtualPath == source.path &&
                proof.guid == source.guid && !string.IsNullOrEmpty(proof.guid) && proof.objects != null && proof.objects.Length > 0 && proof.modules != null && proof.modules.Length > 0,
                "ResourceBuiltinProofMissing", source.path);
            VerifyFile(root, proof.backingPath, proof.backingSha256, "ResourceBuiltinProofMismatch");
            var modules = new Dictionary<string, ModuleDefMD>(StringComparer.Ordinal);
            try
            {
                foreach (var moduleProof in proof.modules)
                {
                    ShadowHash.Require(moduleProof != null && !string.IsNullOrEmpty(moduleProof.assemblyName) &&
                        IsInstallationModule(moduleProof.assemblyName) && !modules.ContainsKey(moduleProof.assemblyName),
                        "ResourceBuiltinTypeUnproven", "Invalid or duplicate engine module proof.");
                    VerifyFile(root, moduleProof.path, moduleProof.sha256, "ResourceBuiltinProofMismatch");
                    var module = ModuleDefMD.Load(File.ReadAllBytes(ShadowHash.SafeChild(root, moduleProof.path)));
                    if (module.Assembly == null || module.Assembly.Name.String != moduleProof.assemblyName)
                    { module.Dispose(); throw new ShadowBuildException("ResourceBuiltinTypeUnproven", "Engine module identity differs from its proof."); }
                    modules.Add(moduleProof.assemblyName, module);
                }
                var identities = new HashSet<long>();
                var provenTypes = new HashSet<string>(StringComparer.Ordinal);
                foreach (var asset in proof.objects)
                {
                    ShadowHash.Require(asset != null && asset.persistent && asset.guid == proof.guid && asset.localId != 0 && identities.Add(asset.localId) &&
                        Regex.IsMatch(asset.serializedSha256 ?? "", "^[0-9a-f]{64}$"), "ResourceBuiltinIdentityMismatch", source.path);
                    if (provenTypes.Add(asset.assemblyName + ":" + asset.typeName)) RequireEngineObjectType(modules, asset.assemblyName, asset.typeName);
                }
            }
            finally { foreach (var module in modules.Values) module.Dispose(); }
        }

        private static void RequireEngineObjectType(IDictionary<string, ModuleDefMD> modules, string assemblyName, string typeName)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (true)
            {
                ModuleDefMD module;
                ShadowHash.Require(!string.IsNullOrEmpty(assemblyName) && !string.IsNullOrEmpty(typeName) && visited.Add(assemblyName + ":" + typeName) && modules.TryGetValue(assemblyName, out module),
                    "ResourceBuiltinTypeUnproven", "Unproven engine type/base chain: " + assemblyName + ":" + typeName);
                module = modules[assemblyName];
                var types = module.GetTypes().Where(t => t.FullName == typeName).ToArray();
                ShadowHash.Require(types.Length == 1 && typeName != "UnityEngine.MonoBehaviour", "ResourceBuiltinTypeUnproven", "Missing/ambiguous or MonoBehaviour builtin type: " + typeName);
                if (typeName == "UnityEngine.Object") return;
                var baseType = types[0].BaseType;
                ShadowHash.Require(baseType != null && baseType.DefinitionAssembly != null, "ResourceBuiltinTypeUnproven", "Builtin has no proven UnityEngine.Object ancestry: " + typeName);
                assemblyName = baseType.DefinitionAssembly.Name.String; typeName = baseType.FullName;
            }
        }

        /// <summary>Read-only diagnostic: records the actual members and persistent identities of one builtin container.</summary>
        public static void InspectBuiltinResources()
        {
            string path = "Library/unity default resources";
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i + 1 < args.Length; ++i) if (args[i] == "-shadowBuiltinPath") path = args[i + 1];
            var objects = AssetDatabase.LoadAllAssetsAtPath(path) ?? new UnityEngine.Object[0];
            ShadowHash.Require(objects.Length <= 4096, "ResourceInspectionBound", "Builtin diagnostic is limited to 4096 objects; actual count=" + objects.Length);
            var rows = new List<BuiltinInspectionRow>();
            foreach (var asset in objects)
            {
                var row = new BuiltinInspectionRow { isNull = asset == null };
                if (asset != null)
                {
                    row.name = asset.name; row.type = asset.GetType().AssemblyQualifiedName;
                    row.assetPath = AssetDatabase.GetAssetPath(asset); row.persistent = EditorUtility.IsPersistent(asset);
                    row.monoBehaviour = asset is MonoBehaviour; row.scriptableObject = asset is ScriptableObject;
                    row.hasIdentity = AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out row.guid, out row.localId);
                    try { string json = EditorJsonUtility.ToJson(asset); row.jsonSha256 = ShadowHash.Text(json); row.jsonLength = json.Length; }
                    catch (Exception ex) { row.serializationError = ex.GetType().FullName + ": " + ex.Message; }
                }
                rows.Add(row);
            }
            var report = new BuiltinInspection { unityVersion = Application.unityVersion, target = EditorUserBuildSettings.activeBuildTarget.ToString(),
                path = path, pathGuid = AssetDatabase.AssetPathToGUID(path), objects = rows.ToArray() };
            string output = Path.GetFullPath("_temp/AssemblyShadow/builtin-resource-inspection-" + Guid.NewGuid().ToString("N") + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            File.WriteAllText(output, JsonUtility.ToJson(report, true), new UTF8Encoding(false));
            Debug.Log("[AssemblyShadow] Builtin resource inspection: " + output + "; objects=" + objects.Length +
                "; legacy predicate failures=" + rows.Count(r => r.isNull || r.monoBehaviour || r.scriptableObject || !r.hasIdentity));
        }

        [Serializable] private sealed class BuiltinInspection { public string unityVersion, target, path, pathGuid; public BuiltinInspectionRow[] objects; }
        [Serializable] private sealed class BuiltinInspectionRow
        {
            public string name, type, assetPath, guid, jsonSha256, serializationError;
            public bool isNull, persistent, monoBehaviour, scriptableObject, hasIdentity;
            public long localId;
            public int jsonLength;
        }

        private static void RequireSourceUnchanged(ShadowResourceSource source)
        {
            string guid;
            string hash = source.builtin ? ShadowHash.Text(BuiltinPayload(source.path, out guid)) : ShadowHash.File(SourceFile(source.path));
            ShadowHash.Require(hash == source.sha256 && (string.IsNullOrEmpty(source.metaSnapshotPath) || ShadowHash.File(SourceFile(source.path) + ".meta") == source.metaSha256),
                "ResourceInputsChanged", source.path + " changed during compiler/bundle construction.");
            if (!source.builtin && !string.IsNullOrEmpty(source.guid))
                ShadowHash.Require(ShadowHash.Sorted(AssetDatabase.GetDependencies(source.path, true)).SequenceEqual(source.dependencies), "ResourceInputsChanged", source.path + " dependencies changed.");
        }

        public static ShadowResourceSource CaptureBuiltinSource(string path, string root)
        {
            ShadowHash.Require(!File.Exists(SourceFile(path)), "ResourceBuiltinUnproven", "Only Unity-owned built-in resources use this capture route.");
            return CaptureSource(path, root);
        }

        private static ShadowResourceScript[] CaptureScripts(IEnumerable<ShadowResourceSource> sources, string root)
        {
            var result = new List<ShadowResourceScript>();
            var captured = sources.ToArray();
            var referenced = new HashSet<string>(StringComparer.Ordinal);
            foreach (var source in captured.Where(s => !s.builtin && !s.path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)))
            {
                string text = File.ReadAllText(ShadowHash.SafeChild(root, source.snapshotPath));
                if (!text.StartsWith("%YAML", StringComparison.Ordinal)) continue;
                foreach (Match match in Regex.Matches(text, @"(?m)^  m_Script:.*\bguid:\s*([0-9a-f]{32})\b")) referenced.Add(match.Groups[1].Value);
            }
            foreach (var source in captured.Where(s => s.path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && referenced.Contains(s.guid)))
            {
                var script = AssetDatabase.LoadAssetAtPath<MonoScript>(source.path);
                var type = script == null ? null : script.GetClass();
                if (type == null) continue;
                string guid; long local;
                ShadowHash.Require(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(script, out guid, out local) && guid == source.guid, "ResourceScriptIdentity", source.path);
                result.Add(new ShadowResourceScript { path = source.path, guid = guid, localId = local,
                    assembly = type.Assembly.GetName().Name, @namespace = type.Namespace ?? "", type = type.Name.Replace('+', '/') });
            }
            return result.OrderBy(s => s.guid, StringComparer.Ordinal).ThenBy(s => s.localId).ToArray();
        }

        private static void CopyCompilerSnapshot(string source, string destination, AssemblySnapshotReceipt receipt)
        {
            Directory.CreateDirectory(destination);
            foreach (var file in AssemblySnapshot.AllFiles(receipt))
            {
                ShadowArtifactWriter.CopyVerified(Path.Combine(source, file.path), destination, file.path, file.sha256);
                if (!string.IsNullOrEmpty(file.pdbPath)) ShadowArtifactWriter.CopyVerified(Path.Combine(source, file.pdbPath), destination, file.pdbPath, file.pdbSha256);
            }
            ShadowArtifactWriter.Json(destination, AssemblySnapshot.ReceiptName, receipt);
            if (receipt.linkedPlayerReceipt != null) ShadowLinkedPlayerEvidence.Copy(source, destination, receipt);
            ShadowReflectionBindingEvidence.Copy(source, destination, receipt);
        }

        private static void VerifyMetadataFiles(string root, ShadowResourceBaselineReceipt receipt)
        {
            ShadowHash.Require(receipt.metadataAssemblies != null && receipt.metadataAssemblies.Length > 0, "ResourceCompilerMismatch", "No resource metadata input inventory.");
            foreach (var file in receipt.metadataAssemblies) VerifyFile(root, file.path, file.sha256, "ResourceCompilerMismatch");
            var expected = new HashSet<string>(receipt.metadataAssemblies.Select(f => Path.GetFullPath(ShadowHash.SafeChild(root, f.path))), StringComparer.Ordinal);
            ShadowHash.Require(expected.Count == receipt.metadataAssemblies.Length && expected.SetEquals(Directory.GetFiles(ShadowHash.SafeChild(root, receipt.metadataAssemblyDirectory), "*.dll", SearchOption.AllDirectories).Select(Path.GetFullPath)),
                "ResourceCompilerMismatch", "Resource metadata DLL set differs from receipt.");
        }

        private static void VerifyM01Proof(string root, ShadowResourceBaselineReceipt receipt)
        {
            ShadowHash.Require(receipt.compilerSnapshotIsPlayer && receipt.originalManifestPath != null && receipt.originalSourceAuditPath != null &&
                receipt.reconstructionProof != null && receipt.reconstructionProof.Length > 0, "ResourceHistoricalProofMissing", "M01 import needs its original manifest, source audit and frozen-source reconstruction evidence.");
            VerifyFile(root, receipt.originalManifestPath, receipt.originalManifestSha256, "ResourceHistoricalProofMismatch");
            VerifyFile(root, receipt.originalSourceAuditPath, receipt.originalSourceAuditSha256, "ResourceHistoricalProofMismatch");
            foreach (var file in receipt.reconstructionProof) VerifyFile(root, file.path, file.sha256, "ResourceHistoricalProofMismatch");
            var original = JsonUtility.FromJson<HistoricalManifest>(File.ReadAllText(ShadowHash.SafeChild(root, receipt.originalManifestPath)));
            var audit = JsonUtility.FromJson<HistoricalAudit>(File.ReadAllText(ShadowHash.SafeChild(root, receipt.originalSourceAuditPath)));
            ShadowHash.Require(original != null && original.schemaVersion == 1 && original.baselineBuildId == "M01-Baseline-v1" && original.unityVersion == receipt.unityVersion &&
                original.target == receipt.target && original.architecture == receipt.architecture && audit != null && audit.verified && audit.comparedFiles != null,
                "ResourceHistoricalProofMismatch", "Original M01 proof identity is inconsistent.");
            foreach (var bundle in receipt.bundles)
                ShadowHash.Require(original.bundles != null && original.bundles.Count(b => b.name == bundle.name && b.sha256 == bundle.sha256) == 1, "ResourceHistoricalProofMismatch", bundle.name);
            ShadowHash.Require(original.bundles.Length == receipt.bundles.Length, "ResourceHistoricalProofMismatch", "Original bundle set changed.");
            foreach (var assembly in original.assemblies ?? new HistoricalAssembly[0])
                ShadowHash.Require(receipt.metadataAssemblies.Count(a => Path.GetFileName(a.path) == assembly.name + ".dll" && a.sha256 == assembly.sha256) == 1,
                    "ResourceHistoricalProofMismatch", assembly.name);
            foreach (var item in audit.comparedFiles)
            {
                var source = receipt.sources.SingleOrDefault(s => s.path == item.path || s.path + ".meta" == item.path);
                ShadowHash.Require(item.matchesFrozen && source != null && (source.path == item.path ? source.sha256 : source.metaSha256) == item.sha256,
                    "ResourceHistoricalProofMismatch", item.path);
            }
        }

        private static void RequireProvenAbi(ResourceAbiDescriptor abi)
        {
            ShadowHash.Require(abi != null && abi.schemaVersion == ResourceAbiHasher.SchemaVersion && abi.unknowns != null && abi.unknowns.Length == 0 && abi.types != null &&
                abi.types.All(t => t != null && !t.hasUnknown && (t.unknownReasons ?? new string[0]).Length == 0 && (t.fields ?? new ResourceAbiFieldDescriptor[0]).All(f => f != null && !f.unknown)),
                "ResourceAbiUnproven", "Unresolved resource ABI cannot establish a resource baseline.");
        }
        private static void RequireProvenIndex(ResourceScriptIndex index)
        { ShadowHash.Require(index != null && index.schemaVersion == 2 && !index.hasUnknown && index.unknowns != null && index.unknowns.Length == 0, "ResourceIndexUnproven", index == null ? "Missing index" : string.Join("; ", index.unknowns ?? new string[0])); }
        private static void VerifyFile(string root, string relative, string hash, string code)
        { string path = ShadowHash.SafeChild(root, relative); ShadowHash.Require(File.Exists(path) && ShadowHash.File(path) == hash, code, path); }
        public static string MetaGuid(string meta)
        { var match = Regex.Match(meta ?? "", @"(?m)^guid:\s*([0-9a-f]{32})\s*$"); ShadowHash.Require(match.Success, "ResourceSourceGuidMissing", "Source meta has no unique GUID."); return match.Groups[1].Value; }

        [Serializable] private sealed class HistoricalManifest { public int schemaVersion; public string baselineBuildId, unityVersion, target, architecture; public HistoricalBundle[] bundles; public HistoricalAssembly[] assemblies; }
        [Serializable] private sealed class HistoricalBundle { public string name, sha256; }
        [Serializable] private sealed class HistoricalAssembly { public string name, sha256; }
        [Serializable] private sealed class HistoricalAudit { public bool verified; public HistoricalAuditFile[] comparedFiles; }
        [Serializable] private sealed class HistoricalAuditFile { public string path, sha256; public bool matchesFrozen; }
    }

    /// <summary>Reads captured bytes only; never asks the current AssetDatabase to reinterpret old bundles.</summary>
    public sealed class FrozenResourceAssetReader : IResourceAssetReader
    {
        private readonly string root;
        private readonly Dictionary<string, ShadowResourceSource> sources;
        private readonly Dictionary<string, ShadowResourceScript> scripts;
        public FrozenResourceAssetReader(string root, IEnumerable<ShadowResourceSource> sources, IEnumerable<ShadowResourceScript> scripts)
        {
            this.root = root;
            this.sources = sources.ToDictionary(s => s.path, StringComparer.Ordinal);
            this.scripts = scripts.ToDictionary(s => s.guid + ":" + s.localId, StringComparer.Ordinal);
            foreach (var script in this.scripts.Values)
                ShadowHash.Require(this.sources.ContainsKey(script.path) && this.sources[script.path].guid == script.guid, "ResourceScriptIdentity", script.path);
        }
        public string[] GetDependencies(string path)
        { ShadowHash.Require(sources.ContainsKey(path), "ResourceSourceMissing", path); return sources[path].dependencies; }
        public ResourceAssetReferences Read(string path)
        {
            ShadowResourceSource source;
            ShadowHash.Require(sources.TryGetValue(path, out source), "ResourceSourceMissing", path);
            if (source.builtin) { ShadowResourceBaseline.VerifyBuiltinSource(root, source); return new ResourceAssetReferences { guid = source.guid }; }
            string text = File.ReadAllText(ShadowHash.SafeChild(root, source.snapshotPath));
            var parsed = UnitySerializedReferenceParser.Parse(text, (guid, id) => {
                ShadowResourceScript script;
                return scripts.TryGetValue(guid + ":" + id, out script) ? new ResourceTypeIdentity { assembly = script.assembly, @namespace = script.@namespace, type = script.type } : null;
            });
            parsed.guid = source.guid;
            return parsed;
        }
    }
}
