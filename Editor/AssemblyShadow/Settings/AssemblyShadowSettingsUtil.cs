using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEditor.Compilation;
using UnityEngine;
using HybridCLR.Editor.Settings;

namespace HybridCLR.Editor.AssemblyShadow
{
    public static class AssemblyShadowSettingsUtil
    {
        public const string DefaultDependencyConfigPath = "ProjectSettings/AssemblyShadowDependencies.json";
        public const string DefaultWhitelistPath = "ProjectSettings/AssemblyShadowExtensibilityWhitelist.json";

        public static ShadowPolicyConfiguration CreatePolicyConfiguration(BuildTarget target)
        {
            AssemblyShadowSettings settings = AssemblyShadowSettings.Instance;
            settings.Normalize();
            ValidateSettingsOrThrow(settings);
            var policy = new ShadowPolicyConfiguration
            {
                assemblies = BuildCapabilities(settings, target),
                dependencies = LoadDependencyConfiguration(settings),
                extensibilityWhitelist = LoadWhitelist(settings),
                rejectUnknownReflectionDependencies = settings.rejectUnknownReflectionDependencies,
                enforceResourceAbi = settings.enforceResourceAbi,
                allowedInternalEditorAssemblies = settings.allowedInternalEditorAssemblies ?? new string[0],
            };
            return ShadowReflectionBindingEvidence.DeclareProject(policy);
        }

        public static string[] ValidateSettings()
        {
            return ValidateSettings(AssemblyShadowSettings.Instance);
        }

        public static string[] ValidateSettings(AssemblyShadowSettings settings)
        {
            if (!settings)
                return new[] { "Assembly Shadow settings could not be loaded." };
            settings.Normalize();
            var errors = new List<string>();
            var shadow = Names(settings.shadowAssemblyDefinitions, settings.shadowAssemblyNames, errors, "shadow");
            var bootstrap = Names(settings.bootstrapAssemblyDefinitions, settings.bootstrapAssemblyNames, errors, "bootstrap");
            AddDuplicates(errors, shadow, "Duplicate shadow assembly");
            AddDuplicates(errors, bootstrap, "Duplicate bootstrap assembly");
            foreach (string name in shadow.Intersect(bootstrap, StringComparer.OrdinalIgnoreCase))
                errors.Add("Assembly is both shadow and bootstrap: " + name);

            var normalNames = new List<string>((HybridCLRSettings.Instance.hotUpdateAssemblies ?? new string[0]).Select(AssemblyNamePolicy.Canonical));
            normalNames.AddRange(Names(HybridCLRSettings.Instance.hotUpdateAssemblyDefinitions, null, errors, "normal hot-update"));
            string[] normal = normalNames.ToArray();
            foreach (string name in shadow.Intersect(normal, StringComparer.OrdinalIgnoreCase))
                errors.Add("Shadow assembly overlaps normal hot-update assembly: " + name);
            foreach (string name in bootstrap.Intersect(normal, StringComparer.OrdinalIgnoreCase))
                errors.Add("Bootstrap assembly overlaps normal hot-update assembly: " + name);

            if (settings.targetReferenceDirectories.Any(string.IsNullOrWhiteSpace))
                errors.Add("Target reference directories must not contain empty entries.");
            var plugins = new List<string>(settings.precompiledAssemblyNames.Select(AssemblyNamePolicy.Canonical));
            foreach (AssemblyCapability capability in settings.precompiledAssemblyCapabilities)
            {
                if (capability == null || string.IsNullOrWhiteSpace(capability.name))
                    errors.Add("Precompiled capability requires an assembly name.");
                else
                {
                    plugins.Add(AssemblyNamePolicy.Canonical(capability.name));
                    if (capability.isBootstrap || capability.classification != AssemblyClassification.Runtime)
                        errors.Add("Precompiled capability may only declare a runtime plugin's shadow capability: " + capability.name);
                }
            }
            AddDuplicates(errors, plugins, "Duplicate precompiled capability");
            return errors.Distinct(StringComparer.Ordinal).ToArray();
        }

        public static void ValidateSettingsOrThrow(AssemblyShadowSettings settings = null)
        {
            string[] errors = ValidateSettings(settings ?? AssemblyShadowSettings.Instance);
            if (errors.Length != 0)
                throw new ShadowBuildException("InvalidShadowSettings", string.Join("; ", errors));
        }

