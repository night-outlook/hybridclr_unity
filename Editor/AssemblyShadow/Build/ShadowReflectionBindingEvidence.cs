using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dnlib.DotNet;
using HybridCLR.AssemblyShadow.CodeGen;

namespace HybridCLR.Editor.AssemblyShadow
{
    [Serializable]
    public sealed class ShadowReflectionBindingDeclaration
    {
        public string id;
        public string consumer;
        public string typeName;
        public string methodSignature;
        public string originalMethodHash;
        public int operationIndex;
        public string[] allowedTypes;
        public string[] providers;
        public string reason;
        public string kind;
        public string imageSha256;
        public string providerAssemblyIdentity;
        public string imagePath;
    }

    // The control define is part of Unity's compiler cache key and the existing
    // snapshot hash. It binds these exact configuration bytes; every snapshot
    // reader also verifies the emitted guard bodies, including linked output.
    public static class ShadowReflectionBindingEvidence
    {
        public const string DirectoryName = "ReflectionBindings";
        public const string ConfigurationPath = DirectoryName + "/configuration.json";

        public static string[] CompilationDefines(IEnumerable<string> userDefines)
        {
            string[] supplied = (userDefines ?? new string[0]).ToArray();
            ShadowHash.Require(!supplied.Any(IsControlDefine), "ReservedCompilerDefine", "Reflection binding control defines are generated from the configuration, not caller supplied.");
            string path = ReflectionBindingConfiguration.ProjectRelativePath;
            if (!File.Exists(path)) return ShadowHash.Sorted(supplied);
            byte[] bytes = File.ReadAllBytes(path);
            Parse(bytes);
            return ShadowHash.Sorted(supplied.Concat(new[] { ReflectionBindingDefines.Create(bytes) }));
        }

        public static string[] UserDefines(IEnumerable<string> compilationDefines)
        {
            string[] defines = (compilationDefines ?? new string[0]).ToArray();
            string ignored;
            ReflectionBindingDefines.TryGetEnabledHash(defines, out ignored);
            return defines.Where(value => !IsControlDefine(value)).ToArray();
        }

        public static void ValidateProjectImages()
        {
            string path = ReflectionBindingConfiguration.ProjectRelativePath;
            ShadowHash.Require(File.Exists(path), "ReflectionBindingConfigurationMissing", path);
            ReadProjectImages(Parse(File.ReadAllBytes(path)));
        }

        public static ShadowPolicyConfiguration DeclareProject(ShadowPolicyConfiguration policy)
        {
            string path = ReflectionBindingConfiguration.ProjectRelativePath;
            if (!File.Exists(path)) return policy;
            return Declare(policy, File.ReadAllBytes(path));
        }

        public static ShadowPolicyConfiguration Declare(ShadowPolicyConfiguration policy, byte[] bytes)
        {
            ShadowHash.Require(policy != null && policy.dependencies != null, "ReflectionBindingPolicyMissing", "Finite binding contracts require an explicit dependency policy.");
            var configuration = Parse(bytes);
            policy.reflectionBindingConfigurationSha256 = ShadowHash.Bytes(bytes);
            policy.reflectionBindingConfigurationHash = configuration.ComputeHash();
            policy.reflectionBindings = Declarations(configuration);
            return policy;
        }

