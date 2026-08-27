using System;
using System.Linq;

namespace HybridCLR.Editor.AssemblyShadow
{
    internal static class BootstrapIsolationRule
    {
        public static void Check(AssemblyPolicyDefinition consumer, AssemblyPolicyDefinition provider,
            ShadowPolicyConfiguration policy, ShadowPolicyValidationResult result)
        {
            if (!consumer.isBootstrap)
                return;
            if (provider.isShadowCapable)
                result.Error("BootstrapBusinessReference", "Bootstrap " + consumer.name + " directly references business/runtime assembly " + provider.name + ".");
        }

        public static bool IsApprovedReflection(AssemblyPolicyDefinition bootstrap, string reference,
            ShadowPolicyConfiguration policy, DateTime utcNow, string resolvedProvider = null, string resolvedType = null)
        {
            string value = reference ?? string.Empty;
            int separator = value.IndexOf('|');
            if (separator <= 0 || separator == value.Length - 1)
                return false;
            string callSite = value.Substring(0, separator);
            string argument = value.Substring(separator + 1);
            var entries = policy.dependencies == null ? null : policy.dependencies.bootstrapEntrypoints;
            return (entries ?? new BootstrapEntrypointDeclaration[0]).Any(entry => entry != null &&
                string.Equals(AssemblyNamePolicy.Canonical(entry.consumer), AssemblyNamePolicy.Canonical(bootstrap.name), StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(entry.provider) && !string.IsNullOrWhiteSpace(entry.typeName) &&
                !string.IsNullOrWhiteSpace(CallSite(entry)) && !string.IsNullOrWhiteSpace(entry.reason) &&
                string.Equals(CallSite(entry), callSite, StringComparison.Ordinal) &&
                (string.IsNullOrEmpty(resolvedProvider) || string.Equals(AssemblyNamePolicy.Canonical(entry.provider), resolvedProvider, StringComparison.OrdinalIgnoreCase)) &&
                (string.IsNullOrEmpty(resolvedType) || string.Equals(entry.typeName, resolvedType, StringComparison.Ordinal)) &&
                (!string.IsNullOrEmpty(entry.target) ? string.Equals(argument, entry.target, StringComparison.Ordinal) :
                 (string.Equals(argument, entry.provider, StringComparison.Ordinal) ||
                 string.Equals(argument, entry.typeName, StringComparison.Ordinal) ||
                 string.Equals(argument, entry.typeName + ", " + entry.provider, StringComparison.Ordinal))));
        }

        internal static string CallSite(BootstrapEntrypointDeclaration entry)
        {
            return string.IsNullOrWhiteSpace(entry.callSite) ? entry.method : entry.callSite;
        }

    }
}
