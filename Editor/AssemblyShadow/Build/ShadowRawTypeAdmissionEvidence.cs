using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using dnlib.DotNet;
using HybridCLR.AssemblyShadow.CodeGen;
using UnityEngine;

namespace HybridCLR.Editor.AssemblyShadow
{
    [Serializable]
    public sealed class RawTypeAdmissionProofSite
    {
        public string id, consumerAssemblyIdentity, consumerPath, consumerSha256;
        public string providerAssemblyIdentity, providerPath, providerSha256, providerInventoryHash;
        public string declaringType, methodSignature;
        public int operationIndex;
        public string operationSignature, kind, typeName;
        public bool throwOnError, ignoreCase;
        public string compiledMethodHash, linkedMethodHash, compiledConsumerSha256;
    }

    [Serializable]
    public sealed class RawTypeAdmissionProofReceipt
    {
        public int schemaVersion = 1;
        public string policy = RawTypeAdmissionConfiguration.Policy;
        public string phase, configurationSha256, configurationHash, unityVersion, target, architecture;
        public string buildGuid, linkedPlayerReceiptHash, profileHash;
        public RawTypeAdmissionProofSite[] sites;
    }

    // Raw queries are observed, not transformed. Every proof field is derived
    // from config/compiler/linked bytes already bound by the snapshot. Policy
    // acceptance additionally validates actual consumer/provider roles; capture
    // of this metadata evidence alone does not authorize a runtime dependency.
    public static class ShadowRawTypeAdmissionEvidence
    {
        public const string DirectoryName = "RawTypeAdmissions";
        public const string ConfigurationPath = DirectoryName + "/configuration.json";
        public const string CompiledProofPath = DirectoryName + "/compiled-evidence.json";
        public const string LinkedProofPath = DirectoryName + "/linked-evidence.json";

        public static string[] CompilationDefines(IEnumerable<string> userDefines)
        {
            string[] supplied = (userDefines ?? new string[0]).ToArray();
            ShadowHash.Require(!supplied.Any(IsControlDefine), "ReservedCompilerDefine",
                "Raw type admission control defines are generated from configuration bytes, not caller supplied.");
            if (!File.Exists(RawTypeAdmissionConfiguration.ProjectRelativePath)) return ShadowHash.Sorted(supplied);
            byte[] bytes = File.ReadAllBytes(RawTypeAdmissionConfiguration.ProjectRelativePath);
            Parse(bytes);
            return ShadowHash.Sorted(supplied.Concat(new[] { RawTypeAdmissionDefines.Create(bytes) }));
        }

        public static string[] UserDefines(IEnumerable<string> compilationDefines)
        {
            string[] defines = (compilationDefines ?? new string[0]).ToArray();
            string ignored;
            RawTypeAdmissionDefines.TryGetEnabledHash(defines, out ignored);
            return defines.Where(value => !IsControlDefine(value)).ToArray();
        }

        public static void Capture(string root, AssemblySnapshotReceipt snapshot, bool? developmentBuild = null)
        {
            string expectedHash;
            if (!RawTypeAdmissionDefines.TryGetEnabledHash(snapshot.extraScriptingDefines, out expectedHash))
            {
                RequireAbsent(root);
                return;
            }
            string source = RawTypeAdmissionConfiguration.ProjectRelativePath;
            ShadowHash.Require(File.Exists(source), "RawTypeAdmissionConfigurationMissing", source);
            byte[] bytes = File.ReadAllBytes(source);
            ShadowHash.Require(ShadowHash.Bytes(bytes) == expectedHash, "RawTypeAdmissionConfigurationChanged", source);
            var configuration = Parse(bytes);
            string compilerMode = null;
            if (configuration.schemaVersion == 2)
            {
                ShadowHash.Require(developmentBuild.HasValue, "RawTypeAdmissionCompilerModeMissing",
                    "Schema 2 capture requires the actual CompilePlayerScripts or Player development mode.");
                compilerMode = RawTypeAdmissionConfiguration.CompilerMode(developmentBuild.Value);
            }
            var proof = Derive(root, snapshot, configuration, expectedHash, false, compilerMode);
            ShadowHash.Require(!Directory.Exists(Path.Combine(root, DirectoryName)), "RawTypeAdmissionEvidenceExists", root);
            Directory.CreateDirectory(Path.Combine(root, DirectoryName));
            WriteNew(ShadowHash.SafeChild(root, ConfigurationPath), bytes);
            WriteNew(ShadowHash.SafeChild(root, CompiledProofPath), ProofBytes(proof));
        }