        public static void AddCompiledDependencies(ShadowPolicyConfiguration policy, IEnumerable<AssemblyDescriptor> descriptors)
        {
            var byName = descriptors.ToDictionary(item => AssemblyIdentityUtil.CanonicalName(item.name), StringComparer.Ordinal);
            var dependencies = new List<DeclaredRuntimeDependency>(policy.dependencies.runtimeDependencies ?? new DeclaredRuntimeDependency[0]);
            foreach (var binding in policy.reflectionBindings ?? new ShadowReflectionBindingDeclaration[0])
            foreach (string provider in binding.providers)
            {
                if (AssemblyIdentityUtil.CanonicalName(binding.consumer) == AssemblyIdentityUtil.CanonicalName(provider)) continue;
                AssemblyDescriptor consumer;
                ShadowHash.Require(byName.TryGetValue(AssemblyIdentityUtil.CanonicalName(binding.consumer), out consumer), "ReflectionBindingInputMissing", binding.consumer);
                if ((consumer.references ?? new string[0]).Any(reference => AssemblyIdentityUtil.CanonicalName(reference) == AssemblyIdentityUtil.CanonicalName(provider))) continue;
                if (dependencies.Any(edge => edge != null && AssemblyIdentityUtil.CanonicalName(edge.consumer) == AssemblyIdentityUtil.CanonicalName(binding.consumer) &&
                    AssemblyIdentityUtil.CanonicalName(edge.provider) == AssemblyIdentityUtil.CanonicalName(provider))) continue;
                dependencies.Add(new DeclaredRuntimeDependency
                {
                    consumer = binding.consumer, provider = provider, kind = "EnforcedFiniteReflectionBinding",
                    evidence = "Finite target contract " + binding.id + "; configuration " + policy.reflectionBindingConfigurationHash,
                    // Do not waive an untransformed dynamic call. Generated
                    // calls use literal targets and ordinary dependency checks.
                    callSite = null,
                });
            }
            policy.dependencies.runtimeDependencies = dependencies.ToArray();
        }

        public static void RequireProjectDefines(IEnumerable<string> defines)
        {
            string[] actual = (defines ?? new string[0]).ToArray();
            string[] expected = CompilationDefines(UserDefines(actual));
            ShadowHash.Require(ShadowHash.Sorted(actual).SequenceEqual(expected, StringComparer.Ordinal), "ReflectionBindingConfigurationChanged",
                "Player compilation defines must bind the current finite reflection configuration.");
        }

        public static void Capture(string root, AssemblySnapshotReceipt receipt)
        {
            string expectedHash;
            if (!ReflectionBindingDefines.TryGetEnabledHash(receipt.extraScriptingDefines, out expectedHash)) return;
            string path = ReflectionBindingConfiguration.ProjectRelativePath;
            ShadowHash.Require(File.Exists(path), "ReflectionBindingConfigurationMissing", path);
            byte[] bytes = File.ReadAllBytes(path);
            ShadowHash.Require(ShadowHash.Bytes(bytes) == expectedHash, "ReflectionBindingConfigurationChanged", "Configuration changed during compilation.");
            var configuration = Parse(bytes);
            Directory.CreateDirectory(Path.Combine(root, DirectoryName));
            var images = ReadProjectImages(configuration);
            foreach (var site in FixedImageSites(configuration))
            {
                string destination = ShadowHash.SafeChild(root, FixedImageSnapshotPath(site.imageSha256));
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.WriteAllBytes(destination, images[site.imagePath]);
            }
            VerifyInputs(root, receipt, configuration, false);
            File.WriteAllBytes(ShadowHash.SafeChild(root, ConfigurationPath), bytes);
        }

        public static ReflectionBindingConfiguration ReadAndVerify(string root, AssemblySnapshotReceipt receipt, bool requireLinked)
        {
            string expectedHash;
            if (!ReflectionBindingDefines.TryGetEnabledHash(receipt.extraScriptingDefines, out expectedHash))
            {
                ShadowHash.Require(!Directory.Exists(Path.Combine(root, DirectoryName)), "UnexpectedReflectionBindingEvidence",
                    "A snapshot without a binding control define cannot contain binding evidence.");
                ShadowHash.Require(receipt.linkedPlayerReceipt == null || (receipt.linkedPlayerReceipt.schemaVersion != 2 &&
                    string.IsNullOrEmpty(receipt.linkedPlayerReceipt.reflectionBindingEvidenceHash)), "UnexpectedReflectionBindingEvidence",
                    "A snapshot without a binding control define cannot claim linked binding evidence.");
                return null;
            }
            string path = ShadowHash.SafeChild(root, ConfigurationPath);
            ShadowHash.Require(File.Exists(path), "ReflectionBindingConfigurationMissing", path);
            byte[] bytes = File.ReadAllBytes(path);
            ShadowHash.Require(ShadowHash.Bytes(bytes) == expectedHash, "ReflectionBindingConfigurationChanged", path);
            var configuration = Parse(bytes);
            var expected = new HashSet<string>(StringComparer.Ordinal) { Path.GetFullPath(path) };
            foreach (var site in FixedImageSites(configuration))
                expected.Add(ShadowHash.SafeChild(root, FixedImageSnapshotPath(site.imageSha256)));
            if (requireLinked)
            {
                expected.Add(ShadowHash.SafeChild(root, ShadowReflectionBindingLinkedEvidence.ReceiptPath));
                expected.Add(ShadowHash.SafeChild(root, ShadowReflectionBindingLinkedEvidence.FacadePath));
            }
            ShadowHash.Require(expected.SetEquals(Directory.GetFiles(Path.Combine(root, DirectoryName), "*", SearchOption.AllDirectories).Select(Path.GetFullPath)),
                "UnexpectedReflectionBindingEvidence", "Binding evidence contains undeclared or missing files.");
            VerifyInputs(root, receipt, configuration, requireLinked);
            return configuration;
        }

