using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace HybridCLR.Editor.AssemblyShadow
{
    // These are build-time classifications, not runtime execution modes.
    public enum AssemblyClassification { Runtime, EditorOnly, TestOnly, Reference, NormalHotUpdate, BuildFiltered }

    [Serializable]
    public sealed class AssemblyCapability
    {
        public string name;
        public AssemblyClassification classification = AssemblyClassification.Runtime;
        public bool isShadowCapable;
        public bool isBootstrap;
        public bool isPrecompiled;
        public bool capabilityDeclared;
    }

    [Serializable]
    public sealed class ShadowDependencyConfiguration
    {
        public int schemaVersion = 1;
        public DeclaredRuntimeDependency[] runtimeDependencies = new DeclaredRuntimeDependency[0];
        public DeclaredResourceDependency[] resourceDependencies = new DeclaredResourceDependency[0];
        public BootstrapEntrypointDeclaration[] bootstrapEntrypoints = new BootstrapEntrypointDeclaration[0];
    }

    [Serializable]
    public sealed class DeclaredRuntimeDependency
    {
        public string consumer;
        public string provider;
        public string kind;
        public string evidence;
        public string callSite;
    }

    [Serializable]
    public sealed class DeclaredResourceDependency
    {
        public string bundle;
        public string assembly;
    }

    // An approved bootstrap entry is an opaque, reflection-only boundary, not a
    // normal business dependency. Never add it to the reverse dependency graph.
    [Serializable]
    public sealed class BootstrapEntrypointDeclaration
    {
        public string consumer;
        public string provider;
        public string typeName;
        public string method;
        public string reason;
        public string callSite;
        public string target;
    }

    [Serializable]
    public sealed class ExtensibilityWhitelist
    {
        public int schemaVersion = 1;
        public ExtensibilityWhitelistEntry[] entries = new ExtensibilityWhitelistEntry[0];
    }

    [Serializable]
    public sealed class ExtensibilityWhitelistEntry
    {
        public string provider;
        public string consumer;
        public string owner;
        public string reason;
        public string reviewer;
        public string expires;
    }

    [Serializable]
    public sealed class ShadowPolicyConfiguration
    {
        public AssemblyCapability[] assemblies = new AssemblyCapability[0];
        public ShadowDependencyConfiguration dependencies = new ShadowDependencyConfiguration();
        public ExtensibilityWhitelist extensibilityWhitelist = new ExtensibilityWhitelist();
        public bool rejectUnknownReflectionDependencies = true;
        public bool enforceResourceAbi = true;
        public string reflectionBindingConfigurationSha256;
        public string reflectionBindingConfigurationHash;
        public ShadowReflectionBindingDeclaration[] reflectionBindings = new ShadowReflectionBindingDeclaration[0];
        // Explicit Editor-only authoring/test tools; none may enter a Player.
        public string[] allowedInternalEditorAssemblies = new string[0];
    }

    public sealed class ShadowBuildException : Exception
    {
        public string Code { get; private set; }
        public ShadowBuildException(string code, string message) : base(code + ": " + message) { Code = code; }
    }

    public static class ShadowHash
    {
        public static string Bytes(byte[] bytes)
        {
            using (var hash = SHA256.Create())
                return string.Concat(hash.ComputeHash(bytes).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
        }

        public static string Text(string text) { return Bytes(Encoding.UTF8.GetBytes(text)); }

        public static string File(string path)
        {
            using (var stream = System.IO.File.OpenRead(path))
            using (var hash = SHA256.Create())
                return string.Concat(hash.ComputeHash(stream).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
        }

        public static string[] Sorted(IEnumerable<string> values)
        {
            return values.Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal).ToArray();
        }

        public static void Require(bool condition, string code, string message)
        {
            if (!condition) throw new ShadowBuildException(code, message);
        }

        public static string SafeChild(string root, string relative)
        {
            Require(!string.IsNullOrWhiteSpace(relative) && !Path.IsPathRooted(relative), "InvalidPath", "Expected a relative artifact path.");
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(Path.Combine(fullRoot, relative));
            Require(full.StartsWith(fullRoot, StringComparison.Ordinal), "InvalidPath", "Artifact path escapes its root: " + relative);
            return full;
        }
    }
}
