using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using HybridCLR.AssemblyShadow.CodeGen;

namespace HybridCLR.Editor.AssemblyShadow
{
    // Admission proves the acquisition, not the callers of a callable selector.
    // Bootstrap intentionally has no Candidate graph edge. Consequently every
    // outside selector caller must represent the selected provider itself.
    // This is not a general taint analysis of already-selected Type objects.
    internal static class RawTypeAdmissionPropagation
    {
        internal static void Validate(CompiledAssemblySet set, ShadowPolicyConfiguration policy,
            IEnumerable<AssemblyPolicyDefinition> definitions, VerifiedRawTypeAdmission[] proofs, ShadowPolicyValidationResult errors)
        {
            if (proofs == null || proofs.Length == 0) return;
            try { new Analysis(set, policy, definitions, proofs, errors).Run(); }
            catch (Exception error) { errors.Error("InvalidRawSelectorPropagation", error.Message); }
        }

        private sealed class Analysis
        {
            private readonly CompiledAssemblySet set;
            private readonly ShadowPolicyConfiguration policy;
            private readonly ShadowPolicyValidationResult errors;
            private readonly Dictionary<string, AssemblyPolicyDefinition> definitions;
            private readonly Dictionary<MethodDef, HashSet<string>> selectors = new Dictionary<MethodDef, HashSet<string>>();
            private readonly Dictionary<MethodDef, List<Call>> calls = new Dictionary<MethodDef, List<Call>>();
            private readonly Dictionary<string, HashSet<string>> assemblyRequirements = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, ModuleDefMD> modules;
            private readonly MethodDef[] methods;
            private readonly HashSet<string> emitted = new HashSet<string>(StringComparer.Ordinal);
            private readonly Dictionary<string, bool> typeCache = new Dictionary<string, bool>(StringComparer.Ordinal);
            private readonly Dictionary<string, TypeDef[]> typeDefinitions;
            private readonly Dictionary<IMethod, MethodDef> resolvedMethods = new Dictionary<IMethod, MethodDef>();
            private readonly Dictionary<MethodDef, bool> outputEffects = new Dictionary<MethodDef, bool>();

            private sealed class Call { internal IMethod Reference; internal MethodDef Target; internal Code Code; internal int Index; }

            internal Analysis(CompiledAssemblySet set, ShadowPolicyConfiguration policy, IEnumerable<AssemblyPolicyDefinition> definitions,
                IEnumerable<VerifiedRawTypeAdmission> proofs, ShadowPolicyValidationResult errors)
            {
                this.set = set; this.policy = policy; this.errors = errors;
                this.definitions = definitions.ToDictionary(value => Canonical(value.name), StringComparer.OrdinalIgnoreCase);
                modules = set.Modules.Values.ToDictionary(value => value.Assembly.Name.String, StringComparer.OrdinalIgnoreCase);
                typeDefinitions = modules.Values.SelectMany(module => module.GetTypes()).GroupBy(type => type.Module.Assembly.FullName + "|" + type.FullName)
                    .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
                methods = set.Assemblies.Values.Where(Runtime).SelectMany(value => set.GetModule(value.name).GetTypes()).SelectMany(type => type.Methods).Where(method => method.HasBody).ToArray();
                foreach (var proof in proofs) Add(selectors, proof.Method, Canonical(new AssemblyNameInfo(proof.ProviderAssemblyIdentity).Name.String));
                foreach (var method in methods)
                {
                    var entries = new List<Call>(); calls.Add(method, entries);
                    for (int index = 0; index < method.Body.Instructions.Count; index++)
                    {
                        var instruction = method.Body.Instructions[index]; var target = instruction.Operand as IMethod;
                        if (target == null || target.MethodSig == null) continue;
                        if (instruction.OpCode.Code != Code.Call && instruction.OpCode.Code != Code.Callvirt && instruction.OpCode.Code != Code.Ldftn &&
                            instruction.OpCode.Code != Code.Ldvirtftn && instruction.OpCode.Code != Code.Newobj) continue;
                        entries.Add(new Call { Reference = target, Target = Resolve(target), Code = instruction.OpCode.Code, Index = index });
                    }
                }
            }