        public static void RequirePolicy(ShadowPolicyConfiguration policy, string root, AssemblySnapshotReceipt receipt, bool requireLinked)
        {
            var configuration = ReadAndVerify(root, receipt, requireLinked);
            string rawHash;
            ReflectionBindingDefines.TryGetEnabledHash(receipt.extraScriptingDefines, out rawHash);
            ShadowHash.Require((policy.reflectionBindingConfigurationSha256 ?? "") == (rawHash ?? "") &&
                (policy.reflectionBindingConfigurationHash ?? "") == (configuration == null ? "" : configuration.ComputeHash()),
                "ReflectionBindingPolicyMismatch", "Requested policy differs from the configuration verified in the compiled inputs.");
            var expected = configuration == null ? new ShadowReflectionBindingDeclaration[0] : Declarations(configuration);
            ShadowHash.Require(UnityEngine.JsonUtility.ToJson(new DeclarationList { entries = expected }) ==
                UnityEngine.JsonUtility.ToJson(new DeclarationList { entries = policy.reflectionBindings ?? new ShadowReflectionBindingDeclaration[0] }),
                "ReflectionBindingPolicyMismatch", "Declared reflection bindings do not match the verified compiler contract.");
        }

        public static ShadowPolicyValidationResult ValidateCompiled(CompiledAssemblySet set, ShadowPolicyConfiguration policy,
            string root, AssemblySnapshotReceipt receipt, bool requireLinked, VerifiedLinkedRuntimeReferences linkedRuntimeReferences = null)
        {
            RequirePolicy(policy, root, receipt, requireLinked);
            var configuration = ReadAndVerify(root, receipt, requireLinked);
            return ShadowAssemblyPolicyValidator.ValidateCompiled(set, policy, DateTime.UtcNow,
                configuration, ReadFixedImages(root, configuration), linkedRuntimeReferences);
        }

        public static void Copy(string source, string destination, AssemblySnapshotReceipt receipt)
        {
            string expectedHash;
            if (!ReflectionBindingDefines.TryGetEnabledHash(receipt.extraScriptingDefines, out expectedHash)) return;
            var configuration = ReadAndVerify(source, receipt, receipt.linkedPlayerReceipt != null);
            ShadowArtifactWriter.CopyVerified(ShadowHash.SafeChild(source, ConfigurationPath), destination, ConfigurationPath, expectedHash);
            foreach (var site in FixedImageSites(configuration).GroupBy(value => value.imageSha256, StringComparer.Ordinal).Select(group => group.First()))
            {
                string path = FixedImageSnapshotPath(site.imageSha256);
                ShadowArtifactWriter.CopyVerified(ShadowHash.SafeChild(source, path), destination, path, site.imageSha256);
            }
            ShadowReflectionBindingLinkedEvidence.Copy(source, destination, receipt);
        }

        // These are the actual dynamically loaded bytes, not a later compiler
        // output with a possibly different MVID. The raw configuration binds
        // their hashes; compiled-policy validation also proves that they have
        // the current NormalHotUpdate provider's complete semantics.
        public static IReadOnlyDictionary<string, byte[]> ReadFixedImages(string root, ReflectionBindingConfiguration configuration)
        {
            var images = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            if (configuration == null) return images;
            foreach (var site in FixedImageSites(configuration))
            {
                string path = ShadowHash.SafeChild(root, FixedImageSnapshotPath(site.imageSha256));
                ShadowHash.Require(File.Exists(path), "FixedAssemblyImageMissing", path);
                images[site.imagePath] = File.ReadAllBytes(path);
            }
            configuration.ValidateImageEvidence(images);
            return images;
        }

