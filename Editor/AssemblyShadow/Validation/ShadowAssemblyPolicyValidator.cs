using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using HybridCLR.AssemblyShadow.CodeGen;
using UnityEditor;
using UnityEditor.Compilation;
using PackageInfo = UnityEditor.PackageManager.PackageInfo;
using UnityEngine;

namespace HybridCLR.Editor.AssemblyShadow
{
    [Serializable]
    public class AssemblyPolicyDefinition
    {
        public string name;
        public string[] references = new string[0];
        public AssemblyClassification classification = AssemblyClassification.Runtime;
        public bool entersPlayer = true;
        public bool isShadowCapable;
        public bool isBootstrap;
        public bool isPrecompiled;
        public bool capabilityDeclared;
        public bool unknownReflectionDependencies;
        public string[] reflectionReferences = new string[0];
        public string[] unknownReflectionCallSites = new string[0];
        public ReflectionDependencyEvidence[] reflectionDependencies = new ReflectionDependencyEvidence[0];
        public ManagedAcquisitionEvidence[] managedAcquisitions = new ManagedAcquisitionEvidence[0];
        public string sourcePath;
    }

    // Alias retained for callers that describe source asmdefs rather than
    // compiled metadata. Both forms are accepted by ValidateDefinitions.
    public sealed class ShadowAssemblyDefinition : AssemblyPolicyDefinition { }

    [Serializable]
    public sealed class ReflectionDependencyEvidence
    {
        public string callSite;
        public string target;
        public string provider;
        public string typeName;
        public string kind;
    }

    [Serializable]
    public sealed class ManagedAcquisitionEvidence
    {
        public string kind, callSite, methodSignature, methodHash, operationSignature;
        public int operationIndex;
        public bool requiresContract, verified;
        public string configurationHash, siteId, provider;
    }

    [Serializable]
    public sealed class ShadowPolicyDiagnostic
    {
        public string code;
        public string message;
        public override string ToString() { return code + ": " + message; }
    }

    public sealed class ShadowPolicyValidationResult
    {
        private readonly List<ShadowPolicyDiagnostic> diagnostics = new List<ShadowPolicyDiagnostic>();
        public bool IsValid { get { return diagnostics.Count == 0; } }
        public bool Passed { get { return IsValid; } }
        public IReadOnlyList<ShadowPolicyDiagnostic> Diagnostics { get { return diagnostics; } }
        public IReadOnlyList<string> Errors { get { return diagnostics.Select(item => item.ToString()).ToArray(); } }
        public void Error(string code, string message) { diagnostics.Add(new ShadowPolicyDiagnostic { code = code, message = message }); }
        public void ThrowIfInvalid()
        {
            if (!IsValid)
                throw new ShadowBuildException("PolicyValidation", string.Join(Environment.NewLine, Errors.ToArray()));
        }
        public override string ToString() { return string.Join(Environment.NewLine, Errors.ToArray()); }
    }

    public static class ShadowAssemblyPolicyValidator
    {
        public static ShadowPolicyValidationResult ValidateCompiled(CompiledAssemblySet set,
            ShadowPolicyConfiguration policy, DateTime utcNow, ReflectionBindingConfiguration acquisitionConfiguration = null,
            IReadOnlyDictionary<string, byte[]> fixedImageEvidence = null)
        {
            if (set == null)
            {
                var missing = new ShadowPolicyValidationResult();
                missing.Error("MissingCompiledSet", "CompiledAssemblySet is required for compiled policy validation.");
                return missing;
            }
            var bindingErrors = new ShadowPolicyValidationResult();
            var bindings = VerifyAcquisitions(set, policy, acquisitionConfiguration, fixedImageEvidence, bindingErrors);
            var definitions = new List<AssemblyPolicyDefinition>();
            foreach (KeyValuePair<string, AssemblyDescriptor> pair in set.Assemblies)
            {
                AssemblyPolicyDefinition definition = FromDescriptor(pair.Value);
                if (IsRuntime(definition)) ReflectionDependencyScanner.ScanVerified(set.Modules, pair.Key, definition, bindings);
                definitions.Add(definition);
            }
            // Resolver modules are evidence for references, not missing Player
            // descriptors. They must never turn an omitted runtime plugin into
            // an innocuous framework dependency.
            foreach (var pair in set.Modules.Where(pair => !set.Assemblies.ContainsKey(pair.Key)))
            {
                AssemblyCapability capability = FindCapability(policy, pair.Key);
                definitions.Add(new AssemblyPolicyDefinition
                {
                    name = pair.Key,
                    classification = capability == null ? AssemblyClassification.Reference : capability.classification,
                    entersPlayer = false,
                    isPrecompiled = capability != null && capability.isPrecompiled,
                    capabilityDeclared = capability != null && capability.capabilityDeclared,
                    isShadowCapable = capability != null && capability.isShadowCapable,
                    isBootstrap = capability != null && capability.isBootstrap,
                });
            }
            var result = ValidateDefinitions(definitions, policy, utcNow);
            foreach (var error in bindingErrors.Diagnostics) result.Error(error.code, error.message);
            return result;
        }