        internal static string[] Names(AssemblyDefinitionAsset[] definitions, IEnumerable<string> names,
            List<string> errors, string role)
        {
            var result = new List<string>();
            foreach (AssemblyDefinitionAsset definition in definitions ?? new AssemblyDefinitionAsset[0])
            {
                if (!definition)
                {
                    errors.Add("Null " + role + " assembly definition.");
                    continue;
                }
                try
                {
                    var data = JsonUtility.FromJson<AssemblyDefinitionData>(definition.text);
                    if (data == null || string.IsNullOrWhiteSpace(data.name))
                        errors.Add("Unnamed " + role + " assembly definition: " + definition.name);
                    else
                        result.Add(AssemblyNamePolicy.Canonical(data.name));
                }
                catch (Exception exception)
                {
                    errors.Add("Invalid " + role + " assembly definition " + definition.name + ": " + exception.Message);
                }
            }
            foreach (string name in names ?? new string[0])
            {
                if (string.IsNullOrWhiteSpace(name))
                    errors.Add("Empty " + role + " assembly name.");
                else
                    result.Add(AssemblyNamePolicy.Canonical(name));
            }
            return result.ToArray();
        }

        private static AssemblyCapability[] BuildCapabilities(AssemblyShadowSettings settings, BuildTarget target)
        {
            ShadowHash.Require(target == EditorUserBuildSettings.activeBuildTarget, "CompilerTargetMismatch",
                "CompilationPipeline describes the active target. Switch the active build target before constructing policy.");
            // Unity API failures are build failures. Returning a partial map here could
            // omit an ordinary AOT consumer from reverse dependency validation.
            Assembly[] player = CompilationPipeline.GetAssemblies(AssembliesType.Player);
            Assembly[] production = CompilationPipeline.GetAssemblies(AssembliesType.PlayerWithoutTestAssemblies);
            AssemblyCapability[] inventory = BuildCompilerInventory(player, production,
                reference => IsUnityInstallationReference(reference, EditorApplication.applicationContentsPath));
            var normalErrors = new List<string>();
            string[] normal = Names(HybridCLRSettings.Instance.hotUpdateAssemblyDefinitions,
                HybridCLRSettings.Instance.hotUpdateAssemblies, normalErrors, "normal hot-update");
            ShadowHash.Require(normalErrors.Count == 0, "InvalidShadowSettings", string.Join("; ", normalErrors.ToArray()));
            return BuildCapabilities(settings, inventory, normal);
        }

        internal static AssemblyCapability[] BuildCompilerInventory(Assembly[] player, Assembly[] production,
            Func<string, bool> isFrameworkReference)
        {
            ShadowHash.Require(player != null && production != null, "CompilerInventoryUnavailable", "Unity returned no Player assembly inventory.");
            var productionNames = new HashSet<string>(production.Select(item => AssemblyNamePolicy.Canonical(item.name)), StringComparer.OrdinalIgnoreCase);
            var inventory = new Dictionary<string, AssemblyCapability>(StringComparer.OrdinalIgnoreCase);
            foreach (Assembly assembly in player)
            {
                string name = AssemblyNamePolicy.Canonical(assembly.name);
                inventory.Add(name, new AssemblyCapability
                {
                    name = name,
                    classification = !productionNames.Contains(name) ? AssemblyClassification.TestOnly :
                        ((assembly.flags & AssemblyFlags.EditorAssembly) != 0 ? AssemblyClassification.EditorOnly : AssemblyClassification.Runtime),
                });
            }
            foreach (string name in productionNames)
                ShadowHash.Require(inventory.ContainsKey(name), "CompilerInventoryMismatch", "Production assembly is absent from the full Player inventory: " + name);
            // Production references win over test-only use of the same plugin.
            // This is set membership from Unity's API, not an assembly-name exemption.
            foreach (Assembly assembly in production.Concat(player))
                foreach (string reference in assembly.compiledAssemblyReferences ?? new string[0])
                {
                    string name = AssemblyNamePolicy.Canonical(Path.GetFileName(reference));
                    if (inventory.ContainsKey(name)) continue;
                    bool framework = isFrameworkReference(reference);
                    inventory.Add(name, new AssemblyCapability
                    {
                        name = name,
                        classification = framework ? AssemblyClassification.Reference :
                            (productionNames.Contains(AssemblyNamePolicy.Canonical(assembly.name)) ? AssemblyClassification.Runtime : AssemblyClassification.TestOnly),
                        isPrecompiled = !framework,
                    });
                }
            return inventory.Values.OrderBy(item => item.name, StringComparer.Ordinal).ToArray();
        }

