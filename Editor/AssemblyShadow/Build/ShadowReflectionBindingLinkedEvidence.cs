using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using dnlib.DotNet;
using HybridCLR.AssemblyShadow.CodeGen;
using UnityEditor;
using UnityEngine;

namespace HybridCLR.Editor.AssemblyShadow
{
    [Serializable]
    public sealed class ReflectionBindingForwarder
    {
        public string typeFullName, destinationAssemblyIdentity;
    }

    [Serializable]
    public sealed class ReflectionBindingFrameworkFile
    {
        public string assemblyIdentity, path, sha256, mvid;
    }

    [Serializable]
    public sealed class ReflectionBindingLinkedSite
    {
        public string id, consumer, compiledPath, compiledSha256, linkedPath, linkedSha256;
        public string methodSignature, guardMethod;
        public int operationIndex;
        public string compiledMethodHash, linkedMethodHash, compiledGuardHash, linkedGuardHash;
    }

    [Serializable]
    public sealed class ReflectionBindingLinkedReceipt
    {
        public int schemaVersion = 1;
        public int mappingPolicyVersion = 1;
        public string unityVersion, target, architecture, buildGuid, il2cppDotNetProfile;
        public string configurationSha256, configurationHash;
        public string facadeSourcePath, facadePath, facadeSha256, sourceAssemblyIdentity, profileHash;
        public ReflectionBindingForwarder[] forwarders;
        public ReflectionBindingFrameworkFile[] runtimeFrameworkModules;
        public ReflectionBindingLinkedSite[] sites;
    }

    // This is a separate post-link proof, not a relaxation of compiler identity.
    // Its file hash is part of linked receipt schema 2 and therefore the Player
    // snapshot/manifest hash. Runtime framework bytes stay in LinkedPlayer.
    public static class ShadowReflectionBindingLinkedEvidence
    {
        public const string DirectoryName = "ReflectionBindings/LinkedRetargeting";
        public const string ReceiptPath = DirectoryName + "/evidence.json";
        public const string FacadePath = DirectoryName + "/netstandard.dll.bytes";

        public static string Capture(string root, AssemblySnapshotReceipt player)
        {
            string configurationSha;
            if (!ReflectionBindingDefines.TryGetEnabledHash(player.extraScriptingDefines, out configurationSha)) return null;
            var configuration = ShadowReflectionBindingEvidence.ReadAndVerify(root, player, false);
            string runtimeProfile;
            string source = RuntimeFacadePath((BuildTarget)Enum.Parse(typeof(BuildTarget), player.target), out runtimeProfile);
            ShadowHash.Require(player.unityVersion == Application.unityVersion && File.Exists(source), "RetargetingFacadeMissing", source);
            byte[] facade = File.ReadAllBytes(source);
            string destination = ShadowHash.SafeChild(root, FacadePath);
            ShadowHash.Require(!Directory.Exists(Path.Combine(root, DirectoryName)), "RetargetingEvidenceExists", destination);
            Directory.CreateDirectory(Path.GetDirectoryName(destination));
            File.WriteAllBytes(destination, facade);
            var receipt = Derive(root, player, configuration, runtimeProfile, Path.GetFullPath(source), facade);
            File.WriteAllText(ShadowHash.SafeChild(root, ReceiptPath), JsonUtility.ToJson(receipt, true), new UTF8Encoding(false));
            return ShadowHash.File(ShadowHash.SafeChild(root, ReceiptPath));
        }

        public static void ReadAndVerify(string root, AssemblySnapshotReceipt player, ReflectionBindingConfiguration configuration)
        {
            ShadowHash.Require(player.linkedPlayerReceipt != null && player.linkedPlayerReceipt.schemaVersion == 2 &&
                !string.IsNullOrEmpty(player.linkedPlayerReceipt.reflectionBindingEvidenceHash), "ReflectionBindingLinkedEvidenceMissing", root);
            string path = ShadowHash.SafeChild(root, ReceiptPath);
            ShadowHash.Require(File.Exists(path) && ShadowHash.File(path) == player.linkedPlayerReceipt.reflectionBindingEvidenceHash,
                "ReflectionBindingLinkedEvidenceChanged", path);
            var stored = JsonUtility.FromJson<ReflectionBindingLinkedReceipt>(File.ReadAllText(path));
            ShadowHash.Require(stored != null && stored.schemaVersion == 1 && stored.mappingPolicyVersion == CapturedReflectionRetargetingProfile.PolicyVersion &&
                stored.facadePath == FacadePath && !string.IsNullOrWhiteSpace(stored.facadeSourcePath) && Path.IsPathRooted(stored.facadeSourcePath),
                "ReflectionBindingLinkedEvidenceSchema", path);
            byte[] facade = File.ReadAllBytes(ShadowHash.SafeChild(root, FacadePath));
            ShadowHash.Require(ShadowHash.Bytes(facade) == stored.facadeSha256, "RetargetingFacadeHashMismatch", FacadePath);
            var derived = Derive(root, player, configuration, stored.il2cppDotNetProfile, stored.facadeSourcePath, facade);
            ShadowHash.Require(JsonUtility.ToJson(stored) == JsonUtility.ToJson(derived), "ReflectionBindingLinkedEvidenceChanged",
                "Linked binding evidence does not match fresh verification of its captured compiler, facade and linked bytes.");
        }