        private static VerifiedReflectionBinding[] VerifyAcquisitions(CompiledAssemblySet set, ShadowPolicyConfiguration policy,
            ReflectionBindingConfiguration configuration, IReadOnlyDictionary<string, byte[]> images, ShadowPolicyValidationResult result)
        {
            var verified = new List<VerifiedReflectionBinding>();
            if (configuration == null)
            {
                if (policy != null && (!string.IsNullOrEmpty(policy.reflectionBindingConfigurationHash) ||
                    !string.IsNullOrEmpty(policy.reflectionBindingConfigurationSha256)))
                    result.Error("MissingManagedAcquisitionConfiguration", "The pinned reflection/acquisition configuration must be supplied to compiled validation.");
                return verified.ToArray();
            }
            try
            {
                configuration.Validate(); configuration.ValidateImageEvidence(images);
                if (policy != null && !string.IsNullOrEmpty(policy.reflectionBindingConfigurationHash) &&
                    policy.reflectionBindingConfigurationHash != configuration.ComputeHash())
                    throw new ReflectionBindingException("AcquisitionConfigurationMismatch", "Policy and supplied configuration hashes differ.");
                foreach (var group in configuration.sites.GroupBy(site => site.assembly, StringComparer.Ordinal))
                {
                    ModuleDefMD module;
                    if (!set.Modules.TryGetValue(group.Key, out module))
                        throw new ReflectionBindingException("AcquisitionConsumerMissing", group.Key);
                    verified.AddRange(ReflectionBindingTransformer.Verify(module, configuration));
                }
                foreach (var binding in verified)
                {
                    if (binding.Kind == "FixedAssemblyBytes")
                    {
                        string provider = binding.Providers.Single(); AssemblyDescriptor descriptor;
                        var declared = FindCapability(policy, provider);
                        if (!set.Assemblies.TryGetValue(provider, out descriptor) || descriptor.classification != AssemblyClassification.NormalHotUpdate ||
                            descriptor.isShadowCapable || descriptor.isBootstrap || (declared != null &&
                                (declared.classification != AssemblyClassification.NormalHotUpdate || declared.isShadowCapable || declared.isBootstrap)))
                            throw new ReflectionBindingException("InvalidFixedImageProvider", binding.SiteId + ": expected actual ordinary hot-update input " + provider);
                        using (var image = ModuleDefMD.Load(images[binding.ImagePath], new ModuleCreationOptions { TryToLoadPdbFromDisk = false }))
                        {
                            if (set.GetModule(provider).Assembly.FullName != binding.ProviderAssemblyIdentity ||
                                AssemblySemanticHasher.Compute(image).semanticHash != AssemblySemanticHasher.Compute(set.GetModule(provider)).semanticHash)
                                throw new ReflectionBindingException("FixedImageSemanticMismatch", binding.SiteId);
                        }
                    }
                    else if (binding.Kind == "FiniteAssemblyList" || binding.Kind == "FiniteAssemblyTypes")
                    {
                        foreach (string aqn in binding.AllowedTypes)
                        {
                            string provider = ReflectionBindingConfiguration.ProviderOf(aqn); ModuleDefMD physical;
                            AssemblyDescriptor descriptor; var capability = FindCapability(policy, provider);
                            bool actual = set.Assemblies.TryGetValue(provider, out descriptor);
                            var classification = actual ? descriptor.classification : capability == null ? AssemblyClassification.Reference : capability.classification;
                            bool controlled = (actual && (descriptor.isBootstrap || descriptor.isShadowCapable)) ||
                                (capability != null && (capability.isBootstrap || capability.isShadowCapable || capability.classification != classification));
                            int comma = aqn.IndexOf(',');
                            if (controlled || (classification != AssemblyClassification.Runtime && classification != AssemblyClassification.Reference) ||
                                (!actual && classification != AssemblyClassification.Reference) || !set.Modules.TryGetValue(provider, out physical) ||
                                physical.Assembly.FullName != aqn.Substring(comma + 2) ||
                                !physical.GetTypes().Any(type => type.FullName == aqn.Substring(0, comma).Replace('+', '/') && !type.HasGenericParameters))
                                throw new ReflectionBindingException("InvalidFiniteAcquisitionProvider", binding.SiteId + ": " + aqn);
                        }
                    }
                }
            }
            catch (Exception error)
            {
                // No partially verified contract is usable after any evidence failure.
                verified.Clear(); result.Error("InvalidManagedAcquisitionContract", error.Message);
            }
            return verified.ToArray();
        }

        public static ShadowPolicyValidationResult ValidateDefinitions(IEnumerable<AssemblyPolicyDefinition> definitions,
            ShadowPolicyConfiguration policy, DateTime utcNow)
        {
            var result = new ShadowPolicyValidationResult();
            policy = policy ?? new ShadowPolicyConfiguration();
            var items = (definitions ?? Enumerable.Empty<AssemblyPolicyDefinition>()).Where(item => item != null).ToArray();
            var byName = new Dictionary<string, AssemblyPolicyDefinition>(StringComparer.OrdinalIgnoreCase);
            foreach (AssemblyPolicyDefinition item in items)
            {
                item.name = AssemblyNamePolicy.Canonical(item.name);
                if (string.IsNullOrEmpty(item.name))
                {
                    result.Error("UnnamedAssembly", "Compiled assembly definition has no name.");
                    continue;
                }
                if (!byName.TryAdd(item.name, item))
                    result.Error("DuplicateAssembly", item.name);
            }

            ValidatePolicyNames(items, policy, byName, result);
            foreach (AssemblyPolicyDefinition consumer in items)
            {
                if (consumer.classification == AssemblyClassification.BuildFiltered) continue;
                foreach (string rawProvider in consumer.references ?? new string[0])
                {
                    string providerName = AssemblyNamePolicy.Canonical(rawProvider);
                    AssemblyPolicyDefinition provider;
                    if (!byName.TryGetValue(providerName, out provider))
                    {
                        if (consumer.entersPlayer && IsRuntime(consumer))
                            result.Error("UnresolvedRuntimeReference", consumer.name + " references unresolved runtime assembly " + providerName + ".");
                        continue;
                    }
                    if (consumer.entersPlayer && IsRuntime(consumer) && !provider.entersPlayer && provider.classification != AssemblyClassification.Reference)
                        result.Error("InvalidRuntimeReference", consumer.name + " references a non-Player assembly " + providerName + ".");
                    CheckFilteredReference(consumer, provider, result);
                    if (IsRuntime(provider))
                    {
                        InternalDependencyRule.Check(consumer, provider, policy, result);
                        if (consumer.entersPlayer && IsRuntime(consumer))
                            ExtensibilityWhitelistRule.Check(consumer, provider, policy, utcNow, result);
                    }
                    BootstrapIsolationRule.Check(consumer, provider, policy, result);
                }
                ValidateReflection(consumer, policy, byName, utcNow, result);
            }
            foreach (DeclaredRuntimeDependency edge in (policy.dependencies == null ? null : policy.dependencies.runtimeDependencies) ?? new DeclaredRuntimeDependency[0])
            {
                AssemblyPolicyDefinition consumer, provider;
                if (edge == null || !byName.TryGetValue(AssemblyNamePolicy.Canonical(edge.consumer), out consumer) ||
                    !byName.TryGetValue(AssemblyNamePolicy.Canonical(edge.provider), out provider)) continue;
                if (consumer.classification == AssemblyClassification.BuildFiltered) continue;
                InternalDependencyRule.Check(consumer, provider, policy, result);
                CheckFilteredReference(consumer, provider, result);
                if (consumer.entersPlayer && IsRuntime(consumer)) ExtensibilityWhitelistRule.Check(consumer, provider, policy, utcNow, result);
                BootstrapIsolationRule.Check(consumer, provider, policy, result);
            }
            return result;
        }

