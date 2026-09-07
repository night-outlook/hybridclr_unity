using System;
using System.Collections.Generic;
using System.Linq;
using HybridCLR.AssemblyShadow.CodeGen;

namespace HybridCLR.Editor.AssemblyShadow
{
    [Serializable]
    public sealed class AssemblyDependencyEdge
    {
        public string consumer;
        public string provider;
        public string kind;
        public string evidence;
    }

    /// <summary>Edges point from caller to provider; a patch follows the reverse edges.</summary>
    public sealed class AssemblyReferenceGraph
    {
        private readonly Dictionary<string, AssemblyDescriptor> assemblies;
        private readonly Dictionary<string, SortedSet<string>> forward = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        private readonly Dictionary<string, SortedSet<string>> reverse = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        private readonly List<AssemblyDependencyEdge> edges = new List<AssemblyDependencyEdge>();

        public AssemblyDependencyEdge[] Edges { get { return edges.OrderBy(e => e.consumer, StringComparer.Ordinal).ThenBy(e => e.provider, StringComparer.Ordinal).ThenBy(e => e.kind, StringComparer.Ordinal).ToArray(); } }

        public AssemblyReferenceGraph(IEnumerable<AssemblyDescriptor> descriptors, ShadowDependencyConfiguration config = null, IEnumerable<AssemblyDependencyEdge> requiredBaselineEdges = null)
        {
            assemblies = new Dictionary<string, AssemblyDescriptor>(StringComparer.Ordinal);
            foreach (var item in descriptors)
            {
                string key = AssemblyIdentityUtil.CanonicalName(item.name);
                ShadowHash.Require(!assemblies.ContainsKey(key), "DuplicateAssembly", item.name);
                assemblies.Add(key, item);
                forward.Add(key, new SortedSet<string>(StringComparer.Ordinal));
                reverse.Add(key, new SortedSet<string>(StringComparer.Ordinal));
            }
            foreach (var item in assemblies.Values.Where(IsRuntime))
                foreach (string reference in item.references ?? new string[0])
                {
                    string provider = AssemblyIdentityUtil.CanonicalName(reference);
                    if (assemblies.ContainsKey(provider) && IsRuntime(assemblies[provider]))
                        AddEdge(AssemblyIdentityUtil.CanonicalName(item.name), provider, "AssemblyRef", "Compiled target assembly reference");
                }
            MergeDeclarations(config ?? new ShadowDependencyConfiguration());
            // Deleting a declaration must not erase an unchanged baseline caller.
            foreach (var edge in requiredBaselineEdges ?? new AssemblyDependencyEdge[0])
            {
                string consumer = AssemblyIdentityUtil.CanonicalName(edge.consumer);
                string provider = AssemblyIdentityUtil.CanonicalName(edge.provider);
                RequireKnown(consumer); RequireKnown(provider);
                if (!forward[consumer].Contains(provider)) AddEdge(consumer, provider, "Baseline:" + edge.kind, edge.evidence);
            }
        }

        private static bool IsRuntime(AssemblyDescriptor descriptor) { return descriptor.classification == AssemblyClassification.Runtime || descriptor.classification == AssemblyClassification.NormalHotUpdate; }

        private void AddEdge(string consumer, string provider, string kind, string evidence)
        {
            ShadowHash.Require(consumer != provider, "SelfDependency", assemblies[consumer].name);
            forward[consumer].Add(provider);
            reverse[provider].Add(consumer);
            edges.Add(new AssemblyDependencyEdge { consumer = assemblies[consumer].name, provider = assemblies[provider].name, kind = kind, evidence = evidence });
        }

