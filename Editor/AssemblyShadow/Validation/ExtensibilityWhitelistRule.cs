using System;
using System.Globalization;
using System.Linq;

namespace HybridCLR.Editor.AssemblyShadow
{
    internal static class ExtensibilityWhitelistRule
    {
        public static void Check(AssemblyPolicyDefinition consumer, AssemblyPolicyDefinition provider,
            ShadowPolicyConfiguration policy, DateTime utcNow, ShadowPolicyValidationResult result)
        {
            if (!AssemblyNamePolicy.IsExtensibility(provider.name) ||
                string.Equals(AssemblyNamePolicy.LogicalModule(consumer.name), AssemblyNamePolicy.LogicalModule(provider.name), StringComparison.OrdinalIgnoreCase))
                return;

            var entries = (policy.extensibilityWhitelist == null ? null : policy.extensibilityWhitelist.entries) ?? new ExtensibilityWhitelistEntry[0];
            var matching = entries.Where(entry => entry != null &&
                string.Equals(AssemblyNamePolicy.Canonical(entry.provider), AssemblyNamePolicy.Canonical(provider.name), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(AssemblyNamePolicy.Canonical(entry.consumer), AssemblyNamePolicy.Canonical(consumer.name), StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matching.Length == 0)
            {
                result.Error("ExtensibilityNotWhitelisted", consumer.name + " references extensibility assembly " + provider.name + " without an approval entry.");
                return;
            }
            bool valid = matching.Any(entry => !string.IsNullOrWhiteSpace(entry.owner) &&
                !string.IsNullOrWhiteSpace(entry.reason) && !string.IsNullOrWhiteSpace(entry.reviewer) &&
                IsFuture(entry.expires, utcNow));
            if (!valid)
                result.Error("ExtensibilityWhitelistExpired", "Extensibility approval for " + consumer.name + " -> " + provider.name + " is incomplete or expired.");
        }

        private static bool IsFuture(string value, DateTime utcNow)
        {
            DateTime expiry;
            return DateTime.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out expiry) && expiry > utcNow;
        }
    }
}
