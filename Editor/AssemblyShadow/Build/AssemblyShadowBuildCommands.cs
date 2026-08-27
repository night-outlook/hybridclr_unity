using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace HybridCLR.Editor.AssemblyShadow
{
    public static class AssemblyShadowBuildCommands
    {
        [MenuItem("HybridCLR/Assembly Shadow/Build Baseline Manifest", false, 500)]
        public static void BuildBaselineManifest()
        {
            var settings = RequireSettings();
            var session = ShadowBuildSession.Load();
            BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
            ShadowHash.Require(File.Exists(settings.resourceBuildMapPath), "ResourceMapMissing", settings.resourceBuildMapPath);
            string snapshot = Argument("-shadowPlayerSnapshot", string.IsNullOrEmpty(settings.playerInputSnapshot) ? session.playerInputSnapshot : settings.playerInputSnapshot);
            string buildId = AssemblySnapshot.ReadAndVerify(snapshot, true).buildId;
            string destination = Path.Combine(settings.baselineOutputRoot, target.ToString(), buildId);
            var result = ShadowBaselineManifestBuilder.Build(new ShadowBaselineBuildRequest
            {
                playerInputSnapshot = snapshot, outputDirectory = destination, buildId = buildId,
                resourceBaselinePath = string.IsNullOrEmpty(settings.resourceBaselinePath) ? session.resourceBaselinePath : settings.resourceBaselinePath,
                target = target, architecture = settings.architecture, sourcePins = ShadowSourcePins.Read(settings.sourcePinFile, target, settings.architecture),
                policy = AssemblyShadowSettingsUtil.CreatePolicyConfiguration(target),
                resources = JsonUtility.FromJson<ShadowResourceBuildMap>(File.ReadAllText(settings.resourceBuildMapPath)),
            });
            session.baselineManifestPath = Path.Combine(destination, "baseline-manifest.json");
            session.baselineBuildId = buildId;
            session.Save();
            Debug.Log("[AssemblyShadow] Baseline manifest: " + session.baselineManifestPath + ", Player input snapshot " + result.playerInputSnapshotHash);
        }

        [MenuItem("HybridCLR/Assembly Shadow/Compile Patch Snapshot", false, 501)]
        public static void CompilePatchSnapshot()
        {
            var settings = RequireSettings();
            BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
            string define = Argument("-shadowDefine", "");
            var session = ShadowBuildSession.Load();
            session.currentCompileSnapshot = AssemblySnapshot.Compile(Path.Combine("HybridCLRData/AssemblyShadow/CompileSnapshots", Guid.NewGuid().ToString("N")),
                target, settings.architecture, ShadowSourcePins.Read(settings.sourcePinFile, target, settings.architecture),
                AssemblyShadowSettingsUtil.CreatePolicyConfiguration(target), string.IsNullOrWhiteSpace(define) ? new string[0] : new[] { define });
            session.Save();
            Debug.Log("[AssemblyShadow] Captured single compile output: " + session.currentCompileSnapshot);
        }

        [MenuItem("HybridCLR/Assembly Shadow/Build Baseline Resources", false, 499)]
        public static void BuildBaselineResources()
        {
            var settings = RequireSettings();
            BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
            var session = ShadowBuildSession.Load();
            session.resourceBaselinePath = ShadowResourceBaseline.Build(new ShadowResourceBuildRequest
            {
                outputDirectory = Path.Combine("HybridCLRData/AssemblyShadow/ResourceBaselines", target.ToString(), Guid.NewGuid().ToString("N")),
                target = target, architecture = settings.architecture,
                sourcePins = ShadowSourcePins.Read(settings.sourcePinFile, target, settings.architecture),
                policy = AssemblyShadowSettingsUtil.CreatePolicyConfiguration(target),
                resources = JsonUtility.FromJson<ShadowResourceBuildMap>(File.ReadAllText(settings.resourceBuildMapPath)),
            });
            session.Save();
            Debug.Log("[AssemblyShadow] Built and froze resource ABI/index/bundle evidence: " + session.resourceBaselinePath);
        }

        [MenuItem("HybridCLR/Assembly Shadow/Build Patch", false, 502)]
        public static void BuildPatch()
        {
            var settings = RequireSettings();
            var session = ShadowBuildSession.Load();
            BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
            string patchId = Argument("-shadowPatchId", settings.patchId);
            string output = Path.Combine(settings.patchOutputRoot, target.ToString(), patchId);
            var result = ShadowPatchManifestBuilder.Build(new ShadowPatchBuildRequest
            {
                baselineManifestPath = string.IsNullOrEmpty(settings.baselineManifestPath) ? session.baselineManifestPath : settings.baselineManifestPath,
                currentCompileSnapshot = string.IsNullOrEmpty(settings.currentCompileOutput) ? session.currentCompileSnapshot : settings.currentCompileOutput,
                outputDirectory = output, patchId = patchId, target = target, architecture = settings.architecture,
                sourcePins = ShadowSourcePins.Read(settings.sourcePinFile, target, settings.architecture), policy = AssemblyShadowSettingsUtil.CreatePolicyConfiguration(target),
                dllOnly = Argument("-shadowAllowResourceRebuild", "false") != "true", includePdb = settings.includePdbInDevelopmentPatch,
            });
            Debug.Log("[AssemblyShadow] Unsigned patch: " + output + ", closure=" + string.Join(", ", result.loadOrder));
        }

        private static AssemblyShadowSettings RequireSettings()
        {
            var settings = AssemblyShadowSettings.Instance;
            ShadowHash.Require(settings.enableAssemblyShadow, "FeatureDisabled", "Enable the independent Assembly Shadow settings first.");
            AssemblyShadowSettingsUtil.ValidateSettingsOrThrow(settings);
            return settings;
        }

        public static string Argument(string name, string fallback)
        {
            var args = Environment.GetCommandLineArgs();
            int index = Array.IndexOf(args, name);
            return index >= 0 && index + 1 < args.Length ? args[index + 1] : fallback;
        }
    }
}