        private void MergeDeclarations(ShadowDependencyConfiguration config)
        {
            ShadowHash.Require(config.schemaVersion == 1 || config.schemaVersion == 2, "DependencySchema", "Unsupported explicit dependency schema.");
            var declared = new HashSet<string>(StringComparer.Ordinal);
            foreach (var edge in config.runtimeDependencies ?? new DeclaredRuntimeDependency[0])
            {
                ShadowHash.Require(edge != null, "InvalidDependency", "Null dependency entry.");
                string consumer = AssemblyIdentityUtil.CanonicalName(edge.consumer);
                string provider = AssemblyIdentityUtil.CanonicalName(edge.provider);
                RequireKnown(consumer); RequireKnown(provider);
                ShadowHash.Require(IsRuntime(assemblies[consumer]) && IsRuntime(assemblies[provider]), "InvalidDependency", "Explicit runtime dependency must name runtime assemblies: " + edge.consumer + " -> " + edge.provider);
                ShadowHash.Require(!string.IsNullOrWhiteSpace(edge.kind) && !string.IsNullOrWhiteSpace(edge.evidence), "InvalidDependency", "Dependency kind and evidence are required.");
                ShadowHash.Require(declared.Add(consumer + "\n" + provider) && !forward[consumer].Contains(provider), "DuplicateDependency", edge.consumer + " -> " + edge.provider);
                AddEdge(consumer, provider, edge.kind, edge.evidence);
            }
            var resources = new HashSet<string>(StringComparer.Ordinal);
            foreach (var edge in config.resourceDependencies ?? new DeclaredResourceDependency[0])
            {
                ShadowHash.Require(edge != null && !string.IsNullOrWhiteSpace(edge.bundle), "InvalidResourceDependency", "A bundle name is required.");
                string provider = AssemblyIdentityUtil.CanonicalName(edge.assembly);
                RequireKnown(provider);
                ShadowHash.Require(resources.Add(edge.bundle + "\n" + provider), "DuplicateResourceDependency", edge.bundle + " -> " + edge.assembly);
            }
            var managedReferences = new HashSet<string>(StringComparer.Ordinal);
            var serializeReferenceDependencies = config.serializeReferenceDependencies ?? new DeclaredSerializeReferenceDependency[0];
            ShadowHash.Require(config.schemaVersion >= 2 || serializeReferenceDependencies.Length == 0, "DependencySchema",
                "SerializeReference declarations require dependency schema 2.");
            foreach (var declaration in serializeReferenceDependencies)
            {
                ShadowHash.Require(declaration != null && !string.IsNullOrWhiteSpace(declaration.consumer) &&
                    !string.IsNullOrWhiteSpace(declaration.callSite) && declaration.callSite.IndexOf("::", StringComparison.Ordinal) > 0 &&
                    !string.IsNullOrWhiteSpace(declaration.evidence) && declaration.concreteTypes != null && declaration.concreteTypes.Length > 0,
                    "InvalidSerializeReferenceDependency", "SerializeReference declarations require a consumer, Type::field callsite, concrete types and evidence.");
                string consumer = AssemblyIdentityUtil.CanonicalName(declaration.consumer);
                RequireKnown(consumer);
                ShadowHash.Require(IsRuntime(assemblies[consumer]), "InvalidSerializeReferenceDependency",
                    "SerializeReference consumer must be a runtime assembly: " + declaration.consumer);
                ShadowHash.Require(managedReferences.Add(consumer + "\n" + declaration.callSite), "DuplicateSerializeReferenceDependency",
                    declaration.consumer + " -> " + declaration.callSite);
                var declaredTypes = new HashSet<string>(StringComparer.Ordinal);
                foreach (string concreteType in declaration.concreteTypes)
                {
                    ShadowHash.Require(declaredTypes.Add(concreteType), "DuplicateSerializeReferenceType", declaration.callSite + " -> " + concreteType);
                    string provider = AssemblyIdentityUtil.CanonicalName(ReflectionBindingConfiguration.ProviderOf(concreteType));
                    RequireKnown(provider);
                    ShadowHash.Require(IsRuntime(assemblies[provider]), "InvalidSerializeReferenceDependency",
                        "SerializeReference concrete type must belong to a runtime assembly: " + concreteType);
                    if (consumer != provider && !forward[consumer].Contains(provider))
                        AddEdge(consumer, provider, "SerializeReference", declaration.evidence + "; " + declaration.callSite);
                }
            }
            var entries = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in config.bootstrapEntrypoints ?? new BootstrapEntrypointDeclaration[0])
            {
                ShadowHash.Require(entry != null, "InvalidEntrypoint", "Null entrypoint.");
                string consumer = AssemblyIdentityUtil.CanonicalName(entry.consumer);
                string provider = AssemblyIdentityUtil.CanonicalName(entry.provider);
                RequireKnown(consumer); RequireKnown(provider);
                var target = assemblies[provider];
                bool approvedProvider = !target.isBootstrap &&
                    ((target.classification == AssemblyClassification.Runtime && target.isShadowCapable) ||
                     (target.classification == AssemblyClassification.NormalHotUpdate && !target.isShadowCapable));
                string callSite = BootstrapIsolationRule.CallSite(entry);
                ShadowHash.Require(assemblies[consumer].isBootstrap && approvedProvider &&
                    !string.IsNullOrWhiteSpace(entry.typeName) && !string.IsNullOrWhiteSpace(callSite) &&
                    callSite.IndexOf("::", StringComparison.Ordinal) > 0 && !string.IsNullOrWhiteSpace(entry.reason),
                    "InvalidEntrypoint", "Entrypoints require a fixed bootstrap, shadow-capable or ordinary hot-update provider, type, callsite and reason.");
                ShadowHash.Require(entries.Add(consumer + "\n" + provider + "\n" + entry.typeName + "\n" + callSite + "\n" + entry.target), "DuplicateEntrypoint", entry.consumer + " -> " + entry.typeName);
            }
        }

        private void RequireKnown(string name)
        {
            ShadowHash.Require(assemblies.ContainsKey(name), "UnknownAssembly", "Dependency configuration names an assembly absent from the target snapshot: " + name);
        }

