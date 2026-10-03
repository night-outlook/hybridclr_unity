using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;

namespace HybridCLR.Editor.AssemblyShadow
{
    [Serializable]
    public sealed class PureInterpreterTypeEligibility
    {
        public string typeKey, declarationKey, assembly, baselineDllSha256, targetDllSha256;
        public string decision;
        public string[] domains, staticReasons, requiredProofs;
        public string nativeV1Decision;
        public bool authorizesExpansion;
    }

    [Serializable]
    public sealed class PureInterpreterEligibilityReport
    {
        public int schemaVersion = 1;
        public string kind = "R03PureInterpreterEligibilityV1";
        public string qualificationGate = "R03-PureInterpreter-Qualification";
        public string sourceInventoryHash, resourceDescriptorHash, analysisBindingHash;
        public string[] changedRoots, closure, targetLoadOrder, inputBindings;
        public PureInterpreterTypeEligibility[] types;
        public bool runtimeProofExecuted, qualificationApproved, expansionAuthorized;
    }

    /// <summary>
    /// Static qualification INPUT, not an admission certificate. This API never
    /// changes V1, stages/loads managed assemblies, runs user code, or establishes
    /// absence of old objects. No runtime path consumes its report as authority.
    /// The caller owns byte-bound compiled sets and the independent resource ABI.
    /// Unknown/dynamic/native boundaries cannot become affirmative eligibility.
    /// </summary>
    public static class PureInterpreterEligibility
    {
        private static readonly string[] Proofs = {
            "CompleteInstalledAndTargetRuntimeInventory", "NoExistingBaselineObjectsOrUses",
            "NoUnprovedReflectionOrNativeConcreteConsumer", "ResourceAndUnityBindingClosure",
            "PrepublicationOwnerAndFailureState", "ConcreteGenericAndInteropCapability",
            "IndependentQualificationReview", "ExplicitOwnerQualificationApproval",
            "SeparateExperimentalPlayerEvidenceBeforeSupportClaim" };

        public static PureInterpreterEligibilityReport Analyze(CompiledAssemblySet baseline,
            CompiledAssemblySet target, IEnumerable<string> explicitRoots,
            ShadowDependencyConfiguration dependencies, ResourceAbiDescriptor resources)
        {
            ShadowHash.Require(baseline != null && target != null, "EligibilityInput", "Two actual compiled assembly sets are required.");
            ShadowHash.Require(resources != null && resources.schemaVersion == 2 && resources.types != null && resources.unknowns != null,
                "EligibilityResourceInput", "Explicit versioned resource descriptor required; resource compatibility alone is insufficient.");
            var bindings = Bind(baseline, "baseline").Concat(Bind(target, "target")).ToArray();
            var roots = AssemblyReferenceGraph.DetectChangedRoots(baseline.Assemblies.Values, target.Assemblies.Values, explicitRoots);
            var graph = new AssemblyReferenceGraph(target.Assemblies.Values, dependencies, new AssemblyReferenceGraph(baseline.Assemblies.Values, dependencies).Edges);
            var closure = graph.ReverseClosure(roots);
            var order = graph.LoadOrder(closure); // Real target cycles still fail, even for an analysis-only report.
            var selected = new HashSet<string>(closure.Select(AssemblyIdentityUtil.CanonicalName), StringComparer.Ordinal);
            foreach (var name in selected)
            {
                var before = baseline.Get(name); var after = target.Get(name);
                ShadowHash.Require(before.isShadowCapable && after.isShadowCapable &&
                    before.classification == AssemblyClassification.Runtime && after.classification == AssemblyClassification.Runtime &&
                    !before.isBootstrap && !after.isBootstrap, "EligibilityRole", "Only baseline-backed Runtime Shadow closure may be qualified: " + name);
            }
            var resourceKeys = new HashSet<string>(StringComparer.Ordinal);
            var resourceUnknown = new List<string>(resources.unknowns);
            var referencedResourceKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in resources.types.OrderBy(t => t == null ? "" : t.typeKey, StringComparer.Ordinal))
            {
                ShadowHash.Require(entry != null && !string.IsNullOrEmpty(entry.typeKey) && resourceKeys.Add(entry.typeKey),
                    "EligibilityResourceInput", "Missing/duplicate resource type key.");
                foreach (var key in (entry.referencedTypeKeys ?? new string[0]).Concat(entry.serializeReferenceCandidates ?? new string[0]))
                    if (!string.IsNullOrWhiteSpace(key)) referencedResourceKeys.Add(key);
                if (entry.hasUnknown) resourceUnknown.Add("UnknownResourceType:" + entry.typeKey);
                foreach (var reason in entry.unknownReasons ?? new string[0]) resourceUnknown.Add(reason);
            }
            resourceKeys.UnionWith(referencedResourceKeys);