        public static void Copy(string source, string destination, AssemblySnapshotReceipt receipt)
        {
            if (receipt.linkedPlayerReceipt == null || string.IsNullOrEmpty(receipt.linkedPlayerReceipt.reflectionBindingEvidenceHash)) return;
            string proof = ShadowHash.SafeChild(source, ReceiptPath);
            var evidence = JsonUtility.FromJson<ReflectionBindingLinkedReceipt>(File.ReadAllText(proof));
            ShadowHash.Require(evidence != null && evidence.facadePath == FacadePath, "ReflectionBindingLinkedEvidenceSchema", proof);
            ShadowArtifactWriter.CopyVerified(proof, destination, ReceiptPath, receipt.linkedPlayerReceipt.reflectionBindingEvidenceHash);
            ShadowArtifactWriter.CopyVerified(ShadowHash.SafeChild(source, FacadePath), destination, FacadePath, evidence.facadeSha256);
        }

        private static ReflectionBindingLinkedReceipt Derive(string root, AssemblySnapshotReceipt player,
            ReflectionBindingConfiguration configuration, string runtimeProfile, string sourceFacadePath, byte[] facade)
        {
            ShadowHash.Require(configuration != null && player.linkedPlayerReceipt != null, "ReflectionBindingLinkedEvidenceMissing", root);
            ShadowHash.Require(ValidProfile(runtimeProfile) && Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(sourceFacadePath))) == runtimeProfile,
                "RetargetingProfileMismatch", "The captured facade path must belong to the recorded target-aware IL2CPP profile.");
            string rawHash;
            ShadowHash.Require(ReflectionBindingDefines.TryGetEnabledHash(player.extraScriptingDefines, out rawHash),
                "ReflectionBindingLinkedEvidenceUnexpected", root);
            var frameworks = RuntimeFrameworkFiles(root, player, facade);
            var profile = CapturedReflectionRetargetingProfile.Load(facade, ShadowHash.Bytes(facade),
                frameworks.Select(file => File.ReadAllBytes(ShadowHash.SafeChild(root, file.path))));
            var sites = new List<ReflectionBindingLinkedSite>();
            foreach (var group in configuration.sites.GroupBy(site => AssemblyIdentityUtil.CanonicalName(site.assembly)).OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                var inputs = player.assemblies.Where(file => AssemblyIdentityUtil.CanonicalName(file.name) == group.Key).ToArray();
                var linked = player.linkedPlayerReceipt.assemblies.Where(file => AssemblyIdentityUtil.CanonicalName(file.name) == group.Key).ToArray();
                ShadowHash.Require(inputs.Length == 1 && linked.Length == 1, "ReflectionBindingLinkedEvidenceMissing", group.Key);
                string compiledPath = inputs[0].path;
                string linkedPath = ShadowLinkedPlayerEvidence.DirectoryName + "/" + linked[0].path;
                ShadowHash.Require(ShadowHash.File(ShadowHash.SafeChild(root, compiledPath)) == inputs[0].sha256 &&
                    ShadowHash.File(ShadowHash.SafeChild(root, linkedPath)) == linked[0].sha256, "ReflectionBindingInputChanged", group.Key);
                using (var compiledModule = ModuleDefMD.Load(ShadowHash.SafeChild(root, compiledPath)))
                using (var linkedModule = ModuleDefMD.Load(ShadowHash.SafeChild(root, linkedPath)))
                {
                    var verified = ReflectionBindingTransformer.VerifyLinked(compiledModule, linkedModule, configuration, profile);
                    ShadowHash.Require(verified.Length == group.Count(), "ReflectionBindingSiteMismatch", group.Key);
                    foreach (var site in verified)
                    {
                        ShadowHash.Require(site.LinkedProfileHash == profile.ComputeHash(), "RetargetingProfileMismatch", site.SiteId);
                        sites.Add(new ReflectionBindingLinkedSite
                        {
                            id = site.SiteId, consumer = site.Assembly, compiledPath = compiledPath, compiledSha256 = inputs[0].sha256,
                            linkedPath = linkedPath, linkedSha256 = linked[0].sha256, methodSignature = site.MethodSignature,
                            guardMethod = site.GuardMethod.Name.String, operationIndex = site.OperationIndex,
                            compiledMethodHash = site.CompiledMethodHash, linkedMethodHash = site.LinkedMethodHash,
                            compiledGuardHash = site.CompiledGuardHash, linkedGuardHash = site.LinkedGuardHash,
                        });
                    }
                }
            }
            var runtime = profile.RuntimeModules.OrderBy(module => module.AssemblyIdentity, StringComparer.Ordinal).Select(module =>
            {
                var file = frameworks.Single(value => value.assemblyIdentity == module.AssemblyIdentity);
                ShadowHash.Require(module.Sha256 == file.sha256 && module.Mvid == file.mvid, "RetargetingFrameworkChanged", file.path);
                return file;
            }).ToArray();
            return new ReflectionBindingLinkedReceipt
            {
                mappingPolicyVersion = CapturedReflectionRetargetingProfile.PolicyVersion,
                unityVersion = player.unityVersion, target = player.target, architecture = player.architecture, buildGuid = player.buildGuid,
                il2cppDotNetProfile = runtimeProfile,
                configurationSha256 = rawHash, configurationHash = configuration.ComputeHash(),
                facadeSourcePath = sourceFacadePath, facadePath = FacadePath, facadeSha256 = profile.FacadeSha256,
                sourceAssemblyIdentity = profile.SourceAssemblyIdentity, profileHash = profile.ComputeHash(),
                forwarders = profile.Forwarders.OrderBy(item => item.TypeFullName, StringComparer.Ordinal).Select(item => new ReflectionBindingForwarder
                { typeFullName = item.TypeFullName, destinationAssemblyIdentity = item.DestinationAssemblyIdentity }).ToArray(),
                runtimeFrameworkModules = runtime, sites = sites.OrderBy(site => site.id, StringComparer.Ordinal).ToArray(),
            };
        }

        private static ReflectionBindingFrameworkFile[] RuntimeFrameworkFiles(string root, AssemblySnapshotReceipt player, byte[] facadeBytes)
        {
            HashSet<string> destinationIdentities;
            using (var facade = ModuleDefMD.Load(facadeBytes))
                destinationIdentities = new HashSet<string>(facade.GetAssemblyRefs().Select(reference => reference.FullName), StringComparer.Ordinal);
            var files = new List<ReflectionBindingFrameworkFile>();
            foreach (var linked in player.linkedPlayerReceipt.assemblies)
            {
                string path = ShadowLinkedPlayerEvidence.DirectoryName + "/" + linked.path;
                using (var module = ModuleDefMD.Load(ShadowHash.SafeChild(root, path)))
                {
                    if (module.Assembly == null || !destinationIdentities.Contains(module.Assembly.FullName)) continue;
                    ShadowHash.Require(ShadowHash.File(ShadowHash.SafeChild(root, path)) == linked.sha256 && module.Mvid.HasValue &&
                        module.Mvid.Value.ToString("D") == linked.mvid, "RetargetingFrameworkChanged", path);
                    files.Add(new ReflectionBindingFrameworkFile { assemblyIdentity = module.Assembly.FullName, path = path, sha256 = linked.sha256, mvid = linked.mvid });
                }
            }
            return files.OrderBy(file => file.assemblyIdentity, StringComparer.Ordinal).ToArray();
        }

        private static string RuntimeFacadePath(BuildTarget target, out string profile)
        {
            // This version-pinned target-aware native Editor API selects the
            // same IL2CPP profile as the actual linker. The public compilation
            // reference-directory API would return the wrong netstandard DLL.
            var namedTarget = UnityEditor.Build.NamedBuildTarget.FromBuildTargetGroup(BuildPipeline.GetBuildTargetGroup(target));
            ShadowHash.Require(PlayerSettings.GetScriptingBackend(namedTarget) == ScriptingImplementation.IL2CPP,
                "RetargetingProfileUnavailable", "Linked binding proof requires IL2CPP.");
            var type = typeof(BuildPipeline).Assembly.GetType("UnityEditorInternal.IL2CPPUtils");
            var method = type == null ? null : type.GetMethod("ApiCompatibilityLevelToDotNetProfileArgument",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null,
                new[] { typeof(ApiCompatibilityLevel), typeof(BuildTarget) }, null);
            ShadowHash.Require(method != null, "RetargetingProfileUnavailable", "This Unity version lacks the pinned target-aware IL2CPP profile API.");
            profile = (string)method.Invoke(null, new object[] { PlayerSettings.GetApiCompatibilityLevel(namedTarget), target });
            ShadowHash.Require(ValidProfile(profile), "RetargetingProfileUnavailable", "Unsupported IL2CPP runtime profile: " + profile);
            string path = Path.GetFullPath(Path.Combine(EditorApplication.applicationContentsPath,
                "MonoBleedingEdge/lib/mono/" + profile + "/Facades/netstandard.dll"));
            ShadowHash.Require(File.Exists(path), "RetargetingProfileUnavailable", "The selected target needs a verified runtime-facade location: " + path);
            return path;
        }

        private static bool ValidProfile(string profile)
        {
            return !string.IsNullOrEmpty(profile) && profile.StartsWith("unityaot-", StringComparison.Ordinal) &&
                profile.All(value => char.IsLetterOrDigit(value) || value == '-');
        }
    }
}