        public static void CaptureLinked(string root, AssemblySnapshotReceipt player)
        {
            var configuration = ReadAndVerify(root, player, false);
            if (configuration == null) return;
            string rawHash;
            RawTypeAdmissionDefines.TryGetEnabledHash(player.extraScriptingDefines, out rawHash);
            var proof = Derive(root, player, configuration, rawHash, true, CompilerMode(root, player, configuration));
            WriteNew(ShadowHash.SafeChild(root, LinkedProofPath), ProofBytes(proof));
            ReadAndVerify(root, player, true);
        }

        public static RawTypeAdmissionConfiguration ReadAndVerify(string root, AssemblySnapshotReceipt snapshot, bool requireLinked)
        {
            string expectedHash;
            if (!RawTypeAdmissionDefines.TryGetEnabledHash(snapshot.extraScriptingDefines, out expectedHash))
            {
                RequireAbsent(root);
                return null;
            }
            string path = ShadowHash.SafeChild(root, ConfigurationPath);
            ShadowHash.Require(File.Exists(path), "RawTypeAdmissionConfigurationMissing", path);
            byte[] bytes = File.ReadAllBytes(path);
            ShadowHash.Require(ShadowHash.Bytes(bytes) == expectedHash, "RawTypeAdmissionConfigurationChanged", path);
            var configuration = Parse(bytes);
            string compilerMode = CompilerMode(root, snapshot, configuration);
            string[] relativePaths = requireLinked
                ? new[] { ConfigurationPath, CompiledProofPath, LinkedProofPath }
                : new[] { ConfigurationPath, CompiledProofPath };
            var expectedFiles = new HashSet<string>(relativePaths.Select(item => ShadowHash.SafeChild(root, item)), StringComparer.Ordinal);
            ShadowHash.Require(expectedFiles.SetEquals(Directory.GetFiles(Path.Combine(root, DirectoryName), "*", SearchOption.AllDirectories).Select(Path.GetFullPath)),
                "UnexpectedRawTypeAdmissionEvidence", "Raw type evidence contains undeclared or missing files.");
            RequireProof(root, CompiledProofPath, Derive(root, snapshot, configuration, expectedHash, false, compilerMode));
            if (requireLinked) RequireProof(root, LinkedProofPath, Derive(root, snapshot, configuration, expectedHash, true, compilerMode));
            return configuration;
        }

        public static void Copy(string source, string destination, AssemblySnapshotReceipt snapshot)
        {
            bool linked = !ShadowLinkedPlayerEvidence.IsAbsent(snapshot);
            var configuration = ReadAndVerify(source, snapshot, linked);
            if (configuration == null) return;
            foreach (string path in linked ? new[] { ConfigurationPath, CompiledProofPath, LinkedProofPath } : new[] { ConfigurationPath, CompiledProofPath })
            {
                string input = ShadowHash.SafeChild(source, path);
                ShadowArtifactWriter.CopyVerified(input, destination, path, ShadowHash.File(input));
            }
        }