            // Both worlds matter: removed historical AOT references cannot be
            // erased by only examining the newest download's AssemblyRefs.
            var exposures = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            var uncertain = new HashSet<string>(StringComparer.Ordinal);
            InspectConsumers(baseline, selected, "baseline", exposures, uncertain);
            InspectConsumers(target, selected, "target", exposures, uncertain);
            var rows = new List<PureInterpreterTypeEligibility>();
            foreach (var name in selected.OrderBy(n => n, StringComparer.Ordinal))
            {
                var original = baseline.Get(name); var changed = target.Get(name);
                var beforeTypes = baseline.GetModule(name).GetTypes().ToDictionary(t => EvolutionSignature.Type(t), StringComparer.Ordinal);
                var v1 = NativeLayoutAdmissionValidator.Analyze(System.IO.File.ReadAllBytes(original.filePath), System.IO.File.ReadAllBytes(changed.filePath));
                foreach (var type in target.GetModule(name).GetTypes().Where(t => !t.IsGlobalModuleType).OrderBy(t => EvolutionSignature.Type(t), StringComparer.Ordinal))
                {
                    var reasons = new HashSet<string>(StringComparer.Ordinal);
                    var domains = new HashSet<string>(StringComparer.Ordinal) { "PureInterpreterCandidate" };
                    var key = EvolutionSignature.Type(type);
                    TypeDef old;
                    if (!beforeTypes.TryGetValue(key, out old)) reasons.Add("NoBaselineTypeCounterpart");
                    if (old != null && EvolutionSignature.DeclarationKey(old) != EvolutionSignature.DeclarationKey(type)) reasons.Add("DeclarationIdentityChanged");
                    InspectShape(type, target, "target", reasons, domains);
                    if (old != null) InspectShape(old, baseline, "baseline", reasons, domains);
                    if (resourceKeys.Contains(key)) { domains.Add("UnityBound"); reasons.Add("SerializedResourceType"); }
                    foreach (var unknown in resourceUnknown) reasons.Add("ResourceUnknown:" + unknown);
                    foreach (var unknown in uncertain) reasons.Add(unknown);
                    HashSet<string> consumers;
                    if (exposures.TryGetValue(key, out consumers))
                    {
                        domains.Add("AotInteropExposed");
                        foreach (var consumer in consumers) reasons.Add(consumer);
                    }
                    rows.Add(new PureInterpreterTypeEligibility {
                        typeKey = key, declarationKey = EvolutionSignature.DeclarationKey(type), assembly = name,
                        baselineDllSha256 = original.sha256, targetDllSha256 = changed.sha256,
                        decision = reasons.Count == 0 ? "StaticCandidateNeedsRuntimeAndReview" : "ExcludedOrNeedsProof",
                        domains = ShadowHash.Sorted(domains), staticReasons = ShadowHash.Sorted(reasons),
                        requiredProofs = (string[])Proofs.Clone(),
                        nativeV1Decision = v1.types.Single(r => r.typeKey == key).decision,
                        authorizesExpansion = false });
                }
            }
            // Verify the source files still match the loaded snapshot's evidence.
            var afterBindings = Bind(baseline, "baseline").Concat(Bind(target, "target")).ToArray();
            ShadowHash.Require(bindings.SequenceEqual(afterBindings), "EligibilityInputChanged", "Compilation bytes changed during analysis.");
            var bindingWriter = new CanonicalSignatureWriter();
            foreach (var binding in bindings) bindingWriter.Token(binding);
            foreach (var value in roots) bindingWriter.Token("root:" + value);
            foreach (var value in closure) bindingWriter.Token("closure:" + value);
            foreach (var value in order) bindingWriter.Token("load:" + value);
            foreach (var edge in graph.Edges)
            { bindingWriter.Token(edge.consumer); bindingWriter.Token(edge.provider); bindingWriter.Token(edge.kind); bindingWriter.Token(edge.evidence); }
            bindingWriter.Token(ResourceAbiHasher.Compute(resources));
            return new PureInterpreterEligibilityReport {
                changedRoots = roots, closure = closure, targetLoadOrder = order, inputBindings = bindings,
                sourceInventoryHash = ShadowHash.Text(string.Join("\n", bindings)), resourceDescriptorHash = ResourceAbiHasher.Compute(resources),
                analysisBindingHash = ShadowHash.Text(bindingWriter.ToString()),
                types = rows.ToArray(), runtimeProofExecuted = false, qualificationApproved = false, expansionAuthorized = false };
        }

