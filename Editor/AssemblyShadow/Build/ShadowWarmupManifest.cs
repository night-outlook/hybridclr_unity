using System;

namespace HybridCLR.Editor.AssemblyShadow
{
    // Separate envelope: never add warmup/default fields to the schema-1 DTO.
    [Serializable]
    public sealed class ShadowPatchManifestV2
    {
        public int schemaVersion = 2;
        public ShadowPatchManifest patch;
        public ShadowWarmupPlan warmup;
    }

    [Serializable]
    public sealed class ShadowWarmupPlan
    {
        public ShadowWarmupTypeEntry[] types = new ShadowWarmupTypeEntry[0];
        public ShadowWarmupMethodEntry[] methods = new ShadowWarmupMethodEntry[0];
    }

    [Serializable]
    public sealed class ShadowWarmupTypeEntry
    {
        public string assembly;
        public string type;
    }

    [Serializable]
    public sealed class ShadowWarmupTypeIdentity
    {
        // Full captured Assembly identity, not an ambient/simple-name lookup.
        public string assembly;
        public string type;
    }

    [Serializable]
    public sealed class ShadowWarmupMethodEntry
    {
        public string assembly;
        public string declaringType;
        public string name;
        public bool isStatic;
        public int genericArity;
        public ShadowWarmupTypeIdentity[] genericArguments = new ShadowWarmupTypeIdentity[0];
        public ShadowWarmupTypeIdentity returnType;
        public ShadowWarmupTypeIdentity[] parameterTypes = new ShadowWarmupTypeIdentity[0];
    }

    public sealed class ShadowWarmupManifestReadResult
    {
        public int SchemaVersion { get; internal set; }
        public ShadowPatchManifest BaseManifest { get; internal set; }
        public ShadowWarmupPlan Warmup { get; internal set; }
    }
}
