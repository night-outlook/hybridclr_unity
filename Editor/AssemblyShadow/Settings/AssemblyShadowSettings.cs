using System;
using System.IO;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace HybridCLR.Editor.AssemblyShadow
{
    /// <summary>
    /// Settings owned by Assembly Shadow.  This asset is deliberately separate
    /// from HybridCLRSettings: shadow candidates remain ordinary AOT input and
    /// must never be added to the normal hot-update list.
    /// </summary>
    public sealed class AssemblyShadowSettings : ScriptableObject
    {
        public bool enableAssemblyShadow;

        public AssemblyDefinitionAsset[] shadowAssemblyDefinitions = new AssemblyDefinitionAsset[0];
        public string[] shadowAssemblyNames = new string[0];
        public AssemblyDefinitionAsset[] bootstrapAssemblyDefinitions = new AssemblyDefinitionAsset[0];
        public string[] bootstrapAssemblyNames = new string[0];

        public string patchOutputRoot = "HybridCLRData/AssemblyShadow/Patches";
        public string baselineOutputRoot = "HybridCLRData/AssemblyShadow/Baselines";
        public string baselineManifestPath = "";
        public string buildId = "";
        public string currentCompileOutput = "";
        public string playerInputSnapshot = "";
        public string resourceBuildMapPath = "ProjectSettings/AssemblyShadowResources.json";
        public string resourceBaselinePath = "";
        public string patchId = "P01";
        public string[] targetReferenceDirectories = new string[0];
        public string architecture = "";
        public string sourcePinFile = "ProjectSettings/AssemblyShadowSourcePins.json";
        // Legacy shorthand: each name explicitly declares a shadow-capable plugin.
        public string[] precompiledAssemblyNames = new string[0];
        // Explicit true/false capability declarations for external runtime DLLs.
        // Classification is derived from compiler provenance, never from this field.
        public AssemblyCapability[] precompiledAssemblyCapabilities = new AssemblyCapability[0];

        // TextAsset fields preserve the task-02.1 authoring workflow.  Paths
        // are preferred for generated/project settings and have stable defaults.
        public TextAsset explicitDependencyConfig;
        public TextAsset extensibilityWhitelist;
        public string explicitDependencyConfigPath = "ProjectSettings/AssemblyShadowDependencies.json";
        public string extensibilityWhitelistPath = "ProjectSettings/AssemblyShadowExtensibilityWhitelist.json";
        public string[] allowedInternalEditorAssemblies = new string[0];

        public bool enforceResourceAbi = true;
        public bool rejectUnknownReflectionDependencies = true;
        public bool includePdbInDevelopmentPatch = true;

        private static AssemblyShadowSettings s_instance;
        private const string AssetPath = "ProjectSettings/AssemblyShadowSettings.asset";

        public static AssemblyShadowSettings Instance
        {
            get
            {
                if (!s_instance)
                    LoadOrCreate();
                return s_instance;
            }
        }

        public static AssemblyShadowSettings LoadOrCreate()
        {
            UnityEngine.Object[] objects = InternalEditorUtility.LoadSerializedFileAndForget(AssetPath);
            s_instance = objects != null && objects.Length > 0
                ? objects[0] as AssemblyShadowSettings
                : (s_instance ?? CreateInstance<AssemblyShadowSettings>());
            if (!s_instance)
                s_instance = CreateInstance<AssemblyShadowSettings>();
            s_instance.Normalize();
            return s_instance;
        }

        public static void Save()
        {
            if (!s_instance)
                return;

            s_instance.Normalize();
            string directory = Path.GetDirectoryName(AssetPath);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);
            InternalEditorUtility.SaveToSerializedFileAndForget(
                new UnityEngine.Object[] { s_instance }, AssetPath, true);
        }

        internal void Normalize()
        {
            shadowAssemblyDefinitions = shadowAssemblyDefinitions ?? new AssemblyDefinitionAsset[0];
            shadowAssemblyNames = shadowAssemblyNames ?? new string[0];
            bootstrapAssemblyDefinitions = bootstrapAssemblyDefinitions ?? new AssemblyDefinitionAsset[0];
            bootstrapAssemblyNames = bootstrapAssemblyNames ?? new string[0];
            targetReferenceDirectories = targetReferenceDirectories ?? new string[0];
            precompiledAssemblyNames = precompiledAssemblyNames ?? new string[0];
            precompiledAssemblyCapabilities = precompiledAssemblyCapabilities ?? new AssemblyCapability[0];
            allowedInternalEditorAssemblies = allowedInternalEditorAssemblies ?? new string[0];
            if (string.IsNullOrWhiteSpace(patchOutputRoot))
                patchOutputRoot = "HybridCLRData/AssemblyShadow/Patches";
            if (string.IsNullOrWhiteSpace(baselineOutputRoot))
                baselineOutputRoot = "HybridCLRData/AssemblyShadow/Baselines";
            if (string.IsNullOrWhiteSpace(sourcePinFile))
                sourcePinFile = "ProjectSettings/AssemblyShadowSourcePins.json";
            if (string.IsNullOrWhiteSpace(explicitDependencyConfigPath))
                explicitDependencyConfigPath = "ProjectSettings/AssemblyShadowDependencies.json";
            if (string.IsNullOrWhiteSpace(extensibilityWhitelistPath))
                extensibilityWhitelistPath = "ProjectSettings/AssemblyShadowExtensibilityWhitelist.json";
        }
    }
}
