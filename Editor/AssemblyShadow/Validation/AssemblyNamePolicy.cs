using System;

namespace HybridCLR.Editor.AssemblyShadow
{
    public static class AssemblyNamePolicy
    {
        public static string Canonical(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return string.Empty;
            string value = name.Trim();
            if (value.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                value = value.Substring(0, value.Length - 4);
            return value;
        }

        public static bool IsInternal(string name) { return Canonical(name).EndsWith(".Implementation.Internal", StringComparison.OrdinalIgnoreCase); }
        public static bool IsExtensibility(string name) { return Canonical(name).EndsWith(".Implementation.Extensibility", StringComparison.OrdinalIgnoreCase); }
        public static bool IsFramework(string name)
        {
            string value = Canonical(name);
            return value == "mscorlib" || value == "netstandard" || value == "System" ||
                value.StartsWith("System.", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("UnityEngine", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("UnityEditor", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("Unity.", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("Mono.", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("HybridCLR.", StringComparison.OrdinalIgnoreCase) ||
                value == "nunit.framework";
        }

        public static string LogicalModule(string name)
        {
            string value = Canonical(name);
            int index = value.IndexOf(".Implementation.", StringComparison.OrdinalIgnoreCase);
            return index > 0 ? value.Substring(0, index) : value;
        }
    }
}
