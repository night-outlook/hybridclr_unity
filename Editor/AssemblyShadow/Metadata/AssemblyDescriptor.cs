using System;

namespace HybridCLR.Editor.AssemblyShadow
{
    [Serializable]
    public sealed class TypeDescriptor
    {
        public string typeKey;
        public string name;
        public string @namespace;
        public string baseType;
    }

    [Serializable]
    public sealed class AssemblyDescriptor
    {
        public string name;
        public string mvid;
        public string filePath;
        public string sha256;
        public string semanticHash;
        public string[] references = new string[0];
        public TypeDescriptor[] types = new TypeDescriptor[0];
        public bool isShadowCapable;
        public bool isBootstrap;
        public bool isPrecompiled;
        public bool capabilityDeclared;
        public AssemblyClassification classification;
    }
}