            internal void Run()
            {
                // Only callable selectors forward selection authority. A void
                // observer, IEnumerator.MoveNext(bool), or Action<int> completion
                // does not become a selector merely by running a probe.
                bool changed;
                do
                {
                    changed = false;
                    foreach (var method in methods.Where(SelectorSignature))
                        foreach (var call in calls[method].Where(value => value.Code == Code.Call || value.Code == Code.Callvirt))
                            foreach (string provider in Providers(call)) changed |= Add(selectors, method, provider);
                    foreach (var definition in definitions.Values.Where(value => value.entersPlayer))
                        foreach (var evidence in definition.reflectionDependencies ?? new ReflectionDependencyEvidence[0])
                            foreach (string provider in ReflectedProviders(evidence))
                                foreach (var method in methods.Where(value => Canonical(value.Module.Assembly.Name.String) == Canonical(definition.name) &&
                                    value.DeclaringType.FullName + "::" + value.Name == evidence.callSite && SelectorSignature(value)))
                                    changed |= Add(selectors, method, provider);
                } while (changed);

                foreach (var pair in selectors)
                {
                    var method = pair.Key; var descriptor = set.Get(method.Module.Assembly.Name.String);
                    // A selector-bearing virtual/interface implementation can be
                    // installed in an outside-typed field without any incoming
                    // Bootstrap token/reference. No guessed dispatch permission.
                    if (descriptor.isBootstrap && (method.IsVirtual || method.HasOverrides))
                        Error("RawSelectorVirtualExposure", method, "Selector-bearing Bootstrap virtual/interface implementation is unsupported.");
                }

                foreach (var method in methods)
                {
                    var invoked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var call in calls[method])
                    {
                        string[] providers = Providers(call).ToArray();
                        if (providers.Length > 0 && (call.Code == Code.Ldftn || call.Code == Code.Ldvirtftn))
                            Error("RawSelectorDelegateExposure", method, "Method/delegate address exposes a provider selector at operation " + call.Index + ".");
                        if (call.Code == Code.Newobj && SelectorOwner(call.Reference.DeclaringType))
                            Error("RawSelectorObjectExposure", method, "Instantiating a selector-bearing owner exposes reflective/virtual selection at operation " + call.Index + ".");
                        if (call.Code == Code.Call || call.Code == Code.Callvirt)
                            foreach (string provider in providers) invoked.Add(provider);
                        if (call.Target == null && PotentialSelector(call.Reference))
                            Error("UnresolvedRawSelectorDispatch", method, "Selector dispatch cannot be resolved to one exact captured definition: " + call.Reference.FullName);
                    }
                    foreach (var instruction in method.Body.Instructions)
                    {
                        if (instruction.OpCode.Code == Code.Ldtoken && SelectorOwner(instruction.Operand as ITypeDefOrRef))
                            Error("RawSelectorTypeTokenExposure", method, "A selector-owning type token is an unsupported callable exposure.");
                        if (instruction.OpCode.Code == Code.Ldtoken && instruction.Operand is IMethod &&
                            Providers(new Call { Reference = (IMethod)instruction.Operand, Target = Resolve((IMethod)instruction.Operand) }).Any())
                            Error("RawSelectorMethodTokenExposure", method, "A selector method token is an unsupported callable exposure.");
                    }
                    if (invoked.Count > 0) CheckSelectedEffects(method);
                    if (!set.Get(method.Module.Assembly.Name.String).isBootstrap)
                        foreach (string provider in invoked) Add(assemblyRequirements, Canonical(method.Module.Assembly.Name.String), provider);
                }

                foreach (var definition in definitions.Values.Where(value => value.entersPlayer))
                    foreach (var evidence in definition.reflectionDependencies ?? new ReflectionDependencyEvidence[0])
                    {
                        string[] selected = ReflectedProviders(evidence).ToArray();
                        if (selected.Length == 0) continue;
                        // Reflection obtains the broker's Type/MethodInfo itself,
                        // not an opaque already-selected business Type. Its escape
                        // is unsupported; ordinary vendor reflection is unaffected.
                        errors.Error("RawSelectorReflectionExposure", definition.name + " acquires selector owner " + evidence.typeName + " at " + evidence.callSite + ".");
                        if (!definition.isBootstrap)
                            foreach (string provider in selected) Add(assemblyRequirements, Canonical(definition.name), provider);
                    }

