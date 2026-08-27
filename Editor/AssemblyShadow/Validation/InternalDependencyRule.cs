using System;

namespace HybridCLR.Editor.AssemblyShadow
{
    internal static class InternalDependencyRule
    {
        public static void Check(AssemblyPolicyDefinition consumer, AssemblyPolicyDefinition provider,
            ShadowPolicyConfiguration policy, ShadowPolicyValidationResult result)
        {
            if (!AssemblyNamePolicy.IsInternal(provider.name))
                return;
            bool sameModule = string.Equals(AssemblyNamePolicy.LogicalModule(consumer.name),
                AssemblyNamePolicy.LogicalModule(provider.name), StringComparison.OrdinalIgnoreCase);
            bool explicitlyAllowedEditor = !consumer.entersPlayer &&
                Contains(policy.allowedInternalEditorAssemblies, consumer.name);
            if (!sameModule && !explicitlyAllowedEditor)
                result.Error("InternalDependency", consumer.name + " references closed internal assembly " + provider.name + ".");
        }

        private static bool Contains(string[] values, string name)
        {
            foreach (string value in values ?? new string[0])
                if (string.Equals(AssemblyNamePolicy.Canonical(value), AssemblyNamePolicy.Canonical(name), StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }
    }
}