        private static void CheckFilteredReference(AssemblyPolicyDefinition consumer, AssemblyPolicyDefinition provider, ShadowPolicyValidationResult result)
        {
            if (IsRuntime(consumer) && provider.classification == AssemblyClassification.BuildFiltered)
                result.Error("RuntimeReferencesFilteredAssembly", consumer.name + " references an assembly removed from the captured Player build: " + provider.name + ".");
        }

        public static ShadowPolicyValidationResult ValidateBeforeCompile(ShadowPolicyConfiguration policy, BuildTarget target)
        {
            string project = Directory.GetParent(Application.dataPath).FullName;
            return ValidateBeforeCompile(policy, target, project);
        }

        public static ShadowPolicyValidationResult ValidateBeforeCompile(ShadowPolicyConfiguration policy,
            BuildTarget target, string projectRoot)
        {
            var parseErrors = new List<string>();
            policy = policy ?? new ShadowPolicyConfiguration();
            var definitions = ReadDefinitions(projectRoot, target, policy, parseErrors).ToList();
            // Compiler inventory also includes predefined script assemblies and
            // DLL references without asmdefs. Explicit candidates/bootstrap must
            // still be found in source or an actual precompiled inventory entry.
            var sourceNames = new HashSet<string>(definitions.Select(item => AssemblyNamePolicy.Canonical(item.name)), StringComparer.OrdinalIgnoreCase);
            foreach (AssemblyCapability capability in policy.assemblies ?? new AssemblyCapability[0])
                if (capability != null && !sourceNames.Contains(AssemblyNamePolicy.Canonical(capability.name)) &&
                    ((!capability.isShadowCapable && !capability.isBootstrap) || capability.isPrecompiled))
                    definitions.Add(new AssemblyPolicyDefinition
                    {
                        name = capability.name, classification = capability.classification,
                        entersPlayer = capability.classification == AssemblyClassification.Runtime || capability.classification == AssemblyClassification.NormalHotUpdate,
                        isShadowCapable = capability.isShadowCapable, isBootstrap = capability.isBootstrap,
                        isPrecompiled = capability.isPrecompiled, capabilityDeclared = capability.capabilityDeclared,
                    });
            ShadowPolicyValidationResult result = ValidateDefinitions(definitions, policy, DateTime.UtcNow);
            foreach (string error in parseErrors)
                result.Error("InvalidAssemblyDefinition", error);
            ValidateBootstrapResources(projectRoot, policy, result);
            return result;
        }

        private static void ValidatePolicyNames(IEnumerable<AssemblyPolicyDefinition> items, ShadowPolicyConfiguration policy,
            IDictionary<string, AssemblyPolicyDefinition> byName, ShadowPolicyValidationResult result)
        {
            var defined = new HashSet<string>(byName.Keys, StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AssemblyCapability capability in policy.assemblies ?? new AssemblyCapability[0])
            {
                if (capability == null || string.IsNullOrWhiteSpace(capability.name))
                    result.Error("InvalidCapability", "Assembly capability has no name.");
                else if (!seen.Add(AssemblyNamePolicy.Canonical(capability.name)))
                    result.Error("DuplicateCapability", capability.name);
                else if ((capability.isShadowCapable || capability.isBootstrap) && !defined.Contains(AssemblyNamePolicy.Canonical(capability.name)))
                    result.Error("CapabilityNotInSnapshot", "Configured assembly is absent from compiled definitions: " + capability.name + ".");
            }
            foreach (AssemblyPolicyDefinition item in items)
            {
                if ((item.isShadowCapable || item.isBootstrap) && (item.classification != AssemblyClassification.Runtime || !item.entersPlayer))
                    result.Error("InvalidCandidateClassification", item.name + " is not an included AOT runtime input.");
                if (item.isShadowCapable && item.isBootstrap)
                    result.Error("ConflictingCapability", item.name + " is both bootstrap and shadow-capable.");
                if (item.isPrecompiled && IsRuntime(item) && !item.capabilityDeclared)
                    result.Error("UndeclaredPrecompiledCapability", "External runtime DLL requires an explicit true/false shadow capability: " + item.name);
                if (item.isPrecompiled && item.classification == AssemblyClassification.Runtime && !item.entersPlayer)
                    result.Error("RuntimeAssemblyMissingFromSnapshot", "Runtime plugin is only a resolver reference, not an actual Player input: " + item.name);
            }
            ValidateDependencies(policy, byName, result);
            ValidateWhitelist(policy, result);
        }