        private static string[] Bind(CompiledAssemblySet set, string world)
        {
            var values = new List<string>();
            foreach (var entry in set.Assemblies.Values.OrderBy(a => AssemblyIdentityUtil.CanonicalName(a.name), StringComparer.Ordinal))
            {
                ShadowHash.Require(!string.IsNullOrEmpty(entry.sha256) && ShadowHash.File(entry.filePath) == entry.sha256,
                    "EligibilityInputChanged", "Actual DLL no longer matches its loaded descriptor: " + entry.name);
                ShadowHash.Require(AssemblySemanticHasher.Compute(set.GetModule(entry.name)).semanticHash == entry.semanticHash,
                    "EligibilityInputChanged", "Loaded module metadata changed: " + entry.name);
                values.Add(world + "|" + set.GetModule(entry.name).Assembly.FullName + "|" + entry.sha256 + "|" +
                    entry.classification + "|shadow=" + entry.isShadowCapable + "|bootstrap=" + entry.isBootstrap);
            }
            // Reference bytes are part of the closed resolver, not merely names/MVIDs.
            foreach (var input in set.Sources.Where(input => input.ReferenceOnly))
            {
                ShadowHash.Require(ShadowHash.File(input.Path) == input.Sha256, "EligibilityInputChanged", "Reference bytes changed: " + input.Name);
                using (var original = ModuleDefMD.Load(System.IO.File.ReadAllBytes(input.Path)))
                    ShadowHash.Require(AssemblySemanticHasher.Compute(original).semanticHash == AssemblySemanticHasher.Compute(set.GetModule(input.Name)).semanticHash,
                        "EligibilityInputChanged", "Loaded reference metadata changed: " + input.Name);
                values.Add(world + "|reference|" + set.GetModule(input.Name).Assembly.FullName + "|" + input.Sha256);
            }
            return values.ToArray();
        }

        private static void InspectConsumers(CompiledAssemblySet set, HashSet<string> selected, string world,
            Dictionary<string, HashSet<string>> exposures, HashSet<string> uncertain)
        {
            foreach (var descriptor in set.Assemblies.Values)
            {
                if (descriptor.classification == AssemblyClassification.BuildFiltered || descriptor.classification == AssemblyClassification.EditorOnly ||
                    descriptor.classification == AssemblyClassification.TestOnly || descriptor.classification == AssemblyClassification.Reference) continue;
                var module = set.GetModule(descriptor.name);
                bool fixedConsumer = !selected.Contains(AssemblyIdentityUtil.CanonicalName(descriptor.name));
                foreach (var reference in module.GetAssemblyRefs())
                    if (!set.Modules.ContainsKey(AssemblyIdentityUtil.CanonicalName(reference.Name)))
                        uncertain.Add("UnresolvedAssembly:" + world + ":" + descriptor.name + ":" + reference.FullName);
                foreach (var reference in module.GetTypeRefs())
                {
                    var resolved = set.ResolveType(reference);
                    if (resolved == null)
                    { uncertain.Add("UnresolvedType:" + world + ":" + descriptor.name + ":" + EvolutionSignature.Type(reference)); continue; }
                    if (!fixedConsumer || !selected.Contains(AssemblyIdentityUtil.CanonicalName(resolved.Module.Assembly.Name))) continue;
                    var key = EvolutionSignature.Type(resolved);
                    HashSet<string> values;
                    if (!exposures.TryGetValue(key, out values)) exposures[key] = values = new HashSet<string>(StringComparer.Ordinal);
                    values.Add((descriptor.classification == AssemblyClassification.NormalHotUpdate ? "OrdinaryConcreteConsumer:" : "FixedAotConcreteConsumer:") + world + ":" + descriptor.name);
                }
                if (!fixedConsumer) continue;
                foreach (var method in module.GetTypes().SelectMany(t => t.Methods))
                {
                    if (method.ImplMap != null || method.IsNative || method.IsUnmanaged || method.IsInternalCall || method.IsUnmanagedExport)
                        uncertain.Add("FixedNativeConsumerNeedsProof:" + world + ":" + descriptor.name);
                    if (!method.HasBody) continue;
                    foreach (var instruction in method.Body.Instructions)
                    {
                        var called = instruction.Operand as IMethod;
                        if (called == null || called.DeclaringType == null) continue;
                        var owner = called.DeclaringType.FullName;
                        if (owner == "System.Type" || owner == "System.Activator" || owner.StartsWith("System.Reflection.", StringComparison.Ordinal) ||
                            owner == "System.Runtime.InteropServices.Marshal")
                            uncertain.Add("DynamicConcreteConsumerNeedsProof:" + world + ":" + descriptor.name + ":" + method.Name);
                    }
                }
            }
        }

