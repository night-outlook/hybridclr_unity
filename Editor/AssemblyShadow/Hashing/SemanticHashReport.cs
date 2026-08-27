using System;

namespace HybridCLR.Editor.AssemblyShadow
{
    [Serializable]
    public sealed class SemanticHashReport
    {
        public int schema;
        public string assembly;
        public string semanticHash;
        public SemanticHashSections sections = new SemanticHashSections();
    }

    [Serializable]
    public sealed class SemanticHashSections
    {
        public string identity;
        public string types;
        public string methods;
        public string attributes;
        public string resources;
    }
}
