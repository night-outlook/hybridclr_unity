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
                    if (invoked.Count > 0) CheckFieldStores(method);
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
            { return OwnerProviders(ResolveType(reference)).Any(); }
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
                bool result = type.Fields.Any(field => !field.IsStatic && field.FieldSig != null && CarriesHandle(field.FieldSig.Type, pending));
                pending.Remove(identity);
                // A negative reached through a recursive container can depend on
                // an ancestor's other fields; only cache a root negative.
                if (result || pending.Count == 0) typeCache[identity] = result;
                return result;
            }

            private sealed class Flow
            {
                internal readonly List<bool> Stack = new List<bool>();
                internal bool[] Locals;
                internal Flow(int count) { Locals = new bool[count]; }
                internal Flow Copy() { var value = new Flow(Locals.Length); value.Stack.AddRange(Stack); Array.Copy(Locals, value.Locals, Locals.Length); return value; }
                internal bool Pop() { if (Stack.Count == 0) return false; bool value = Stack[Stack.Count - 1]; Stack.RemoveAt(Stack.Count - 1); return value; }
                internal bool Merge(Flow other)
                {
                    bool changed = false;
                    if (Stack.Count != other.Stack.Count) throw new ReflectionBindingException("UnsupportedRawSelectorFlow", "Inconsistent stack height while proving field exposure.");
                    for (int index = 0; index < Stack.Count; index++) if (other.Stack[index] && !Stack[index]) { Stack[index] = true; changed = true; }
                    for (int index = 0; index < Locals.Length; index++) if (other.Locals[index] && !Locals[index]) { Locals[index] = true; changed = true; }
                    return changed;
                }
            }
            private void CheckFieldStores(MethodDef method)
            {
                var il = method.Body.Instructions;
                if (!il.Any(instruction => (instruction.OpCode.Code == Code.Stfld || instruction.OpCode.Code == Code.Stsfld) &&
                    instruction.Operand is IField && CarriesHandle(((IField)instruction.Operand).FieldSig.Type))) return;
                var indices = il.Select((instruction, index) => new { instruction, index }).ToDictionary(value => value.instruction, value => value.index);
                var states = new Flow[il.Count]; var pending = new Queue<int>();
                Action<int, Flow> enter = (index, flow) => { if (index < 0 || index >= states.Length) return; if (states[index] == null) { states[index] = flow.Copy(); pending.Enqueue(index); } else if (states[index].Merge(flow)) pending.Enqueue(index); };
                enter(0, new Flow(method.Body.Variables.Count));
                while (pending.Count > 0)
                {
                    int index = pending.Dequeue(); var state = states[index].Copy(); var instruction = il[index]; Code code = instruction.OpCode.Code;
                    foreach (var handler in method.Body.ExceptionHandlers)
                        if (handler.TryStart != null && index >= indices[handler.TryStart] && (handler.TryEnd == null || index < indices[handler.TryEnd]))
                        {
                            var exceptional = state.Copy(); exceptional.Stack.Clear();
                            if (handler.HandlerType == ExceptionHandlerType.Catch || handler.HandlerType == ExceptionHandlerType.Filter) exceptional.Stack.Add(false);
                            if (handler.FilterStart != null) enter(indices[handler.FilterStart], exceptional);
                            if (handler.HandlerStart != null) enter(indices[handler.HandlerStart], exceptional);
                        }
                    if (code == Code.Dup) { bool value = state.Pop(); state.Stack.Add(value); state.Stack.Add(value); }
                    else if (code.ToString().StartsWith("Ldloc", StringComparison.Ordinal) || code.ToString().StartsWith("Stloc", StringComparison.Ordinal))
                    {
                        var local = instruction.GetLocal(method.Body.Variables);
                        if (local == null) throw new ReflectionBindingException("UnsupportedRawSelectorFlow", "Missing local in " + method.FullName);
                        int slot = method.Body.Variables.IndexOf(local);
                        if (code.ToString().StartsWith("Stloc", StringComparison.Ordinal)) state.Locals[slot] = state.Pop(); else state.Stack.Add(state.Locals[slot]);
                    }
                    else if (code == Code.Stfld || code == Code.Stsfld)
                    {
                        bool value = state.Pop(); var field = (IField)instruction.Operand;
                        if (value && CarriesHandle(field.FieldSig.Type)) Error("RawSelectorFieldExposure", method, "A selected handle/container is stored in " + field.FullName + " at operation " + index + ".");
                        if (code == Code.Stfld) state.Pop();
                    }
                    else if (code == Code.Call || code == Code.Callvirt || code == Code.Newobj)
                    {
                        var target = instruction.Operand as IMethod;
                        if (target == null || target.MethodSig == null) throw new ReflectionBindingException("UnsupportedRawSelectorFlow", method.FullName);
                        bool tainted = false; for (int argument = 0; argument < target.MethodSig.Params.Count; argument++) tainted |= state.Pop();
                        if (target.MethodSig.HasThis && code != Code.Newobj) tainted |= state.Pop();
                        bool selected = Providers(new Call { Reference = target, Target = Resolve(target), Code = code }).Any();
                        if (tainted && target.MethodSig.HasThis) MarkContainers(method, state);
                        if (code == Code.Newobj || target.MethodSig.RetType.ElementType != ElementType.Void)
                            state.Stack.Add(selected || tainted && (code == Code.Newobj || CarriesHandle(target.MethodSig.RetType)));
                    }
                    else
                    {
                        int pushes, pops; instruction.CalculateStackUsage(out pushes, out pops); bool value = false;
                        if (pops < 0) { value = state.Stack.Any(item => item); state.Stack.Clear(); } else for (int pop = 0; pop < pops; pop++) value |= state.Pop();
                        if (value && code.ToString().StartsWith("Stelem", StringComparison.Ordinal)) MarkContainers(method, state);
                        if (value && (code == Code.Starg || code == Code.Starg_S || code == Code.Stind_Ref || code == Code.Stobj))
                            Error("RawSelectorIndirectStoreExposure", method, "Selected handle escapes through an unsupported argument/indirect store at operation " + index + ".");
                        for (int push = 0; push < pushes; push++) state.Stack.Add(value);
                    }
                    if (code == Code.Leave || code == Code.Leave_S) state.Stack.Clear();
                    var branch = instruction.Operand as Instruction; var branches = instruction.Operand as IList<Instruction>;
                    if (branch != null) enter(indices[branch], state);
                    if (branches != null) foreach (var target in branches) enter(indices[target], state);
                    if (instruction.OpCode.FlowControl != FlowControl.Branch && instruction.OpCode.FlowControl != FlowControl.Return && instruction.OpCode.FlowControl != FlowControl.Throw)
                        enter(index + 1, state);
                }
            }
            private void MarkContainers(MethodDef method, Flow state)
            {
                // A selected value inserted into a local aggregate can alias an
                // earlier local. Conservatively retain it in every handle-bearing
                // local; unrelated cached lambda constants remain untainted.
                for (int index = 0; index < state.Locals.Length; index++)
                    if (CarriesHandle(method.Body.Variables[index].Type)) state.Locals[index] = true;
                for (int index = 0; index < state.Stack.Count; index++) state.Stack[index] = true;
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
