using System;

namespace HybridCLR.Editor.AssemblyShadow
{
    /// <summary>Versioned, editor-only description of Unity's serialized type contract.</summary>
    [Serializable]
    public sealed class ResourceAbiDescriptor
    {
        public int schemaVersion = 2;
        public ResourceAbiTypeDescriptor[] types = new ResourceAbiTypeDescriptor[0];
        public string[] unknowns = new string[0];

        public ResourceAbiDescriptor() { }

        public ResourceAbiDescriptor(ResourceAbiTypeDescriptor[] types, string[] unknowns)
        {
            this.types = types ?? new ResourceAbiTypeDescriptor[0];
            this.unknowns = unknowns ?? new string[0];
        }
    }

    [Serializable]
    public sealed class ResourceAbiTypeDescriptor
    {
        public string typeKey;
        public string assembly;
        public string @namespace;
        public string type;
        public string baseChain;
        public string[] interfaces = new string[0];
        public ResourceAbiFieldDescriptor[] fields = new ResourceAbiFieldDescriptor[0];
        public bool serializationCallback;
        public string callbackSemanticHash;
        public string[] serializeReferenceCandidates = new string[0];
        // Direct serialization edges: bases, inline values and managed candidates.
        public string[] referencedTypeKeys = new string[0];
        public string[] unknownReasons = new string[0];
        public bool hasUnknown;
    }

    [Serializable]
    public sealed class ResourceAbiFieldDescriptor
    {
        public string declaringType;
        public string name;
        public string type;
        public string shape;
        public string flags;
        public string[] formerNames = new string[0];
        public string managedReferenceMode;
        public bool unknown;
    }

}
