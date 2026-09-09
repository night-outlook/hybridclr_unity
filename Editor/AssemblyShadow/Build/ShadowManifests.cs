using System;
using UnityEditor;

namespace HybridCLR.Editor.AssemblyShadow
{
    [Serializable]
    public sealed class ShadowBundleDefinition
    {
        public string name;
        public string[] assets;
    }

    [Serializable]
    public sealed class ShadowResourceBuildMap
    {
        public int schemaVersion = 1;
        public string bundleDirectory;
        public ShadowBundleDefinition[] bundles = new ShadowBundleDefinition[0];
    }

    [Serializable]
    public sealed class ShadowBundleArtifact
    {
        public string name;
        public string sha256;
        public string[] assets;
    }

    [Serializable]
    public sealed class ShadowBaselineManifest
    {
        public int schemaVersion = 1;
        public int semanticHashSchema = 1;
        public string baselineBuildId;
        public string unityVersion;
        public string target;
        public string architecture;
        public ShadowSourcePins sourcePins;
        public string runtimeAbiHash;
        public string[] shadowCandidates;
        public string[] bootstrapAssemblies;
        public string bootstrapAbiHash;
        public string resourceAbiHash;
        public string resourceIndexHash;
        public string resourceBaselinePath = "ResourceInputs";
        public string resourceBuildReceiptHash;
        public string policyHash;
        public string reflectionBindingConfigurationSha256;
        public string reflectionBindingConfigurationHash;
        public ShadowReflectionBindingDeclaration[] reflectionBindings = new ShadowReflectionBindingDeclaration[0];
        public string playerInputSnapshot = "PlayerInputs";
        public string playerInputSnapshotHash;
        public string playerBuildGuid;
        public string nativeLibrarySha256;
        public int nativeBudgetCapabilityVersion;
        public MetadataEncodingProfile metadataEncodingProfile;
        public MetadataCapacityReport metadataCapacityReport;
        // Profile 1 remains readable for historical baselines. Profile 2 is
        // deliberately represented by distinct DTOs so a sparse-page report
        // cannot be mistaken for the legacy cursor allocator.
        public MetadataEncodingProfile2 metadataEncodingProfile2;
        public MetadataCapacityProfile2PreliminaryReport metadataCapacityReport2;
        public AssemblyDescriptor[] assemblies;
        public AssemblyDependencyEdge[] dependencyGraph;
        public string[] deferredFacadeReferences = new string[0];
        public ShadowBundleArtifact[] bundles;
    }

    [Serializable]
    public sealed class ShadowPatchAssembly
    {
        public string name;
        public string dll;
        public string sha256;
        public string semanticHash;
        public string mvid;
        public string baselineMvid;
        public ulong dllSize;
        public string pdb;
        public string pdbSha256;
        public string[] references;
    }

    [Serializable]
    public sealed class ShadowPatchManifest
    {
        public int schemaVersion = 1;
        public int semanticHashSchema = 1;
        public string patchId;
        public string baselineBuildId;
        public string baselineManifestSha256;
        public string unityVersion;
        public string target;
        public string architecture;
        public ShadowSourcePins sourcePins;
        public string runtimeAbiHash;
        public string compileSnapshotHash;
        public string reflectionBindingConfigurationSha256;
        public string reflectionBindingConfigurationHash;
        public ShadowReflectionBindingDeclaration[] reflectionBindings = new ShadowReflectionBindingDeclaration[0];
        public string bootstrapAbiHash;
        public string baselineResourceAbiHash;
        public string resourceAbiHash;
        public string resourceChangeLevel;
        public bool dllOnly;
        public string[] resourceBundlesRequired;
        public string[] resourceChangeReasons;
        public string[] changedRoots;
        public string[] loadOrder;
        public ShadowPatchAssembly[] closure;
        public AssemblyDependencyEdge[] dependencyGraph;
        public string[] deferredFacadeReferences = new string[0];
        public bool unsigned = true;
        public string signatureAlgorithm = "None";
        public int nativeBudgetCapabilityVersion;
        public MetadataEncodingProfile metadataEncodingProfile;
        public MetadataCapacityReport metadataCapacityReport;
        public MetadataEncodingProfile2 metadataEncodingProfile2;
        public MetadataCapacityProfile2PreliminaryReport metadataCapacityReport2;
    }

    public sealed class ShadowBaselineBuildRequest
    {
        public string playerInputSnapshot;
        public string resourceBaselinePath;
        public string outputDirectory;
        public string buildId;
        public BuildTarget target;
        public string architecture;
        public ShadowSourcePins sourcePins;
        public ShadowPolicyConfiguration policy;
        public ShadowResourceBuildMap resources;
    }

    public sealed class ShadowPatchBuildRequest
    {
        public string baselineManifestPath;
        public string currentCompileSnapshot;
        public string outputDirectory;
        public string patchId;
        public BuildTarget target;
        public string architecture;
        public ShadowSourcePins sourcePins;
        public ShadowPolicyConfiguration policy;
        public string[] explicitChangedRoots;
        public bool dllOnly = true;
        public bool includePdb = true;
    }
}