        private static RawTypeAdmissionProofReceipt Derive(string root, AssemblySnapshotReceipt snapshot,
            RawTypeAdmissionConfiguration configuration, string rawHash, bool linked, string compilerMode)
        {
            using (var compiled = LoadModules(root, AssemblySnapshot.AllFiles(snapshot)))
            {
                CapturedReflectionRetargetingProfile profile = null;
                using (var actual = linked ? LoadLinkedModules(root, snapshot) : null)
                {
                    VerifiedRawTypeAdmission[] verified;
                    if (linked)
                    {
                        profile = ReadCapturedProfile(root, snapshot);
                        verified = RawTypeAdmissionVerifier.VerifyLinked(compiled.modules, actual.modules, configuration, profile, compilerMode);
                    }
                    else verified = RawTypeAdmissionVerifier.Verify(compiled.modules, configuration, compilerMode);
                    ShadowHash.Require(verified.Length == configuration.sites.Length, "RawTypeAdmissionSiteMismatch", root);
                    var active = linked ? actual : compiled;
                    var sites = verified.OrderBy(site => site.SiteId, StringComparer.Ordinal).Select(site =>
                    {
                        var declaration = configuration.sites.Single(item => item.id == site.SiteId);
                        string consumer = SimpleName(site.ConsumerAssemblyIdentity), provider = SimpleName(site.ProviderAssemblyIdentity);
                        SnapshotFile currentConsumer = active.files[consumer], currentProvider = active.files[provider];
                        SnapshotFile compilerConsumer = compiled.files[consumer];
                        return new RawTypeAdmissionProofSite
                        {
                            id = site.SiteId,
                            consumerAssemblyIdentity = site.ConsumerAssemblyIdentity,
                            consumerPath = currentConsumer.path, consumerSha256 = currentConsumer.sha256,
                            providerAssemblyIdentity = site.ProviderAssemblyIdentity,
                            providerPath = currentProvider.path, providerSha256 = currentProvider.sha256,
                            providerInventoryHash = site.ProviderInventoryHash,
                            declaringType = declaration.declaringType, methodSignature = declaration.methodSignature,
                            operationIndex = site.OperationIndex, operationSignature = declaration.operationSignature,
                            kind = site.Kind, typeName = declaration.typeName,
                            throwOnError = declaration.throwOnError, ignoreCase = declaration.ignoreCase,
                            compiledMethodHash = site.CompiledMethodHash,
                            linkedMethodHash = linked ? ReflectionBindingFingerprint.Compute(site.Method) : "",
                            compiledConsumerSha256 = compilerConsumer.sha256,
                        };
                    }).ToArray();
                    return new RawTypeAdmissionProofReceipt
                    {
                        policy = configuration.policy, phase = linked ? "Linked" : "Compiled", configurationSha256 = rawHash,
                        configurationHash = configuration.ComputeHash(), unityVersion = snapshot.unityVersion,
                        target = snapshot.target, architecture = snapshot.architecture,
                        buildGuid = linked ? snapshot.buildGuid : "",
                        linkedPlayerReceiptHash = linked ? snapshot.linkedPlayerReceiptHash : "",
                        profileHash = linked ? profile.ComputeHash() : "", sites = sites,
                    };
                }
            }
        }

        private static CapturedReflectionRetargetingProfile ReadCapturedProfile(string root, AssemblySnapshotReceipt player)
        {
            // This dependency is already byte-bound by linked receipt schema 2.
            // Do not create a new unbound facade copy or read the host's current
            // framework when replaying a historical snapshot.
            var configuration = ShadowReflectionBindingEvidence.ReadAndVerify(root, player, true);
            ShadowHash.Require(configuration != null, "RawTypeAdmissionRetargetingMissing",
                "Raw-query linked verification requires the captured reflection retargeting proof.");
            string path = ShadowHash.SafeChild(root, ShadowReflectionBindingLinkedEvidence.ReceiptPath);
            var evidence = JsonUtility.FromJson<ReflectionBindingLinkedReceipt>(File.ReadAllText(path));
            var profile = CapturedReflectionRetargetingProfile.Load(
                File.ReadAllBytes(ShadowHash.SafeChild(root, evidence.facadePath)), evidence.facadeSha256,
                evidence.runtimeFrameworkModules.Select(file => File.ReadAllBytes(ShadowHash.SafeChild(root, file.path))));
            ShadowHash.Require(profile.ComputeHash() == evidence.profileHash, "RawTypeAdmissionRetargetingChanged", path);
            return profile;
        }