        public static string FixedImageSnapshotPath(string sha256)
        {
            ShadowHash.Require(sha256 != null && sha256.Length == 64 && sha256.All(value =>
                (value >= '0' && value <= '9') || (value >= 'a' && value <= 'f')), "FixedAssemblyImageHash", "Expected a lowercase SHA-256.");
            return DirectoryName + "/Images/" + sha256 + ".dll.bytes";
        }

        private static IReadOnlyDictionary<string, byte[]> ReadProjectImages(ReflectionBindingConfiguration configuration)
        {
            var images = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var site in FixedImageSites(configuration))
            {
                string path = ShadowHash.SafeChild(Directory.GetCurrentDirectory(), site.imagePath);
                ShadowHash.Require(File.Exists(path), "FixedAssemblyImageMissing", path);
                images[site.imagePath] = File.ReadAllBytes(path);
            }
            configuration.ValidateImageEvidence(images);
            return images;
        }

        private static IEnumerable<ReflectionBindingSite> FixedImageSites(ReflectionBindingConfiguration configuration)
        {
            return configuration.sites.Where(site => site.kind == "FixedAssemblyBytes");
        }

        private static void VerifyInputs(string root, AssemblySnapshotReceipt receipt, ReflectionBindingConfiguration configuration, bool requireLinked)
        {
            ReadFixedImages(root, configuration);
            foreach (var group in configuration.sites.GroupBy(site => AssemblyIdentityUtil.CanonicalName(site.assembly)))
            {
                var inputs = (receipt.assemblies ?? new SnapshotFile[0]).Where(file => AssemblyIdentityUtil.CanonicalName(file.name) == group.Key).ToArray();
                ShadowHash.Require(inputs.Length == 1, "ReflectionBindingInputMissing", "Exactly one unfiltered compiler input must contain " + group.Key);
                VerifyModule(ShadowHash.SafeChild(root, inputs[0].path), configuration, group.Count());
            }
            if (requireLinked) ShadowReflectionBindingLinkedEvidence.ReadAndVerify(root, receipt, configuration);
        }

        private static void VerifyModule(string path, ReflectionBindingConfiguration configuration, int expectedSites)
        {
            using (var module = ModuleDefMD.Load(path))
            {
                var verified = ReflectionBindingTransformer.Verify(module, configuration);
                ShadowHash.Require(verified.Length == expectedSites, "ReflectionBindingSiteMismatch", path);
            }
        }

        private static ReflectionBindingConfiguration Parse(byte[] bytes)
        {
            var configuration = ReflectionBindingConfiguration.Parse(bytes);
            configuration.Validate();
            return configuration;
        }

        private static ShadowReflectionBindingDeclaration[] Declarations(ReflectionBindingConfiguration configuration)
        {
            return configuration.sites.OrderBy(site => site.id, StringComparer.Ordinal).Select(site => new ShadowReflectionBindingDeclaration
            {
                id = site.id, consumer = site.assembly, typeName = site.typeName, methodSignature = site.methodSignature,
                originalMethodHash = site.originalMethodHash, operationIndex = site.operationIndex,
                allowedTypes = ShadowHash.Sorted(site.allowedTypes), reason = site.reason,
                kind = configuration.schemaVersion == 1 ? null : site.kind,
                imageSha256 = site.imageSha256, providerAssemblyIdentity = site.providerAssemblyIdentity, imagePath = site.imagePath,
                // Configuration validation accepts only concrete, assembly-qualified
                // names. Generic/array/pointer/byref syntax is rejected by CodeGen.
                providers = site.kind == "FixedAssemblyBytes"
                    ? new[] { AssemblyIdentityUtil.CanonicalName(site.providerAssemblyIdentity.Split(',')[0]) }
                    : ShadowHash.Sorted(site.allowedTypes.Select(value => AssemblyIdentityUtil.CanonicalName(value.Substring(value.IndexOf(',') + 1).Split(',')[0].Trim()))),
            }).ToArray();
        }

        private static bool IsControlDefine(string value)
        {
            return value != null && value.StartsWith(ReflectionBindingDefines.Prefix, StringComparison.Ordinal);
        }

        [Serializable] private sealed class DeclarationList { public ShadowReflectionBindingDeclaration[] entries; }
    }
}