        // Deterministic core: the caller supplies Unity-derived inventory rather
        // than guessing roles from assembly prefixes or source directory names.
        internal static AssemblyCapability[] BuildCapabilities(AssemblyShadowSettings settings,
            IEnumerable<AssemblyCapability> compilerInventory, IEnumerable<string> normalHotUpdates)
        {
            settings.Normalize();
            var errors = new List<string>();
            string[] shadow = Names(settings.shadowAssemblyDefinitions, settings.shadowAssemblyNames, errors, "shadow");
            string[] bootstrap = Names(settings.bootstrapAssemblyDefinitions, settings.bootstrapAssemblyNames, errors, "bootstrap");
            var result = new Dictionary<string, AssemblyCapability>(StringComparer.OrdinalIgnoreCase);
            foreach (AssemblyCapability source in compilerInventory)
            {
                string key = AssemblyNamePolicy.Canonical(source.name);
                ShadowHash.Require(!result.ContainsKey(key), "DuplicateCompilerAssembly", key);
                result.Add(key, new AssemblyCapability { name = key, classification = source.classification, isPrecompiled = source.isPrecompiled });
            }
            foreach (string rawName in normalHotUpdates ?? Enumerable.Empty<string>())
            {
                string name = AssemblyNamePolicy.Canonical(rawName);
                AssemblyCapability capability;
                if (result.TryGetValue(name, out capability) && capability.classification == AssemblyClassification.Runtime)
                    capability.classification = AssemblyClassification.NormalHotUpdate;
            }
            foreach (string name in shadow)
                DeclareCapability(result, name, true, false, false);
            foreach (string name in bootstrap)
                DeclareCapability(result, name, false, true, false);
            foreach (string name in settings.precompiledAssemblyNames)
                DeclareCapability(result, name, true, false, true);
            foreach (AssemblyCapability declaration in settings.precompiledAssemblyCapabilities)
            {
                ShadowHash.Require(declaration != null && !string.IsNullOrWhiteSpace(declaration.name), "InvalidCapability", "Precompiled capability requires a name.");
                DeclareCapability(result, declaration.name, declaration.isShadowCapable, false, true);
            }
            ShadowHash.Require(errors.Count == 0, "InvalidShadowSettings", string.Join("; ", errors.ToArray()));
            return result.Values.OrderBy(item => item.name, StringComparer.Ordinal).ToArray();
        }

        private static void DeclareCapability(Dictionary<string, AssemblyCapability> result, string name,
            bool shadow, bool bootstrap, bool precompiled)
        {
            string key = AssemblyNamePolicy.Canonical(name);
            ShadowHash.Require(!string.IsNullOrEmpty(key), "InvalidCapability", "Capability requires a name.");
            AssemblyCapability capability;
            if (!result.TryGetValue(key, out capability))
            {
                ShadowHash.Require(!precompiled, "UnknownPrecompiledCapability", "Precompiled declaration is not a target compiler input: " + key);
                capability = new AssemblyCapability { name = key };
                result.Add(key, capability);
            }
            ShadowHash.Require(!precompiled || capability.isPrecompiled, "InvalidPrecompiledCapability", "Assembly is not an external runtime plugin: " + key);
            ShadowHash.Require(!precompiled || shadow || !capability.isShadowCapable, "ConflictingCapability",
                "Explicit non-shadow plugin conflicts with the candidate list: " + key);
            bool ordinaryHotUpdatePlugin = precompiled && !shadow && !bootstrap && capability.classification == AssemblyClassification.NormalHotUpdate;
            ShadowHash.Require(capability.classification == AssemblyClassification.Runtime || ordinaryHotUpdatePlugin, "InvalidCapabilityClassification",
                key + " is " + capability.classification + ", not an AOT runtime candidate.");
            ShadowHash.Require(!(shadow && capability.isBootstrap) && !(bootstrap && capability.isShadowCapable),
                "ConflictingCapability", key + " cannot be both bootstrap and shadow-capable.");
            capability.isShadowCapable |= shadow;
            capability.isBootstrap |= bootstrap;
            capability.isPrecompiled |= precompiled;
            capability.capabilityDeclared = true;
        }