        private static void InspectShape(TypeDef type, CompiledAssemblySet set, string world, HashSet<string> reasons, HashSet<string> domains)
        {
            if (type.IsValueType || type.IsEnum) { domains.Add("AotInteropExposed"); reasons.Add("ValueRepresentation:" + world); }
            if (type.IsInterface || type.IsAbstract) reasons.Add("NotConcreteReferenceClass:" + world);
            if (type.HasGenericParameters || type.Methods.Any(m => m.HasGenericParameters)) reasons.Add("GenericBoundaryNeedsProof:" + world);
            if (!type.IsAutoLayout) { domains.Add("AotInteropExposed"); reasons.Add("ExplicitOrSequentialLayout:" + world); }
            if (type.CustomAttributes.Any(a => a.TypeFullName.Contains("StructLayout") || a.TypeFullName.Contains("ComImport") ||
                a.TypeFullName.Contains("IsByRefLike"))) { domains.Add("AotInteropExposed"); reasons.Add("NativeAttribute:" + world); }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (TypeDef at = type; at != null; )
            {
                var identity = EvolutionSignature.Type(at);
                if (!seen.Add(identity) || seen.Count > 128) { reasons.Add("UnprovedBaseChain:" + world); break; }
                if (at.Namespace == "UnityEngine" && at.Name == "Object")
                { domains.Add("UnityBound"); reasons.Add("UnityNativeBinding:" + world); }
                if (at.FullName == "System.Delegate" || at.FullName == "System.MulticastDelegate")
                { domains.Add("AotInteropExposed"); reasons.Add("DelegateAbi:" + world); }
                if (at.BaseType == null) break;
                var next = set.ResolveType(at.BaseType);
                if (next == null) { reasons.Add("UnresolvedBase:" + world + ":" + EvolutionSignature.Type(at.BaseType)); break; }
                at = next;
            }
            foreach (var field in type.Fields)
                if (field.HasMarshalType || Risky(field.FieldType, 0))
                { domains.Add("AotInteropExposed"); reasons.Add("FieldGenericPointerOrInterop:" + world + ":" + field.Name); }
            foreach (var method in type.Methods)
            {
                if (method.ImplMap != null || method.IsNative || method.IsUnmanaged || method.IsUnmanagedExport || method.IsInternalCall ||
                    method.CustomAttributes.Any(a => a.TypeFullName.Contains("MonoPInvokeCallback") || a.TypeFullName.Contains("UnmanagedCallersOnly")))
                { domains.Add("AotInteropExposed"); reasons.Add("NativeCallableMethod:" + world + ":" + method.Name); }
                if (method.MethodSig == null || Risky(method.MethodSig.RetType, 0) || method.MethodSig.Params.Any(t => Risky(t, 0)))
                { domains.Add("AotInteropExposed"); reasons.Add("MethodGenericPointerOrInterop:" + world + ":" + method.Name); }
            }
        }

        private static bool Risky(TypeSig signature, int depth)
        {
            if (signature == null || depth >= 128) return true;
            if (signature is GenericSig || signature is GenericInstSig || signature is FnPtrSig || signature is PtrSig ||
                signature is ByRefSig || signature is ModifierSig || signature is PinnedSig) return true;
            return signature.Next != null && Risky(signature.Next, depth + 1);
        }
    }
}