                // A declared path to a Bootstrap selector-bearing assembly may
                // hide forwarding not represented by a direct call operand.
                var brokerAssemblies = selectors.GroupBy(pair => Canonical(pair.Key.Module.Assembly.Name.String))
                    .ToDictionary(group => group.Key, group => new HashSet<string>(group.SelectMany(pair => pair.Value), StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
                do
                {
                    changed = false;
                    // An incoming AssemblyRef can expose a selector through
                    // signatures/fields/composite metadata without a direct call
                    // or ldtoken operand. Preserve its finite provider obligation
                    // at the assembly boundary as well as at exact callsites.
                    foreach (var descriptor in set.Assemblies.Values.Where(Runtime))
                        foreach (string reference in descriptor.references ?? new string[0])
                        {
                            HashSet<string> selected; AssemblyPolicyDefinition broker;
                            if (!brokerAssemblies.TryGetValue(Canonical(reference), out selected) ||
                                !definitions.TryGetValue(Canonical(reference), out broker) || !broker.isBootstrap) continue;
                            foreach (string provider in selected.ToArray())
                                if (descriptor.isBootstrap) changed |= Add(brokerAssemblies, Canonical(descriptor.name), provider);
                                else Add(assemblyRequirements, Canonical(descriptor.name), provider);
                        }
                    foreach (var edge in (policy == null || policy.dependencies == null ? null : policy.dependencies.runtimeDependencies) ?? new DeclaredRuntimeDependency[0])
                    {
                        HashSet<string> selected; AssemblyPolicyDefinition consumer, broker;
                        if (edge == null || !brokerAssemblies.TryGetValue(Canonical(edge.provider), out selected) ||
                            !definitions.TryGetValue(Canonical(edge.consumer), out consumer) || !definitions.TryGetValue(Canonical(edge.provider), out broker) || !broker.isBootstrap) continue;
                        foreach (string provider in selected.ToArray())
                            if (consumer.isBootstrap) changed |= Add(brokerAssemblies, Canonical(consumer.name), provider);
                            else Add(assemblyRequirements, Canonical(consumer.name), provider);
                    }
                } while (changed);
                foreach (var pair in assemblyRequirements)
                    foreach (string provider in pair.Value)
                    {
                        if (pair.Key == provider) continue;
                        AssemblyDescriptor consumer;
                        if (!set.Assemblies.TryGetValue(pair.Key, out consumer) || !Runtime(consumer)) continue;
                        bool actualReference = (consumer.references ?? new string[0]).Any(value => Canonical(value) == provider);
                        bool declared = ((policy == null || policy.dependencies == null ? null : policy.dependencies.runtimeDependencies) ?? new DeclaredRuntimeDependency[0])
                            .Any(edge => edge != null && Canonical(edge.consumer) == pair.Key && Canonical(edge.provider) == provider &&
                                !string.IsNullOrWhiteSpace(edge.kind) && !string.IsNullOrWhiteSpace(edge.evidence));
                        if (!actualReference && !declared)
                            errors.Error("RawSelectorDependencyMissing", consumer.name + " selects " + provider + " through an admitted callable; an actual AssemblyRef or truthful direct runtime dependency is required for reverse closure.");
                    }
            }

            private IEnumerable<string> Providers(Call call)
            {
                var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase); HashSet<string> direct;
                if (call.Target != null && selectors.TryGetValue(call.Target, out direct)) result.UnionWith(direct);
                if (call.Code == Code.Callvirt || call.Code == Code.Ldvirtftn)
                    foreach (var pair in selectors)
                        if (call.Target != null && Implements(pair.Key, call.Target)) result.UnionWith(pair.Value);
                return result;
            }
            private bool Implements(MethodDef implementation, MethodDef declaration)
            {
                if (!implementation.IsVirtual && !implementation.HasOverrides) return false;
                foreach (var item in implementation.Overrides)
                    if (Resolve(item.MethodDeclaration) == declaration) return true;
                if (implementation.Name != declaration.Name || !new SigComparer().Equals(implementation.MethodSig, declaration.MethodSig)) return false;
                var pending = new Stack<TypeDef>(); var visited = new HashSet<TypeDef>(); pending.Push(implementation.DeclaringType);
                while (pending.Count > 0)
                {
                    var type = pending.Pop(); if (!visited.Add(type)) continue;
                    foreach (var iface in type.Interfaces)
                    { var actual = ResolveType(iface.Interface); if (actual == declaration.DeclaringType) return true; if (actual != null) pending.Push(actual); }
                    var parent = ResolveType(type.BaseType); if (parent == declaration.DeclaringType) return true; if (parent != null) pending.Push(parent);
                }
                return false;
            }
            private IEnumerable<string> ReflectedProviders(ReflectionDependencyEvidence evidence)
            {
                if (evidence == null || string.IsNullOrEmpty(evidence.typeName) || string.IsNullOrEmpty(evidence.provider)) return new string[0];
                ModuleDefMD module; TypeDef[] matches;
                if (!modules.TryGetValue(evidence.provider, out module) || !typeDefinitions.TryGetValue(module.Assembly.FullName + "|" + evidence.typeName.Replace('+', '/'), out matches) || matches.Length != 1)
                    return new string[0];
                return OwnerProviders(matches[0]);
            }
            private bool PotentialSelector(IMethod reference)
            {
                var spec = reference as MethodSpec; if (spec != null) reference = spec.Method;
                return reference != null && reference.DeclaringType != null && selectors.Keys.Any(method =>
                    reference.DeclaringType.DefinitionAssembly != null && Canonical(method.Module.Assembly.Name.String) == Canonical(reference.DeclaringType.DefinitionAssembly.Name.String) &&
                    method.Name == reference.Name && method.DeclaringType.FullName == DefinitionTypeName(reference.DeclaringType));
            }
            private MethodDef Resolve(IMethod reference)
            {
                var spec = reference as MethodSpec; if (spec != null) reference = spec.Method;
                if (reference == null || reference.MethodSig == null) return null;
                MethodDef cached; if (resolvedMethods.TryGetValue(reference, out cached)) return cached;
                var owner = ResolveType(reference.DeclaringType); if (owner == null) return null;
                var matching = owner.Methods.Where(method => method.Name == reference.Name &&
                    new SigComparer().Equals(method.MethodSig, reference.MethodSig)).ToArray();
                cached = matching.Length == 1 ? matching[0] : null; resolvedMethods[reference] = cached; return cached;
            }
            private TypeDef ResolveType(ITypeDefOrRef reference)
            {
                if (reference == null || reference.DefinitionAssembly == null) return null;
                ModuleDefMD module;
                if (!modules.TryGetValue(reference.DefinitionAssembly.Name.String, out module) || module.Assembly.FullName != reference.DefinitionAssembly.FullName) return null;
                string name = DefinitionTypeName(reference);
                TypeDef[] matching;
                return typeDefinitions.TryGetValue(module.Assembly.FullName + "|" + name, out matching) && matching.Length == 1 ? matching[0] : null;
            }
            private static string DefinitionTypeName(ITypeDefOrRef reference)
            {
                var spec = reference as TypeSpec; var generic = spec == null ? null : spec.TypeSig as GenericInstSig;
                return generic == null ? reference.FullName : generic.GenericType.TypeDefOrRef.FullName;
            }
            private bool SelectorOwner(ITypeDefOrRef reference)
            {
                var spec = reference as TypeSpec;
                return spec == null ? OwnerProviders(ResolveType(reference)).Any() : SelectorComponent(spec.TypeSig, new HashSet<TypeSig>());
            }
            private bool SelectorComponent(TypeSig signature, HashSet<TypeSig> visited)
            {
                if (signature == null || !visited.Add(signature)) throw new ReflectionBindingException("UnsupportedRawSelectorToken", "Missing/cyclic type token signature.");
                var generic = signature as GenericInstSig;
                if (generic != null)
                    return SelectorOwner(generic.GenericType.TypeDefOrRef) || generic.GenericArguments.Any(argument => SelectorComponent(argument, new HashSet<TypeSig>(visited)));
                var modifier = signature as ModifierSig;
                if (modifier != null) return SelectorOwner(modifier.Modifier) || SelectorComponent(signature.Next, visited);
                var leaf = signature as TypeDefOrRefSig;
                if (leaf != null) return SelectorOwner(leaf.TypeDefOrRef);
                if (signature is NonLeafSig) return SelectorComponent(signature.Next, visited);
                if (signature is CorLibTypeSig || signature is GenericVar || signature is GenericMVar) return false;
                throw new ReflectionBindingException("UnsupportedRawSelectorToken", "Unsupported type token signature: " + signature.FullName);
            }
            private IEnumerable<string> OwnerProviders(TypeDef type)
            {
                var visited = new HashSet<TypeDef>(); var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (type != null && visited.Add(type))
                {
                    foreach (var pair in selectors)
                        if (pair.Key.Module == type.Module && (pair.Key.DeclaringType == type || pair.Key.DeclaringType.FullName.StartsWith(type.FullName + "/", StringComparison.Ordinal)))
                            result.UnionWith(pair.Value);
                    type = ResolveType(type.BaseType);
                }
                return result;
            }
            private bool SelectorSignature(MethodDef method)
            {
                return method.MethodSig != null && (CarriesHandle(method.MethodSig.RetType) || method.MethodSig.Params.Any(parameter =>
                    parameter is ByRefSig ? CarriesHandle(parameter.Next) : DelegateType(parameter) && CarriesHandle(parameter)));
            }
            private bool DelegateType(TypeSig signature)
            {
                var type = ResolveType(signature == null ? null : signature.ToTypeDefOrRef()); var visited = new HashSet<TypeDef>();
                while (type != null && visited.Add(type))
                { if (type.FullName == "System.Delegate" || type.FullName == "System.MulticastDelegate") return true; type = ResolveType(type.BaseType); }
                return false;
            }
            private bool CarriesHandle(TypeSig signature) { return CarriesHandle(signature, new HashSet<string>(StringComparer.Ordinal)); }
            private bool CarriesHandle(TypeSig signature, HashSet<string> pending)
            {
                if (signature == null) return false;
                if (signature is GenericVar || signature is GenericMVar) return true;
                if (signature is NonLeafSig && !(signature is GenericInstSig)) return CarriesHandle(signature.Next, pending);
                string name = signature.FullName;
                if (new[] { "System.Object", "System.Type", "System.RuntimeTypeHandle", "System.RuntimeMethodHandle", "System.RuntimeFieldHandle", "System.ModuleHandle",
                    "System.Reflection.TypeInfo", "System.Reflection.MemberInfo", "System.Reflection.MethodBase", "System.Reflection.MethodInfo", "System.Reflection.ConstructorInfo",
                    "System.Reflection.FieldInfo", "System.Reflection.PropertyInfo", "System.Reflection.EventInfo", "System.Reflection.ParameterInfo", "System.Reflection.Assembly",
                    "System.Reflection.Module", "System.Delegate", "System.MulticastDelegate", "System.Collections.IEnumerable", "System.Collections.IEnumerator" }.Contains(name, StringComparer.Ordinal)) return true;
                var generic = signature as GenericInstSig;
                if (generic != null && generic.GenericArguments.Any(argument => CarriesHandle(argument, pending))) return true;
                if (signature is CorLibTypeSig) return false;
                var type = ResolveType(signature.ToTypeDefOrRef()); if (type == null) return false;
                string identity = type.Module.Assembly.FullName + "|" + name; bool cached;
                if (typeCache.TryGetValue(identity, out cached)) return cached;
                if (!pending.Add(identity)) return false;
                bool result = DelegateType(signature)
                    ? type.Methods.Where(method => method.Name == "Invoke" && method.MethodSig != null).Any(method =>
                        CarriesHandle(CloseType(method.MethodSig.RetType, generic == null ? null : generic.GenericArguments, null), pending) ||
                        method.MethodSig.Params.Any(parameter => CarriesHandle(CloseType(parameter, generic == null ? null : generic.GenericArguments, null), pending)))
                    : type.Fields.Any(field => !field.IsStatic && field.FieldSig != null && CarriesHandle(CloseType(field.FieldSig.Type, generic == null ? null : generic.GenericArguments, null), pending));
                pending.Remove(identity);
                // A negative reached through a recursive container can depend on
                // an ancestor's other fields; only cache a root negative.
                if (result || pending.Count == 0) typeCache[identity] = result;
                return result;
            }