        internal static bool IsUnityInstallationReference(string path, string unityContentsPath)
        {
            string root = Path.GetFullPath(unityContentsPath).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(path).StartsWith(root, StringComparison.Ordinal);
        }

        private static ShadowDependencyConfiguration LoadDependencyConfiguration(AssemblyShadowSettings settings)
        {
            string json = ReadTextAssetOrPath(settings.explicitDependencyConfig, settings.explicitDependencyConfigPath, DefaultDependencyConfigPath);
            if (string.IsNullOrWhiteSpace(json))
                return new ShadowDependencyConfiguration();
            try
            {
                var result = JsonUtility.FromJson<ShadowDependencyConfiguration>(json);
                if (result == null)
                    throw new InvalidOperationException("The source did not contain a dependency configuration object.");
                return result;
            }
            catch (ShadowBuildException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new ShadowBuildException("InvalidPolicySource", "Dependency configuration is invalid: " + exception.Message);
            }
        }

        private static ExtensibilityWhitelist LoadWhitelist(AssemblyShadowSettings settings)
        {
            string json = ReadTextAssetOrPath(settings.extensibilityWhitelist, settings.extensibilityWhitelistPath, DefaultWhitelistPath);
            if (string.IsNullOrWhiteSpace(json))
                return new ExtensibilityWhitelist();
            try
            {
                var result = JsonUtility.FromJson<ExtensibilityWhitelist>(json);
                if (result == null)
                    throw new InvalidOperationException("The source did not contain an extensibility whitelist object.");
                return result;
            }
            catch (ShadowBuildException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new ShadowBuildException("InvalidPolicySource", "Extensibility whitelist is invalid: " + exception.Message);
            }
        }

        private static string ReadTextAssetOrPath(TextAsset asset, string configuredPath, string fallbackPath)
        {
            if (asset != null)
            {
                if (string.IsNullOrWhiteSpace(asset.text))
                    throw new ShadowBuildException("InvalidPolicySource", "An explicitly assigned policy TextAsset is empty.");
                return asset.text;
            }

            bool customPath = !string.IsNullOrWhiteSpace(configuredPath) && !PathsEqual(configuredPath, fallbackPath);
            string path;
            try
            {
                path = ResolveProjectPath(customPath ? configuredPath : fallbackPath);
            }
            catch (ShadowBuildException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new ShadowBuildException("InvalidPolicySource", "The explicitly configured policy source path is invalid: " + exception.Message);
            }
            if (!File.Exists(path))
            {
                if (customPath)
                    throw new ShadowBuildException("InvalidPolicySource", "The explicitly configured policy source does not exist: " + configuredPath);
                // A missing default means that no optional policy has been authored.
                return string.Empty;
            }

            try
            {
                string text = File.ReadAllText(path);
                if (customPath && string.IsNullOrWhiteSpace(text))
                    throw new ShadowBuildException("InvalidPolicySource", "The explicitly configured policy source is empty: " + configuredPath);
                return text;
            }
            catch (ShadowBuildException)
            {
                throw;
            }
            catch (Exception exception)
            {
                throw new ShadowBuildException("InvalidPolicySource", "The policy source could not be read: " + path + ". " + exception.Message);
            }
        }

        private static string ResolveProjectPath(string path)
        {
            if (Path.IsPathRooted(path))
                return Path.GetFullPath(path);
            DirectoryInfo project = Directory.GetParent(Application.dataPath);
            if (project == null)
                throw new ShadowBuildException("InvalidPolicySource", "The Unity project root could not be resolved.");
            return Path.GetFullPath(Path.Combine(project.FullName, path));
        }

        private static bool PathsEqual(string left, string right)
        {
            try
            {
                return string.Equals(ResolveProjectPath(left), ResolveProjectPath(right), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
            }
        }

        private static void AddDuplicates(List<string> errors, IEnumerable<string> values, string prefix)
        {
            foreach (string duplicate in values.GroupBy(value => value, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1).Select(group => group.Key))
                errors.Add(prefix + ": " + duplicate);
        }

        [Serializable]
        private sealed class AssemblyDefinitionData { public string name; public string[] optionalUnityReferences; }
    }
}