        public string[] ReverseClosure(IEnumerable<string> changedRoots)
        {
            var pending = new Queue<string>();
            var parent = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (string root in changedRoots.Select(AssemblyIdentityUtil.CanonicalName).Distinct().OrderBy(n => n, StringComparer.Ordinal))
            {
                RequireKnown(root);
                parent.Add(root, null);
                pending.Enqueue(root);
            }
            while (pending.Count > 0)
            {
                string provider = pending.Dequeue();
                var descriptor = assemblies[provider];
                string path = DependencyPath(provider, parent);
                ShadowHash.Require(IsRuntime(descriptor), "NonRuntimeClosure", path);
                ShadowHash.Require(!descriptor.isBootstrap, "BootstrapInClosure", "Fixed Bootstrap cannot enter a patch: " + path);
                ShadowHash.Require(!descriptor.isPrecompiled || descriptor.capabilityDeclared, "UndeclaredPrecompiledCapability", path);
                ShadowHash.Require(descriptor.isShadowCapable, "NonShadowConsumer", "Non-shadow AOT consumer depends on changed shadow assembly: " + path);
                foreach (string consumer in reverse[provider])
                    if (!parent.ContainsKey(consumer)) { parent.Add(consumer, provider); pending.Enqueue(consumer); }
            }
            string[] closure = parent.Keys.Select(key => assemblies[key].name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            LoadOrder(closure); // Reject cycles even if the caller only asks for closure.
            return closure;
        }

        private string DependencyPath(string end, Dictionary<string, string> parent)
        {
            var result = new List<string>();
            for (string name = end; name != null; name = parent[name]) result.Add(assemblies[name].name);
            return string.Join(" -> ", result.ToArray());
        }

        public string[] LoadOrder(IEnumerable<string> closure)
        {
            var selected = new HashSet<string>(closure.Select(AssemblyIdentityUtil.CanonicalName), StringComparer.Ordinal);
            var state = new Dictionary<string, int>(StringComparer.Ordinal);
            var stack = new List<string>();
            var result = new List<string>();
            foreach (string name in selected.OrderBy(n => n, StringComparer.Ordinal)) Visit(name, selected, state, stack, result);
            return result.ToArray();
        }

        private void Visit(string name, HashSet<string> selected, Dictionary<string, int> state, List<string> stack, List<string> result)
        {
            RequireKnown(name);
            int mark;
            if (state.TryGetValue(name, out mark))
            {
                if (mark == 2) return;
                int start = stack.IndexOf(name);
                throw new ShadowBuildException("DependencyCycle", string.Join(" -> ", stack.Skip(start).Concat(new[] { name }).Select(key => assemblies[key].name).ToArray()));
            }
            state[name] = 1; stack.Add(name);
            foreach (string provider in forward[name]) if (selected.Contains(provider)) Visit(provider, selected, state, stack, result);
            stack.RemoveAt(stack.Count - 1); state[name] = 2;
            result.Add(assemblies[name].name);
        }

        public static string[] DetectChangedRoots(IEnumerable<AssemblyDescriptor> baseline, IEnumerable<AssemblyDescriptor> current, IEnumerable<string> explicitRoots = null)
        {
            var before = baseline.Where(d => d.classification == AssemblyClassification.Runtime).ToDictionary(d => AssemblyIdentityUtil.CanonicalName(d.name), StringComparer.Ordinal);
            var after = current.Where(d => d.classification == AssemblyClassification.Runtime).ToDictionary(d => AssemblyIdentityUtil.CanonicalName(d.name), StringComparer.Ordinal);
            var roots = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pair in before)
            {
                AssemblyDescriptor descriptor;
                ShadowHash.Require(after.TryGetValue(pair.Key, out descriptor), "AssemblyRemoved", "Player assembly missing from current snapshot: " + pair.Value.name);
                if (descriptor.semanticHash != pair.Value.semanticHash)
                {
                    ShadowHash.Require(!pair.Value.isBootstrap, "BootstrapAbiChanged", pair.Value.name);
                    ShadowHash.Require(pair.Value.isShadowCapable && descriptor.isShadowCapable, "FixedAssemblyChanged", pair.Value.name);
                    roots.Add(pair.Key);
                }
            }
            foreach (var pair in after.Where(p => !before.ContainsKey(p.Key)))
                throw new ShadowBuildException("BaselineMissing", "New runtime assembly has no AOT baseline: " + pair.Value.name);
            if (explicitRoots != null)
            {
                var requested = new HashSet<string>(explicitRoots.Select(AssemblyIdentityUtil.CanonicalName), StringComparer.Ordinal);
                ShadowHash.Require(roots.IsSubsetOf(requested), "ChangedRootsOmitted", "Explicit roots omit detected changes: " + string.Join(", ", roots.Except(requested).ToArray()));
                foreach (string root in requested)
                {
                    ShadowHash.Require(before.ContainsKey(root), "BaselineMissing", root);
                    roots.Add(root);
                }
            }
            return roots.Select(key => after[key].name).OrderBy(n => n, StringComparer.Ordinal).ToArray();
        }
    }
}