        private static LoadedModules LoadLinkedModules(string root, AssemblySnapshotReceipt player)
        {
            ShadowLinkedPlayerEvidence.ReadAndVerify(root, player);
            return LoadModules(root, player.linkedPlayerReceipt.assemblies.Select(file => new SnapshotFile
            {
                name = file.name, path = ShadowLinkedPlayerEvidence.DirectoryName + "/" + file.path, sha256 = file.sha256,
            }));
        }

        private static LoadedModules LoadModules(string root, IEnumerable<SnapshotFile> files)
        {
            var result = new LoadedModules();
            try
            {
                foreach (var file in files)
                {
                    string name = AssemblyIdentityUtil.CanonicalName(file.name);
                    ShadowHash.Require(!result.modules.ContainsKey(name), "RawTypeAdmissionDuplicateInput", name);
                    byte[] bytes = File.ReadAllBytes(ShadowHash.SafeChild(root, file.path));
                    ShadowHash.Require(ShadowHash.Bytes(bytes) == file.sha256, "RawTypeAdmissionInputChanged", file.path);
                    var module = ModuleDefMD.Load(bytes);
                    result.modules.Add(name, module);
                    ShadowHash.Require(module.Assembly != null && AssemblyIdentityUtil.CanonicalName(module.Assembly.Name.String) == name,
                        "RawTypeAdmissionInputIdentity", file.path);
                    result.files.Add(name, file);
                }
                return result;
            }
            catch { result.Dispose(); throw; }
        }

        private sealed class LoadedModules : IDisposable
        {
            internal readonly Dictionary<string, ModuleDefMD> modules = new Dictionary<string, ModuleDefMD>(StringComparer.Ordinal);
            internal readonly Dictionary<string, SnapshotFile> files = new Dictionary<string, SnapshotFile>(StringComparer.Ordinal);
            public void Dispose() { foreach (var module in modules.Values) module.Dispose(); }
        }

        private static RawTypeAdmissionConfiguration Parse(byte[] bytes)
        {
            var configuration = RawTypeAdmissionConfiguration.Parse(bytes);
            configuration.Validate();
            return configuration;
        }

        internal static string CompilerMode(string root, AssemblySnapshotReceipt snapshot, RawTypeAdmissionConfiguration configuration)
        {
            if (configuration == null || configuration.schemaVersion == 1) return null;
            return ShadowCompilerModeEvidence.CompilerMode(root, snapshot);
        }

        private static void RequireAbsent(string root)
        {
            ShadowHash.Require(!Directory.Exists(Path.Combine(root, DirectoryName)), "UnexpectedRawTypeAdmissionEvidence",
                "A snapshot without a raw type admission control cannot contain raw admission evidence.");
        }

        private static void RequireProof(string root, string relative, RawTypeAdmissionProofReceipt derived)
        {
            string path = ShadowHash.SafeChild(root, relative);
            ShadowHash.Require(File.Exists(path) && File.ReadAllBytes(path).SequenceEqual(ProofBytes(derived)),
                "RawTypeAdmissionProofChanged", "Raw proof must exactly equal fresh derivation from captured bytes: " + relative);
        }

        private static byte[] ProofBytes(RawTypeAdmissionProofReceipt proof)
        { return new UTF8Encoding(false).GetBytes(JsonUtility.ToJson(proof, true)); }

        private static void WriteNew(string path, byte[] bytes)
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                stream.Write(bytes, 0, bytes.Length);
        }

        private static bool IsControlDefine(string value)
        { return value != null && value.StartsWith(RawTypeAdmissionDefines.Prefix, StringComparison.Ordinal); }

        private static string SimpleName(string identity)
        { return AssemblyIdentityUtil.CanonicalName(identity.Split(',')[0].Trim()); }
    }
}