            private sealed class Value
            {
                internal bool Selected, External;
                internal readonly HashSet<int> Roots = new HashSet<int>();
                internal Value Copy() { var value = new Value { Selected = Selected, External = External }; value.Roots.UnionWith(Roots); return value; }
                internal bool Merge(Value other)
                { bool changed = other.Selected && !Selected || other.External && !External; Selected |= other.Selected; External |= other.External; foreach (int root in other.Roots) changed |= Roots.Add(root); return changed; }
                internal static Value Root(int root, bool external = false, bool selected = false)
                { var value = new Value { External = external, Selected = selected }; value.Roots.Add(root); return value; }
            }
            private sealed class Flow
            {
                internal readonly List<Value> Stack = new List<Value>();
                internal readonly Dictionary<int, Value> Heap = new Dictionary<int, Value>();
                internal Flow Copy() { var value = new Flow(); value.Stack.AddRange(Stack.Select(item => item.Copy())); foreach (var pair in Heap) value.Heap.Add(pair.Key, pair.Value.Copy()); return value; }
                internal Value Pop() { if (Stack.Count == 0) throw new ReflectionBindingException("UnsupportedRawSelectorFlow", "Stack underflow while proving selected-value effects."); var value = Stack[Stack.Count - 1]; Stack.RemoveAt(Stack.Count - 1); return Read(value); }
                internal Value Read(Value value)
                {
                    var result = value.Copy(); var pending = new Queue<int>(result.Roots); var seen = new HashSet<int>();
                    while (pending.Count > 0) { int root = pending.Dequeue(); Value content; if (!seen.Add(root) || !Heap.TryGetValue(root, out content)) continue; result.Merge(content); foreach (int child in content.Roots) pending.Enqueue(child); }
                    return result;
                }
                internal void Write(Value address, Value value)
                { foreach (int root in address.Roots) { Value content; if (!Heap.TryGetValue(root, out content)) Heap[root] = value.Copy(); else content.Merge(value); } }
                internal bool Merge(Flow other)
                {
                    bool changed = false;
                    if (Stack.Count != other.Stack.Count) throw new ReflectionBindingException("UnsupportedRawSelectorFlow", "Inconsistent stack height while proving selected-value effects.");
                    for (int index = 0; index < Stack.Count; index++) changed |= Stack[index].Merge(other.Stack[index]);
                    foreach (var pair in other.Heap) { Value value; if (!Heap.TryGetValue(pair.Key, out value)) { Heap[pair.Key] = pair.Value.Copy(); changed = true; } else changed |= value.Merge(pair.Value); }
                    return changed;
                }
            }
            private sealed class Effects
            {
                internal readonly Value Returned = new Value();
                internal readonly HashSet<int> WrittenArguments = new HashSet<int>();
            }
            private void CheckSelectedEffects(MethodDef method)
            { AnalyzeEffects(method, null, new HashSet<MethodDef>()); }
            private Effects AnalyzeEffects(MethodDef method, Value[] inputs, HashSet<MethodDef> active)
            {
                var il = method.Body.Instructions;
                var effects = new Effects();
                if (!active.Add(method)) throw new ReflectionBindingException("UnsupportedRawSelectorFlow", "Recursive selected-value effect requires an unavailable proof: " + method.FullName);
                var arguments = method.Parameters.Where(parameter => !parameter.IsReturnTypeParameter).Select((parameter, index) =>
                    Value.Root(-index - 1, inputs == null || inputs[index].External, inputs != null && inputs[index].Selected)).ToArray();
                var indices = il.Select((instruction, index) => new { instruction, index }).ToDictionary(value => value.instruction, value => value.index);
                var states = new Flow[il.Count]; var pending = new Queue<int>();
                Action<int, Flow> enter = (index, flow) => { if (index < 0 || index >= states.Length) return; if (states[index] == null) { states[index] = flow.Copy(); pending.Enqueue(index); } else if (states[index].Merge(flow)) pending.Enqueue(index); };
                enter(0, new Flow());
                while (pending.Count > 0)
                {
                    int index = pending.Dequeue(); var state = states[index].Copy(); var instruction = il[index]; Code code = instruction.OpCode.Code;
                    foreach (var handler in method.Body.ExceptionHandlers)
                        if (handler.TryStart != null && index >= indices[handler.TryStart] && (handler.TryEnd == null || index < indices[handler.TryEnd]))
                        {
                            var exceptional = state.Copy(); exceptional.Stack.Clear();
                            if (handler.HandlerType == ExceptionHandlerType.Catch || handler.HandlerType == ExceptionHandlerType.Filter) exceptional.Stack.Add(new Value());
                            if (handler.FilterStart != null) enter(indices[handler.FilterStart], exceptional);
                            if (handler.HandlerStart != null) enter(indices[handler.HandlerStart], exceptional);
                        }
                    if (code == Code.Dup) { var value = state.Pop(); state.Stack.Add(value); state.Stack.Add(value.Copy()); }
                    else if (code.ToString().StartsWith("Ldloc", StringComparison.Ordinal) || code.ToString().StartsWith("Stloc", StringComparison.Ordinal))
                    {
                        var local = instruction.GetLocal(method.Body.Variables);
                        if (local == null) throw new ReflectionBindingException("UnsupportedRawSelectorFlow", "Missing local in " + method.FullName);
                        int slot = il.Count + 1 + method.Body.Variables.IndexOf(local);
                        if (code.ToString().StartsWith("Stloc", StringComparison.Ordinal)) state.Heap[slot] = state.Pop();
                        else if (code == Code.Ldloca || code == Code.Ldloca_S) state.Stack.Add(Value.Root(slot));
                        else { Value value; state.Stack.Add(state.Heap.TryGetValue(slot, out value) ? state.Read(value) : new Value()); }
                    }
                    else if (code.ToString().StartsWith("Ldarg", StringComparison.Ordinal) || code == Code.Starg || code == Code.Starg_S)
                    {
                        int slot = instruction.GetParameterIndex();
                        if (slot < 0 || slot >= arguments.Length) throw new ReflectionBindingException("UnsupportedRawSelectorFlow", "Invalid argument in " + method.FullName);
                        if (code == Code.Starg || code == Code.Starg_S) state.Heap[-slot - 1] = state.Pop();
                        else state.Stack.Add(state.Read(arguments[slot]));
                    }
                    else if (code == Code.Newarr)
                    { state.Pop(); state.Stack.Add(Value.Root(index + 1)); }
                    else if (code == Code.Stfld || code == Code.Stsfld)
                    {
                        var value = state.Pop(); var address = code == Code.Stsfld ? new Value { External = true } : state.Pop();
                        var field = (IField)instruction.Operand;
                        var owner = field.DeclaringType as TypeSpec; var generic = owner == null ? null : owner.TypeSig as GenericInstSig;
                        if (!value.Selected && !CarriesHandle(CloseType(field.FieldSig.Type, generic == null ? null : generic.GenericArguments, null))) value = new Value();
                        StoreSelected(method, index, state, address, value, effects, "RawSelectorFieldExposure");
                    }
                    else if (code.ToString().StartsWith("Stelem", StringComparison.Ordinal))
                    {
                        var value = state.Pop(); state.Pop(); var address = state.Pop();
                        StoreSelected(method, index, state, address, value, effects, "RawSelectorOutputExposure");
                    }
                    else if (code == Code.Stobj || code.ToString().StartsWith("Stind", StringComparison.Ordinal) || code == Code.Cpobj)
                    {
                        var value = state.Pop(); var address = state.Pop();
                        StoreSelected(method, index, state, address, value, effects, "RawSelectorIndirectStoreExposure");
                    }
                    else if (code == Code.Ldfld || code == Code.Ldflda || code == Code.Ldobj || code.ToString().StartsWith("Ldind", StringComparison.Ordinal))
                        state.Stack.Add(state.Read(state.Pop()));
                    else if (code.ToString().StartsWith("Ldelem", StringComparison.Ordinal))
                    { state.Pop(); state.Stack.Add(state.Read(state.Pop())); }
                    else if (code == Code.Ldsfld || code == Code.Ldsflda)
                        state.Stack.Add(new Value { External = true });
                    else if (code == Code.Ret)
                    {
                        if (method.MethodSig.RetType.ElementType != ElementType.Void) effects.Returned.Merge(state.Pop());
                    }
                    else if (code == Code.Call || code == Code.Callvirt || code == Code.Newobj)
                    {
                        var target = instruction.Operand as IMethod;
                        if (target == null || target.MethodSig == null) throw new ReflectionBindingException("UnsupportedRawSelectorFlow", method.FullName);
                        if (target.MethodSig.ParamsAfterSentinel != null && target.MethodSig.ParamsAfterSentinel.Count != 0)
                            throw new ReflectionBindingException("UnsupportedRawSelectorFlow", "Vararg selected-value effects are unsupported: " + target.FullName);
                        int count = target.MethodSig.Params.Count + (target.MethodSig.HasThis ? 1 : 0); var values = new Value[count];
                        for (int argument = count - 1; argument >= (code == Code.Newobj ? 1 : 0); argument--) values[argument] = state.Pop();
                        if (code == Code.Newobj) values[0] = Value.Root(index + 1);
                        var actual = Resolve(target); bool selected = Providers(new Call { Reference = target, Target = actual, Code = code }).Any();
                        bool tainted = values.Any(value => value.Selected); var returned = new Value();
                        if (tainted && code != Code.Newobj && DelegateType(target.DeclaringType.ToTypeSig()))
                            Error("RawSelectorCallbackExposure", method, "Selected handles are published through a callback at operation " + index + ".");
                        if (tainted && code == Code.Callvirt && actual != null && calls.ContainsKey(actual) && actual.IsVirtual && !actual.IsFinal && !actual.DeclaringType.IsSealed)
                            Error("RawSelectorUnprovedOutputCall", method, "Selected-value effects through overridable dispatch are unsupported at operation " + index + ".");
                        if (tainted && actual != null && calls.ContainsKey(actual) && HasOutputEffects(actual))
                        {
                            var nested = AnalyzeEffects(actual, values, new HashSet<MethodDef>(active));
                            returned.Selected = nested.Returned.Selected; returned.External = nested.Returned.External;
                            foreach (int root in nested.Returned.Roots)
                                if (root < 0) returned.Merge(values[-root - 1]); else returned.Roots.Add(index + 1);
                            foreach (int argument in nested.WrittenArguments) StoreSelected(method, index, state, values[argument], new Value { Selected = true }, effects, "RawSelectorOutputExposure");
                        }
                        else if (tainted && (actual == null || !calls.ContainsKey(actual)))
                        {
                            // Opaque Type diagnostics are not output selectors.
                            // A selected value plus a caller-owned mutable sink,
                            // however, needs an actual body/effect proof.
                            for (int argument = 0; argument < values.Length; argument++)
                            {
                                var signature = CallParameterType(target, argument);
                                if (MutableHandleContainer(signature)) StoreSelected(method, index, state, values[argument], new Value { Selected = true }, effects, "RawSelectorUnprovedOutputCall");
                            }
                        }
                        if (code == Code.Newobj || target.MethodSig.RetType.ElementType != ElementType.Void)
                        {
                            var value = code == Code.Newobj ? values[0].Copy() : returned.Copy();
                            var returnType = CloseCallType(target, target.MethodSig.RetType);
                            // Without a return-alias proof, a mutable result may
                            // still be a caller-owned input, not a fresh object.
                            if (code != Code.Newobj && MutableHandleContainer(returnType))
                                for (int argument = 0; argument < values.Length; argument++)
                                    if (MutableHandleContainer(CallParameterType(target, argument))) value.Merge(values[argument]);
                            if (code != Code.Newobj && value.Roots.Count == 0) value.Roots.Add(index + 1);
                            value.Selected |= selected || tainted && (code == Code.Newobj || CarriesHandle(returnType));
                            if (code == Code.Newobj || CarriesHandle(returnType)) value.External |= returned.External;
                            else { value.Selected = false; value.External = false; value.Roots.Clear(); }
                            state.Stack.Add(value);
                        }
                    }
                    else
                    {
                        int pushes, pops; instruction.CalculateStackUsage(out pushes, out pops); var value = new Value();
                        if (pops < 0) state.Stack.Clear(); else for (int pop = 0; pop < pops; pop++) value.Merge(state.Pop());
                        if (code == Code.Calli && value.Selected) Error("RawSelectorIndirectCallExposure", method, "Selected handles cross an unsupported calli at operation " + index + ".");
                        if ((code == Code.Cpblk || code == Code.Initblk) && value.Selected) Error("RawSelectorIndirectStoreExposure", method, "Selected handles cross an unsupported block store at operation " + index + ".");
                        if (code == Code.Throw && value.Selected) Error("RawSelectorThrownHandleExposure", method, "A selected handle/container escapes through a thrown object at operation " + index + ".");
                        if (code == Code.Ldlen || code == Code.Ceq || code == Code.Cgt || code == Code.Cgt_Un || code == Code.Clt || code == Code.Clt_Un) value = new Value();
                        for (int push = 0; push < pushes; push++) state.Stack.Add(value.Copy());
                    }
                    if (code == Code.Leave || code == Code.Leave_S) state.Stack.Clear();
                    var branch = instruction.Operand as Instruction; var branches = instruction.Operand as IList<Instruction>;
                    if (branch != null) enter(indices[branch], state);
                    if (branches != null) foreach (var target in branches) enter(indices[target], state);
                    if (instruction.OpCode.FlowControl != FlowControl.Branch && instruction.OpCode.FlowControl != FlowControl.Return && instruction.OpCode.FlowControl != FlowControl.Throw)
                        enter(index + 1, state);
                }
                active.Remove(method); return effects;
            }
            private void StoreSelected(MethodDef method, int index, Flow state, Value address, Value value, Effects effects, string code)
            {
                if (value.Selected)
                {
                    if (address.External || address.Roots.Count == 0) Error(code, method, "Selected handles escape through caller-owned/global/unknown storage at operation " + index + ".");
                    foreach (int root in address.Roots.Where(root => root < 0)) effects.WrittenArguments.Add(-root - 1);
                }
                if (address.External)
                    foreach (int root in value.Roots) state.Write(Value.Root(root), new Value { External = true });
                state.Write(address, value);
            }
            private static TypeSig CallParameterType(IMethod target, int index)
            { return target.MethodSig.HasThis && index == 0 ? target.DeclaringType.ToTypeSig() : CloseCallType(target, target.MethodSig.Params[index - (target.MethodSig.HasThis ? 1 : 0)]); }
            private static TypeSig CloseCallType(IMethod target, TypeSig signature)
            {
                var owner = target.DeclaringType as TypeSpec; var generic = owner == null ? null : owner.TypeSig as GenericInstSig; var method = target as MethodSpec;
                return CloseType(signature, generic == null ? null : generic.GenericArguments, method == null ? null : method.GenericInstMethodSig.GenericArguments);
            }
            private static TypeSig CloseType(TypeSig signature, IList<TypeSig> types, IList<TypeSig> methods)
            {
                var type = signature as GenericVar; if (type != null) return types != null && type.Number < types.Count ? types[(int)type.Number] : signature;
                var method = signature as GenericMVar; if (method != null) return methods != null && method.Number < methods.Count ? methods[(int)method.Number] : signature;
                if (signature == null || !signature.ContainsGenericParameter) return signature;
                var generic = signature as GenericInstSig; if (generic != null) return new GenericInstSig(generic.GenericType, generic.GenericArguments.Select(argument => CloseType(argument, types, methods)).ToArray());
                if (signature is SZArraySig) return new SZArraySig(CloseType(signature.Next, types, methods));
                var array = signature as ArraySig; if (array != null) return new ArraySig(CloseType(array.Next, types, methods), array.Rank, array.Sizes, array.LowerBounds);
                if (signature is ByRefSig) return new ByRefSig(CloseType(signature.Next, types, methods));
                if (signature is PtrSig) return new PtrSig(CloseType(signature.Next, types, methods));
                if (signature is PinnedSig) return new PinnedSig(CloseType(signature.Next, types, methods));
                var required = signature as CModReqdSig; if (required != null) return new CModReqdSig(required.Modifier, CloseType(required.Next, types, methods));
                var optional = signature as CModOptSig; if (optional != null) return new CModOptSig(optional.Modifier, CloseType(optional.Next, types, methods));
                throw new ReflectionBindingException("UnsupportedRawSelectorFlow", "Unsupported generic effect signature: " + signature.FullName);
            }
            private bool HasOutputEffects(MethodDef method)
            {
                bool cached; if (outputEffects.TryGetValue(method, out cached)) return cached;
                var pending = new Stack<MethodDef>(); var visited = new HashSet<MethodDef>(); pending.Push(method);
                while (pending.Count > 0)
                {
                    var current = pending.Pop(); if (!visited.Add(current)) continue;
                    foreach (var instruction in current.Body.Instructions)
                    {
                        string code = instruction.OpCode.Code.ToString();
                        if (code == "Stfld" || code == "Stsfld" || code == "Stobj" || code == "Cpobj" || code.StartsWith("Stind", StringComparison.Ordinal) || code.StartsWith("Stelem", StringComparison.Ordinal))
                            return outputEffects[method] = true;
                    }
                    foreach (var call in calls[current].Where(call => call.Code == Code.Call || call.Code == Code.Callvirt || call.Code == Code.Newobj))
                    {
                        if (call.Target != null && calls.ContainsKey(call.Target))
                        { if (call.Code == Code.Callvirt && call.Target.IsVirtual && !call.Target.IsFinal && !call.Target.DeclaringType.IsSealed) return outputEffects[method] = true; pending.Push(call.Target); continue; }
                        if (call.Code != Code.Newobj && DelegateType(call.Reference.DeclaringType.ToTypeSig())) return outputEffects[method] = true;
                        int count = call.Reference.MethodSig.Params.Count + (call.Reference.MethodSig.HasThis ? 1 : 0);
                        for (int index = 0; index < count; index++) if (MutableHandleContainer(CallParameterType(call.Reference, index))) return outputEffects[method] = true;
                    }
                }
                return outputEffects[method] = false;
            }
            private bool MutableHandleContainer(TypeSig signature)
            {
                if (signature == null || !CarriesHandle(signature)) return false;
                if (signature is ByRefSig || signature is ArraySig || signature is SZArraySig || signature is GenericVar || signature is GenericMVar) return true;
                string name = signature.FullName;
                if (name == "System.Type" || name == "System.Reflection.TypeInfo" || name == "System.Reflection.Assembly" || name == "System.Reflection.Module" ||
                    name == "System.Reflection.MemberInfo" || name == "System.Reflection.MethodBase" || name == "System.Reflection.MethodInfo" ||
                    name == "System.Reflection.ConstructorInfo" || name == "System.Reflection.FieldInfo" || name == "System.Reflection.PropertyInfo" || name == "System.Reflection.EventInfo") return false;
                return !DelegateType(signature);
            }
            private void Error(string code, MethodDef method, string text)
            { if (emitted.Add(code + "|" + method.FullName + "|" + text)) errors.Error(code, method.Module.Assembly.Name + " " + method.FullName + ": " + text); }
            private static bool Add<T>(Dictionary<T, HashSet<string>> target, T key, string value)
            { HashSet<string> values; if (!target.TryGetValue(key, out values)) { values = new HashSet<string>(StringComparer.OrdinalIgnoreCase); target.Add(key, values); } return values.Add(value); }
            private static bool Runtime(AssemblyDescriptor value) { return value.classification == AssemblyClassification.Runtime || value.classification == AssemblyClassification.NormalHotUpdate; }
            private static string Canonical(string name) { return AssemblyIdentityUtil.CanonicalName(name); }
        }
    }
}