        private static void ValidateDependencies(ShadowPolicyConfiguration policy,
            IDictionary<string, AssemblyPolicyDefinition> byName, ShadowPolicyValidationResult result)
        {
            var dependencies = policy.dependencies;
            if (dependencies == null) return;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DeclaredRuntimeDependency edge in dependencies.runtimeDependencies ?? new DeclaredRuntimeDependency[0])
            {
                if (edge == null || string.IsNullOrWhiteSpace(edge.consumer) || string.IsNullOrWhiteSpace(edge.provider) ||
                    string.IsNullOrWhiteSpace(edge.kind) || string.IsNullOrWhiteSpace(edge.evidence))
                {
                    result.Error("InvalidDependency", "Explicit runtime dependencies require consumer, provider, kind and evidence.");
                    continue;
                }
                string key = AssemblyNamePolicy.Canonical(edge.consumer) + "\n" + AssemblyNamePolicy.Canonical(edge.provider);
                if (string.Equals(AssemblyNamePolicy.Canonical(edge.consumer), AssemblyNamePolicy.Canonical(edge.provider), StringComparison.OrdinalIgnoreCase))
                    result.Error("SelfDependency", edge.consumer);
                if (!seen.Add(key)) result.Error("DuplicateDependency", edge.consumer + " -> " + edge.provider);
                if (!byName.ContainsKey(AssemblyNamePolicy.Canonical(edge.consumer)) || !byName.ContainsKey(AssemblyNamePolicy.Canonical(edge.provider)))
                    result.Error("UnknownDependency", edge.consumer + " -> " + edge.provider);
            }
            var entryKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (BootstrapEntrypointDeclaration entry in dependencies.bootstrapEntrypoints ?? new BootstrapEntrypointDeclaration[0])
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.consumer) || string.IsNullOrWhiteSpace(entry.provider) ||
                    string.IsNullOrWhiteSpace(entry.typeName) || string.IsNullOrWhiteSpace(BootstrapIsolationRule.CallSite(entry)) || string.IsNullOrWhiteSpace(entry.reason))
                {
                    result.Error("InvalidEntrypoint", "Bootstrap entrypoints require consumer, provider, typeName, method and reason.");
                    continue;
                }
                string consumer = AssemblyNamePolicy.Canonical(entry.consumer);
                string provider = AssemblyNamePolicy.Canonical(entry.provider);
                string key = consumer + "\n" + provider + "\n" + entry.typeName + "\n" + BootstrapIsolationRule.CallSite(entry) + "\n" + entry.target;
                if (!entryKeys.Add(key)) result.Error("DuplicateEntrypoint", entry.consumer + " -> " + entry.method);
                AssemblyPolicyDefinition consumerDefinition;
                AssemblyPolicyDefinition providerDefinition;
                if (!byName.TryGetValue(consumer, out consumerDefinition) || !consumerDefinition.isBootstrap)
                    result.Error("InvalidEntrypoint", "Entrypoint consumer is not a configured bootstrap assembly: " + entry.consumer);
                if (!byName.TryGetValue(provider, out providerDefinition) || !providerDefinition.isShadowCapable)
                    result.Error("InvalidEntrypoint", "Entrypoint provider is not a shadow-capable assembly: " + entry.provider);
                if (BootstrapIsolationRule.CallSite(entry).IndexOf("::", StringComparison.Ordinal) <= 0)
                    result.Error("InvalidEntrypoint", "Entrypoint callsite must be Type::Method: " + BootstrapIsolationRule.CallSite(entry));
            }
            foreach (DeclaredResourceDependency edge in dependencies.resourceDependencies ?? new DeclaredResourceDependency[0])
                if (edge == null || string.IsNullOrWhiteSpace(edge.bundle) || !byName.ContainsKey(AssemblyNamePolicy.Canonical(edge.assembly)))
                    result.Error("InvalidResourceDependency", "Resource dependency requires a bundle and a known assembly.");
        }

        private static void ValidateWhitelist(ShadowPolicyConfiguration policy, ShadowPolicyValidationResult result)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ExtensibilityWhitelistEntry entry in (policy.extensibilityWhitelist == null ? null : policy.extensibilityWhitelist.entries) ?? new ExtensibilityWhitelistEntry[0])
            {
                if (entry == null || string.IsNullOrWhiteSpace(entry.provider) || string.IsNullOrWhiteSpace(entry.consumer) ||
                    string.IsNullOrWhiteSpace(entry.owner) || string.IsNullOrWhiteSpace(entry.reason) || string.IsNullOrWhiteSpace(entry.reviewer) ||
                    string.IsNullOrWhiteSpace(entry.expires))
                {
                    result.Error("InvalidWhitelist", "Extensibility whitelist entries require provider, consumer, owner, reason, reviewer and expiry.");
                    continue;
                }
                string key = AssemblyNamePolicy.Canonical(entry.provider) + "\n" + AssemblyNamePolicy.Canonical(entry.consumer);
                if (!seen.Add(key)) result.Error("DuplicateWhitelist", entry.provider + " -> " + entry.consumer);
            }
        }

        private static void ValidateReflection(AssemblyPolicyDefinition consumer, ShadowPolicyConfiguration policy,
            IDictionary<string, AssemblyPolicyDefinition> byName, DateTime utcNow, ShadowPolicyValidationResult result)
        {
            if (!consumer.entersPlayer || !IsRuntime(consumer)) return;
            foreach (var acquisition in consumer.managedAcquisitions ?? new ManagedAcquisitionEvidence[0])
                if (acquisition.requiresContract && !acquisition.verified)
                    result.Error("UnboundedManagedAcquisition", consumer.name + " " + acquisition.kind + " at " + acquisition.methodSignature +
                        " operation " + acquisition.operationIndex + " [" + acquisition.methodHash + "] calls " + acquisition.operationSignature +
                        "; method-level prose cannot authorize this operation.");
            if (consumer.unknownReflectionDependencies && policy.rejectUnknownReflectionDependencies)
                result.Error("UnknownReflectionDependency", consumer.name + " contains dynamic/unknown reflection dependencies.");
            foreach (string callSite in consumer.unknownReflectionCallSites ?? new string[0])
            {
                bool declared = !consumer.isBootstrap && policy.dependencies != null &&
                    (policy.dependencies.runtimeDependencies ?? new DeclaredRuntimeDependency[0]).Any(edge => edge != null &&
                        string.Equals(AssemblyNamePolicy.Canonical(edge.consumer), consumer.name, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(edge.callSite, callSite, StringComparison.Ordinal) && byName.ContainsKey(AssemblyNamePolicy.Canonical(edge.provider)) &&
                        !string.IsNullOrWhiteSpace(edge.kind) && !string.IsNullOrWhiteSpace(edge.evidence));
                if (!declared && (consumer.isBootstrap || policy.rejectUnknownReflectionDependencies))
                    result.Error("UnknownReflectionDependency", consumer.name + " has an unbounded reflection/SerializeReference dependency at " + callSite + ".");
            }
            var evidence = new List<ReflectionDependencyEvidence>(consumer.reflectionDependencies ?? new ReflectionDependencyEvidence[0]);
            foreach (string reference in consumer.reflectionReferences ?? new string[0])
            {
                int separator = (reference ?? string.Empty).IndexOf('|');
                string argument = separator < 0 ? reference : reference.Substring(separator + 1);
                string assembly = ReflectionDependencyScanner.AssemblyNameFromQualifiedType(argument);
                if (string.IsNullOrEmpty(assembly) && byName.ContainsKey(AssemblyNamePolicy.Canonical(argument))) assembly = AssemblyNamePolicy.Canonical(argument);
                evidence.Add(new ReflectionDependencyEvidence { callSite = separator < 0 ? "" : reference.Substring(0, separator), target = argument, provider = assembly });
            }
            foreach (ReflectionDependencyEvidence dependency in evidence)
            {
                string reference = dependency.callSite + "|" + dependency.target;
                AssemblyPolicyDefinition providerDefinition;
                bool known = !string.IsNullOrEmpty(dependency.provider) && byName.TryGetValue(dependency.provider, out providerDefinition);
                if (!string.IsNullOrEmpty(dependency.provider) && byName.TryGetValue(dependency.provider, out providerDefinition))
                    CheckFilteredReference(consumer, providerDefinition, result);
                if (consumer.isBootstrap)
                {
                    bool stableReference = !string.IsNullOrEmpty(dependency.provider) && byName.TryGetValue(dependency.provider, out providerDefinition) &&
                        providerDefinition.classification == AssemblyClassification.Reference;
                    if (!stableReference && !BootstrapIsolationRule.IsApprovedReflection(consumer, reference, policy, utcNow, dependency.provider, dependency.typeName))
                        result.Error("BootstrapReflection", "Bootstrap reflection reference is not an approved entrypoint: " + consumer.name + " -> " + reference);
                    continue;
                }
                if (!known && policy.rejectUnknownReflectionDependencies)
                    result.Error("UnknownReflectionDependency", consumer.name + " reflection reference is not a compiled or declared dependency: " + reference);
                else if (known && !string.Equals(consumer.name, dependency.provider, StringComparison.OrdinalIgnoreCase) &&
                    !HasDeclaredDependency(policy, consumer.name, dependency.provider) &&
                    !(consumer.references ?? new string[0]).Any(item => string.Equals(AssemblyNamePolicy.Canonical(item), dependency.provider, StringComparison.OrdinalIgnoreCase)))
                    result.Error("UndeclaredReflectionDependency", consumer.name + " reflection reference requires explicit dependency evidence: " + dependency.provider);
            }
        }

        private static bool HasDeclaredDependency(ShadowPolicyConfiguration policy, string consumer, string provider)
        {
            return policy.dependencies != null && (policy.dependencies.runtimeDependencies ?? new DeclaredRuntimeDependency[0]).Any(edge => edge != null &&
                string.Equals(AssemblyNamePolicy.Canonical(edge.consumer), AssemblyNamePolicy.Canonical(consumer), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(AssemblyNamePolicy.Canonical(edge.provider), AssemblyNamePolicy.Canonical(provider), StringComparison.OrdinalIgnoreCase));
        }

        private static AssemblyCapability FindCapability(ShadowPolicyConfiguration policy, string provider)
        {
            return (policy == null ? new AssemblyCapability[0] : policy.assemblies ?? new AssemblyCapability[0]).FirstOrDefault(item => item != null &&
                string.Equals(AssemblyNamePolicy.Canonical(item.name), AssemblyNamePolicy.Canonical(provider), StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsRuntime(AssemblyPolicyDefinition item)
        {
            return item.classification == AssemblyClassification.Runtime || item.classification == AssemblyClassification.NormalHotUpdate;
        }

        private static AssemblyPolicyDefinition FromDescriptor(AssemblyDescriptor descriptor)
        {
            var item = new AssemblyPolicyDefinition
            {
                name = descriptor == null ? string.Empty : descriptor.name,
                references = descriptor == null ? new string[0] : descriptor.references ?? new string[0],
                isShadowCapable = descriptor != null && descriptor.isShadowCapable,
                isBootstrap = descriptor != null && descriptor.isBootstrap,
                isPrecompiled = descriptor != null && descriptor.isPrecompiled,
                capabilityDeclared = descriptor != null && descriptor.capabilityDeclared,
                classification = descriptor == null ? AssemblyClassification.Runtime : descriptor.classification,
                entersPlayer = descriptor == null || descriptor.classification == AssemblyClassification.Runtime || descriptor.classification == AssemblyClassification.NormalHotUpdate,
            };
            return item;
        }

        private static IEnumerable<AssemblyPolicyDefinition> ReadDefinitions(string projectRoot, BuildTarget target,
            ShadowPolicyConfiguration policy, List<string> parseErrors)
        {
            bool currentProject = IsCurrentProject(projectRoot);
            var files = currentProject
                ? ImportedAssetPaths(".asmdef").ToDictionary(path => path, path => ResolveAssetPath(projectRoot, path), StringComparer.Ordinal)
                : SourceRoots(projectRoot).SelectMany(root => Directory.GetFiles(root, "*.asmdef", SearchOption.AllDirectories))
                    .Select(Path.GetFullPath).Distinct(StringComparer.Ordinal).ToDictionary(path => path, path => path, StringComparer.Ordinal);
            var production = new Dictionary<string, UnityEditor.Compilation.Assembly>(StringComparer.OrdinalIgnoreCase);
            var compiler = new Dictionary<string, UnityEditor.Compilation.Assembly>(StringComparer.OrdinalIgnoreCase);
            if (currentProject)
            {
                UnityEditor.Compilation.Assembly[] player = CompilationPipeline.GetAssemblies(AssembliesType.PlayerWithoutTestAssemblies);
                UnityEditor.Compilation.Assembly[] editor = CompilationPipeline.GetAssemblies(AssembliesType.Editor);
                ShadowHash.Require(player != null && editor != null, "CompilerInventoryUnavailable", "Unity returned no production or Editor assembly inventory.");
                foreach (var assembly in editor) compiler.Add(AssemblyNamePolicy.Canonical(assembly.name), assembly);
                foreach (var assembly in player)
                {
                    string name = AssemblyNamePolicy.Canonical(assembly.name);
                    production.Add(name, assembly); compiler[name] = assembly;
                }
            }
            var guidNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                string guid = currentProject ? AssetDatabase.AssetPathToGUID(file.Key) : ReadMetaGuid(file.Value + ".meta");
                var asmdef = ReadAsmdef(file.Value);
                if (asmdef == null || string.IsNullOrWhiteSpace(asmdef.name))
                {
                    parseErrors.Add("Malformed or unnamed imported asmdef: " + file.Value);
                    continue;
                }
                if (!string.IsNullOrEmpty(guid))
                {
                    if (guidNames.ContainsKey(guid)) parseErrors.Add("Duplicate imported asmdef GUID " + guid + " in " + file.Value);
                    else guidNames.Add(guid, asmdef.name);
                }
            }
            var controlled = new HashSet<string>((policy.assemblies ?? new AssemblyCapability[0])
                .Where(item => item != null && (item.isShadowCapable || item.isBootstrap)).Select(item => AssemblyNamePolicy.Canonical(item.name)), StringComparer.OrdinalIgnoreCase);
            foreach (var file in files.OrderBy(item => item.Key, StringComparer.Ordinal))
            {
                var asmdef = ReadAsmdef(file.Value);
                if (asmdef == null || string.IsNullOrWhiteSpace(asmdef.name)) continue;
                string name = AssemblyNamePolicy.Canonical(asmdef.name);
                UnityEditor.Compilation.Assembly compiled;
                compiler.TryGetValue(name, out compiled);
                string[] effectiveReferences = compiled == null ? new string[0] :
                    (compiled.assemblyReferences ?? new UnityEditor.Compilation.Assembly[0]).Select(item => item.name)
                    .Concat((compiled.compiledAssemblyReferences ?? new string[0]).Select(Path.GetFileNameWithoutExtension)).ToArray();
                // Package asmdefs can retain references to absent optional
                // packages. Unity's effective graph is authoritative for those
                // edges. Project source and configured boundaries remain strict;
                // every known candidate edge is retained even if unused by Unity.
                bool strict = !currentProject || file.Key.StartsWith("Assets/", StringComparison.Ordinal) || controlled.Contains(name);
                string[] references = SelectPreflightReferences((asmdef.references ?? new string[0]).Concat(asmdef.precompiledReferences ?? new string[0]),
                    guidNames, effectiveReferences, controlled, strict, parseErrors, file.Key);
                bool entersPlayer = PlatformMatches(asmdef, target, parseErrors, file.Value);
                bool editorAssembly = (asmdef.includePlatforms ?? new string[0]).Length > 0 &&
                    asmdef.includePlatforms.All(item => string.Equals(item, "Editor", StringComparison.OrdinalIgnoreCase));
                AssemblyCapability capability = (policy.assemblies ?? new AssemblyCapability[0]).FirstOrDefault(item => item != null &&
                    string.Equals(AssemblyNamePolicy.Canonical(item.name), AssemblyNamePolicy.Canonical(asmdef.name), StringComparison.OrdinalIgnoreCase));
                bool testAssembly = (capability != null && capability.classification == AssemblyClassification.TestOnly) ||
                    (asmdef.optionalUnityReferences ?? new string[0]).Any(item => string.Equals(item, "TestAssemblies", StringComparison.OrdinalIgnoreCase));
                yield return new AssemblyPolicyDefinition
                {
                    name = asmdef.name,
                    references = references,
                    classification = testAssembly ? AssemblyClassification.TestOnly : (editorAssembly ? AssemblyClassification.EditorOnly :
                        (capability == null ? AssemblyClassification.Runtime : capability.classification)),
                    entersPlayer = (currentProject ? production.ContainsKey(name) : entersPlayer) && !editorAssembly && !testAssembly,
                    isShadowCapable = capability != null && capability.isShadowCapable,
                    isBootstrap = capability != null && capability.isBootstrap,
                    capabilityDeclared = capability != null && capability.capabilityDeclared,
                    sourcePath = file.Value,
                };
            }
        }

        internal static string[] SelectPreflightReferences(IEnumerable<string> declaredReferences, IDictionary<string, string> guidNames,
            IEnumerable<string> compilerReferences, ISet<string> controlledNames, bool strict, List<string> errors, string sourcePath)
        {
            var effective = new HashSet<string>((compilerReferences ?? new string[0]).Select(AssemblyNamePolicy.Canonical), StringComparer.OrdinalIgnoreCase);
            var selected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in declaredReferences)
            {
                string provider = AssemblyNamePolicy.Canonical(raw);
                if (string.IsNullOrWhiteSpace(provider)) { errors.Add("Empty asmdef reference in " + sourcePath); continue; }
                if (provider.StartsWith("GUID:", StringComparison.OrdinalIgnoreCase))
                {
                    string guid = provider.Substring(5).Trim();
                    if (!Regex.IsMatch(guid, @"\A[0-9a-fA-F]{32}\z"))
                    { errors.Add("Malformed asmdef GUID reference " + raw + " in " + sourcePath); continue; }
                    if (!guidNames.TryGetValue(guid, out provider))
                    {
                        if (strict) errors.Add("Unresolved asmdef GUID reference " + raw + " in " + sourcePath);
                        continue;
                    }
                    provider = AssemblyNamePolicy.Canonical(provider);
                }
                if (strict || controlledNames.Contains(provider) || effective.Contains(provider)) selected.Add(provider);
            }
            // A compiler-observed edge to a configured boundary cannot disappear
            // merely because an optional/raw GUID did not resolve in the source map.
            foreach (string provider in effective.Where(controlledNames.Contains)) selected.Add(provider);
            return selected.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        }

        private static string[] ImportedAssetPaths(params string[] extensions)
        {
            string[] paths = AssetDatabase.GetAllAssetPaths();
            ShadowHash.Require(paths != null, "ImportedAssetInventoryUnavailable", "Unity returned no imported asset inventory.");
            return FilterImportedAssetPaths(paths, extensions);
        }

        internal static string[] FilterImportedAssetPaths(IEnumerable<string> importedPaths, params string[] extensions)
        {
            return importedPaths.Where(path => !string.IsNullOrWhiteSpace(path) &&
                (path.StartsWith("Assets/", StringComparison.Ordinal) || path.StartsWith("Packages/", StringComparison.Ordinal)) &&
                extensions.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)))
                .Distinct(StringComparer.Ordinal).OrderBy(path => path, StringComparer.Ordinal).ToArray();
        }

        private static bool IsCurrentProject(string projectRoot)
        {
            return string.Equals(Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar),
                Directory.GetParent(Application.dataPath).FullName, StringComparison.Ordinal);
        }

        private static IEnumerable<string> SourceRoots(string projectRoot)
        {
            // Only used by detached filesystem fixtures. For the live project,
            // AssetDatabase owns imported package/local/registry asset identity.
            var roots = new HashSet<string>(StringComparer.Ordinal)
            { Path.Combine(projectRoot, "Assets"), Path.Combine(projectRoot, "Packages") };
            return roots.Where(Directory.Exists).OrderBy(path => path, StringComparer.Ordinal);
        }

        internal static string ResolveAssetPath(string projectRoot, string assetPath)
        {
            string local = Path.IsPathRooted(assetPath) ? assetPath : Path.Combine(projectRoot, assetPath);
            if (File.Exists(local) || Directory.Exists(local)) return local;
            if (IsCurrentProject(projectRoot) && assetPath.StartsWith("Packages/", StringComparison.Ordinal))
            {
                PackageInfo package = PackageInfo.FindForAssetPath(assetPath);
                if (package != null && !string.IsNullOrWhiteSpace(package.resolvedPath))
                    return Path.Combine(package.resolvedPath, assetPath.Substring(("Packages/" + package.name + "/").Length));
            }
            return local;
        }

        private static void ValidateBootstrapResources(string projectRoot, ShadowPolicyConfiguration policy,
            ShadowPolicyValidationResult result)
        {
            var resourcePaths = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                EditorBuildSettingsScene scene = (EditorBuildSettings.scenes ?? new EditorBuildSettingsScene[0])
                    .FirstOrDefault(item => item != null && item.enabled);
                var roots = new List<string>();
                if (scene != null) roots.Add(scene.path);
                foreach (UnityEngine.Object asset in PlayerSettings.GetPreloadedAssets() ?? new UnityEngine.Object[0])
                {
                    if (asset == null) continue;
                    string path = AssetDatabase.GetAssetPath(asset);
                    if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("A preloaded asset has no physical asset path.");
                    roots.Add(path);
                }
                foreach (string root in roots)
                    foreach (string dependency in new[] { root }.Concat(AssetDatabase.GetDependencies(root, true) ?? new string[0]))
                    {
                        string physical = ResolveAssetPath(projectRoot, dependency);
                        if (File.Exists(physical)) resourcePaths.Add(Path.GetFullPath(physical));
                        else if (dependency == root) result.Error("BootstrapResourceMissing", root);
                    }
            }
            catch (Exception exception) { result.Error("BootstrapResourceInventoryFailed", exception.Message); }
            ValidateBootstrapResources(resourcePaths, projectRoot, policy, result);
        }

        public static void ValidateBootstrapResources(IEnumerable<string> resourcePaths, string projectRoot,
            ShadowPolicyConfiguration policy, ShadowPolicyValidationResult result)
        {
            var scriptAssemblies = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var physicalSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            bool currentProject = IsCurrentProject(projectRoot);
            string[] sourceRoots = currentProject ? new string[0] : SourceRoots(projectRoot).ToArray();
            foreach (string source in sourceRoots.SelectMany(root => Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
                .Concat(Directory.GetFiles(root, "*.dll", SearchOption.AllDirectories))).Distinct(StringComparer.Ordinal))
            {
                string guid = ReadMetaGuid(source + ".meta");
                if (string.IsNullOrEmpty(guid)) continue;
                if (physicalSources.ContainsKey(guid)) result.Error("AmbiguousScriptGuid", guid);
                else physicalSources.Add(guid, source);
            }
            var capabilities = (policy.assemblies ?? new AssemblyCapability[0]).Where(item => item != null)
                .GroupBy(item => AssemblyNamePolicy.Canonical(item.name), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            foreach (string asset in (resourcePaths ?? Enumerable.Empty<string>()).Select(Path.GetFullPath).Distinct(StringComparer.Ordinal)
                .Where(path => path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)))
            {
                string text;
                try { text = File.ReadAllText(asset); }
                catch (Exception exception) { result.Error("BootstrapResourceUnreadable", asset + ": " + exception.Message); continue; }
                if (!text.StartsWith("%YAML", StringComparison.Ordinal) && !text.StartsWith("--- !u!", StringComparison.Ordinal))
                { result.Error("BootstrapResourceUninspectable", "Bootstrap serialized resource must use text serialization: " + asset); continue; }
                foreach (Match match in Regex.Matches(text, @"m_Script\s*:\s*\{([^}]*)\}"))
                {
                    string fields = match.Groups[1].Value;
                    if (Regex.IsMatch(fields, @"(?:^|,)\s*fileID\s*:\s*0\s*(?:,|$)")) continue;
                    Match guidMatch = Regex.Match(fields, @"(?:^|,)\s*guid\s*:\s*([0-9a-fA-F]{32})\s*(?:,|$)");
                    if (!guidMatch.Success) { result.Error("BootstrapScriptGuidInvalid", asset); continue; }
                    string guid = guidMatch.Groups[1].Value;
                    string assembly;
                    if (!scriptAssemblies.TryGetValue(guid, out assembly))
                    {
                        string source;
                        if (currentProject)
                        {
                            // Resolve only the actual startup resource's GUID.
                            // Package Samples~/source~ copies are not imported
                            // assets and must never create global false collisions.
                            string imported = AssetDatabase.GUIDToAssetPath(guid);
                            string physical = string.IsNullOrEmpty(imported) ? null : ResolveAssetPath(projectRoot, imported);
                            if (FilterImportedAssetPaths(new[] { imported }, ".cs", ".dll").Length != 1 ||
                                !string.Equals(AssetDatabase.AssetPathToGUID(imported), guid, StringComparison.OrdinalIgnoreCase) ||
                                string.IsNullOrEmpty(physical) || !File.Exists(physical) ||
                                !string.Equals(ReadMetaGuid(physical + ".meta"), guid, StringComparison.OrdinalIgnoreCase))
                            { result.Error("BootstrapScriptGuidUnresolved", asset + " -> " + guid + " is not a physical imported script or managed DLL."); continue; }
                            physicalSources[guid] = physical;
                        }
                        if (!physicalSources.TryGetValue(guid, out source) || !File.Exists(source))
                        { result.Error("BootstrapScriptGuidUnresolved", asset + " -> " + guid); continue; }
                        try
                        {
                            if (source.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                            {
                                using (ModuleDefMD module = ModuleDefMD.Load(File.ReadAllBytes(source)))
                                    assembly = module.Assembly == null ? null : module.Assembly.Name.String;
                            }
                            else if (currentProject)
                            {
                                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                                assembly = string.IsNullOrEmpty(assetPath) ? null : AssemblyNamePolicy.Canonical(CompilationPipeline.GetAssemblyNameFromScriptPath(assetPath));
                            }
                            else assembly = FindNearestAssemblyDefinition(Path.GetDirectoryName(source), sourceRoots);
                        }
                        catch (Exception exception) { result.Error("BootstrapScriptIdentityFailed", source + ": " + exception.Message); continue; }
                        if (string.IsNullOrWhiteSpace(assembly)) { result.Error("BootstrapScriptIdentityUnknown", source); continue; }
                        scriptAssemblies[guid] = assembly;
                    }
                    AssemblyCapability capability;
                    if (capabilities.TryGetValue(AssemblyNamePolicy.Canonical(assembly), out capability) &&
                        capability.isShadowCapable)
                        result.Error("BootstrapResourceBusinessScript", "Bootstrap resource " + asset + " references shadow business assembly " + assembly + ".");
                }
                // Unity SerializeReference YAML includes an explicit assembly
                // name. Business managed references are also bootstrap business
                // resources even when their owning MonoBehaviour is stable.
                foreach (Match match in Regex.Matches(text, @"\btype\s*:\s*\{[^}]*\basm\s*:\s*([^,}\r\n]+)"))
                {
                    string name = AssemblyNamePolicy.Canonical(match.Groups[1].Value.Trim().Trim('\'', '"'));
                    AssemblyCapability capability;
                    if (!capabilities.TryGetValue(name, out capability)) result.Error("UnknownSerializeReferenceAssembly", asset + " -> " + name);
                    else if (capability.isShadowCapable) result.Error("BootstrapResourceBusinessScript", asset + " has a managed reference to " + name + ".");
                }
            }
        }

        private static string FindNearestAssemblyDefinition(string directory, string[] roots)
        {
            DirectoryInfo current = string.IsNullOrEmpty(directory) ? null : new DirectoryInfo(directory);
            while (current != null)
            {
                string[] definitions = Directory.GetFiles(current.FullName, "*.asmdef", SearchOption.TopDirectoryOnly);
                if (definitions.Length > 1) throw new InvalidOperationException("Multiple asmdefs in " + current.FullName);
                if (definitions.Length == 1)
                {
                    AsmdefData data = ReadAsmdef(definitions.OrderBy(path => path, StringComparer.Ordinal).First());
                    return data == null ? null : data.name;
                }
                if (roots.Any(root => string.Equals(Path.GetFullPath(root), current.FullName, StringComparison.Ordinal))) break;
                current = current.Parent;
            }
            return null;
        }

        private static bool PlatformMatches(AsmdefData data, BuildTarget target, List<string> errors, string path)
        {
            AssemblyDefinitionPlatform[] platforms = CompilationPipeline.GetAssemblyDefinitionPlatforms();
            ShadowHash.Require(platforms != null, "PlatformInventoryUnavailable", "Unity asmdef platform inventory is unavailable.");
            var known = new HashSet<string>(platforms.Select(platform => platform.Name), StringComparer.OrdinalIgnoreCase) { "Editor" };
            foreach (string platform in (data.includePlatforms ?? new string[0]).Concat(data.excludePlatforms ?? new string[0]))
                if (platform == null || !known.Contains(platform)) errors.Add("Unknown asmdef platform " + platform + " in " + path);
            if ((data.includePlatforms ?? new string[0]).Length > 0 && (data.excludePlatforms ?? new string[0]).Length > 0)
                errors.Add("Asmdef cannot combine includePlatforms and excludePlatforms: " + path);
            var targetNames = new HashSet<string>(platforms.Where(platform => platform.BuildTarget == target).Select(platform => platform.Name), StringComparer.OrdinalIgnoreCase);
            bool included = data.includePlatforms == null || data.includePlatforms.Length == 0 || data.includePlatforms.Any(item =>
                targetNames.Contains(item));
            bool excluded = (data.excludePlatforms ?? new string[0]).Any(item =>
                targetNames.Contains(item));
            return included && !excluded;
        }

        private static AsmdefData ReadAsmdef(string path)
        {
            try { return JsonUtility.FromJson<AsmdefData>(File.ReadAllText(path)); }
            catch (Exception) { return null; }
        }

        private static string ReadMetaGuid(string path)
        {
            if (!File.Exists(path)) return null;
            foreach (string line in File.ReadAllLines(path))
                if (line.StartsWith("guid:", StringComparison.OrdinalIgnoreCase)) return line.Substring(5).Trim();
            return null;
        }

        [Serializable]
        private sealed class AsmdefData
        {
            public string name;
            public string[] references;
            public string[] precompiledReferences;
            public string[] includePlatforms;
            public string[] excludePlatforms;
            public string[] optionalUnityReferences;
        }
    }
}
