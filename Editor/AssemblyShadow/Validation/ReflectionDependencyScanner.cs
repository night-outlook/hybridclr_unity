using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using HybridCLR.AssemblyShadow.CodeGen;

namespace HybridCLR.Editor.AssemblyShadow
{
    // A deliberately small IL abstract interpreter. Only values proven on every
    // incoming control-flow path are constants. Unknown values are never guessed
    // from nearby instructions, names, editor-domain types, or approved strings.
    internal static class ReflectionDependencyScanner
    {
        private sealed class Value
        {
            internal string kind, text, assembly;
            internal static Value String(string value) { return new Value { kind = "string", text = value }; }
            internal static Value Assembly(string name) { return new Value { kind = "assembly", assembly = name }; }
            internal static Value Type(string name, string assembly) { return new Value { kind = "type", text = name, assembly = assembly }; }
            internal static bool Same(Value a, Value b)
            {
                return ReferenceEquals(a, b) || (a != null && b != null && a.kind == b.kind && a.text == b.text && a.assembly == b.assembly);
            }
        }

        private sealed class State
        {
            internal readonly List<Value> stack = new List<Value>();
            internal Value[] locals;
            internal bool invalid;
            internal State(int localCount) { locals = new Value[localCount]; }
            internal State Copy()
            {
                var copy = new State(locals.Length) { invalid = invalid };
                copy.stack.AddRange(stack); Array.Copy(locals, copy.locals, locals.Length); return copy;
            }
            internal Value Pop()
            {
                if (stack.Count == 0) { invalid = true; return null; }
                Value value = stack[stack.Count - 1]; stack.RemoveAt(stack.Count - 1); return value;
            }
            internal bool Merge(State incoming)
            {
                bool changed = false;
                if (!invalid && (incoming.invalid || stack.Count != incoming.stack.Count)) { invalid = true; changed = true; }
                for (int index = 0; index < stack.Count; ++index)
                    if (stack[index] != null && (index >= incoming.stack.Count || !Value.Same(stack[index], incoming.stack[index])))
                    { stack[index] = null; changed = true; }
                for (int index = 0; index < locals.Length; ++index)
                    if (locals[index] != null && !Value.Same(locals[index], incoming.locals[index]))
                    { locals[index] = null; changed = true; }
                return changed;
            }
        }

        internal static void Scan(IReadOnlyDictionary<string, ModuleDefMD> modules, string assemblyName, AssemblyPolicyDefinition definition)
        { ScanVerified(modules, assemblyName, definition, new VerifiedReflectionBinding[0]); }

        internal static void ScanVerified(IReadOnlyDictionary<string, ModuleDefMD> modules, string assemblyName, AssemblyPolicyDefinition definition,
            IEnumerable<VerifiedReflectionBinding> verifiedBindings, IEnumerable<VerifiedRawTypeAdmission> rawTypeAdmissions = null)
        {
            ModuleDefMD module;
            if (modules == null || !modules.TryGetValue(assemblyName, out module))
            { definition.unknownReflectionDependencies = true; return; }
            var evidence = new List<ReflectionDependencyEvidence>();
            var acquisitions = new List<ManagedAcquisitionEvidence>();
            // Loader dictionary keys are canonical, while verified configuration
            // identities retain their original case. Match using the same rule.
            string canonicalAssembly = AssemblyIdentityUtil.CanonicalName(assemblyName);
            var assemblyBindings = verifiedBindings.Where(binding => AssemblyIdentityUtil.CanonicalName(binding.Assembly) == canonicalAssembly).ToArray();
            var rawAdmissions = (rawTypeAdmissions ?? new VerifiedRawTypeAdmission[0]).Where(binding =>
                AssemblyIdentityUtil.CanonicalName(new AssemblyNameInfo(binding.ConsumerAssemblyIdentity).Name.String) == canonicalAssembly).ToArray();
            definition.rawTypeAdmissionDependencies.Clear();
            var guardedSites = assemblyBindings.ToDictionary(binding => binding.OriginalMethod);
            var fixedGuards = assemblyBindings.Where(binding => binding.Kind == "FixedAssemblyBytes")
                .ToDictionary(binding => binding.GuardMethod);
            var unknown = new HashSet<string>(StringComparer.Ordinal);
            var staticReferences = new HashSet<string>(definition.references ?? new string[0], StringComparer.OrdinalIgnoreCase);
            var stateMachines = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (TypeDef type in module.GetTypes())
                foreach (MethodDef method in type.Methods)
                    foreach (CustomAttribute attribute in method.CustomAttributes.Where(item =>
                        item.TypeFullName == "System.Runtime.CompilerServices.IteratorStateMachineAttribute" ||
                        item.TypeFullName == "System.Runtime.CompilerServices.AsyncStateMachineAttribute"))
                        if (attribute.ConstructorArguments.Count == 1)
                        {
                            TypeSig generated = attribute.ConstructorArguments[0].Value as TypeSig;
                            if (generated != null) stateMachines[generated.FullName] = type.FullName + "::" + method.Name;
                        }
            foreach (TypeDef type in module.GetTypes())
            {
                foreach (FieldDef field in type.Fields)
                    if (field.CustomAttributes.Any(attribute => attribute.TypeFullName == "UnityEngine.SerializeReference"))
                        unknown.Add(type.FullName + "::" + field.Name);
                foreach (MethodDef method in type.Methods)
                {
                    if (method.Body == null || method.Body.Instructions.Count == 0) continue;
                    string callSite = type.FullName + "::" + method.Name;
                    string original;
                    if (method.Name == "MoveNext" && stateMachines.TryGetValue(type.FullName, out original)) callSite = original;
                    bool needsValueAnalysis = NeedsValueAnalysis(method);
                    var freshNames = needsValueAnalysis ? FindFreshAssemblyNames(method, modules) : new Dictionary<Instruction, string>();
                    State[] states = needsValueAnalysis ? Analyze(method, fixedGuards, freshNames, modules) : null;
                    for (int index = 0; index < method.Body.Instructions.Count; ++index)
                    {
                        Instruction instruction = method.Body.Instructions[index];
                        if (instruction.OpCode.Code == Code.Ldtoken)
                        {
                            ITypeDefOrRef referencedType = instruction.Operand as ITypeDefOrRef;
                            if (referencedType != null && referencedType.DefinitionAssembly != null)
                            {
                                string staticProvider = referencedType.DefinitionAssembly.Name.String;
                                if (!string.Equals(staticProvider, assemblyName, StringComparison.OrdinalIgnoreCase)) staticReferences.Add(staticProvider);
                            }
                        }
                        IMethod called = instruction.Operand as IMethod;
                        if (called == null) continue;
                        if (instruction.OpCode.Code == Code.Ldftn || instruction.OpCode.Code == Code.Ldvirtftn)
                        {
                            string indirectKind; int indirectArgument;
                            string acquisition = AcquisitionKind(called);
                            if (acquisition != null || IsReflectionCall(called, out indirectKind, out indirectArgument))
                                acquisitions.Add(Acquisition(method, called, callSite, index, "IndirectAcquisition", true, null));
                            continue;
                        }
                        if (instruction.OpCode.Code != Code.Call && instruction.OpCode.Code != Code.Callvirt) continue;
                        var rawAdmission = rawAdmissions.FirstOrDefault(binding => binding.Matches(method, index));
                        if (rawAdmission != null)
                        {
                            bool receiverLoad = index == rawAdmission.ReceiverLoadIndex;
                            string rawKind = receiverLoad ? "Assembly.Load" : rawAdmission.Kind;
                            string providerName = AssemblyIdentityUtil.CanonicalName(new AssemblyNameInfo(rawAdmission.ProviderAssemblyIdentity).Name.String);
                            var rawEvidence = new ReflectionDependencyEvidence { callSite = callSite, provider = providerName,
                                target = receiverLoad || string.IsNullOrEmpty(rawAdmission.TypeName) ? rawAdmission.ReceiverLiteral : rawAdmission.TypeName,
                                typeName = rawAdmission.TypeName, kind = "RawTypeAdmission." + rawKind };
                            evidence.Add(rawEvidence); definition.rawTypeAdmissionDependencies.Add(rawEvidence);
                            var rawAcquisition = Acquisition(method, called, callSite, index, rawKind, true, null);
                            rawAcquisition.verified = true; rawAcquisition.configurationHash = rawAdmission.ConfigurationHash;
                            rawAcquisition.siteId = rawAdmission.SiteId; rawAcquisition.provider = providerName;
                            acquisitions.Add(rawAcquisition);
                            continue;
                        }
                        VerifiedReflectionBinding guardedSite;
                        if (guardedSites.TryGetValue(method, out guardedSite) && index == guardedSite.OperationIndex)
                            acquisitions.Add(Acquisition(method, called, callSite, index, guardedSite.Kind, true, guardedSite));
                        string acquisitionKind = AcquisitionKind(called);
                        if (acquisitionKind != null)
                        {
                            VerifiedReflectionBinding binding;
                            bool bound = acquisitionKind == "Assembly.LoadBytes" && fixedGuards.TryGetValue(method, out binding);
                            binding = bound ? fixedGuards[method] : null;
                            acquisitions.Add(Acquisition(method, called, callSite, index, acquisitionKind,
                                !IsHandleOnlyAcquisition(acquisitionKind), binding));
                            if (binding != null)
                                evidence.Add(new ReflectionDependencyEvidence { callSite = binding.TypeName + "::" + binding.OriginalMethod.Name,
                                    target = binding.ImageSha256, provider = binding.Providers.Single(), kind = "FixedAssemblyBytes" });
                            // Handle enumeration is recorded, not claimed as type acquisition.
                            // Type enumeration and image loads require exact generated proof.
                            continue;
                        }
                        string kind;
                        int argumentIndex;
                        if (!IsReflectionCall(called, out kind, out argumentIndex)) continue;
                        State input = states == null ? null : states[index];
                        Value argument = Argument(input, called, argumentIndex);
                        string freshName;
                        if (kind == "Assembly.Load" && input != null && !input.invalid && freshNames.TryGetValue(instruction, out freshName))
                            argument = Value.String(freshName);
                        if (argument == null || argument.kind != "string" || string.IsNullOrWhiteSpace(argument.text))
                        { unknown.Add(callSite); acquisitions.Add(Acquisition(method, called, callSite, index, kind, true, null)); continue; }
                        // Resolver-delegate overloads can resolve the same literal
                        // against arbitrary assemblies. A string is never proof
                        // for those overloads, including on the legacy simple path.
                        if ((kind == "Type.GetType" || kind == "Assembly.GetType") &&
                            (argumentIndex != 0 || called.MethodSig.Params.Count > 3 ||
                             called.MethodSig.Params.Skip(1).Any(parameter => parameter.ElementType != ElementType.Boolean)))
                        { unknown.Add(callSite); acquisitions.Add(Acquisition(method, called, callSite, index, kind, true, null)); continue; }
                        string provider = null, typeName = null;
                        if (kind == "Assembly.Load")
                            provider = freshNames.ContainsKey(instruction) ? NameLoadProvider(argument.text) :
                                StringNameLoadProvider(called, method.Module, modules, argument.text);
                        else
                        {
                            Value receiver = Receiver(input, called);
                            string requiredAssembly = kind == "Assembly.GetType" && receiver != null && receiver.kind == "assembly" ? receiver.assembly : null;
                            if (kind == "Assembly.GetType" && requiredAssembly == null) { unknown.Add(callSite); acquisitions.Add(Acquisition(method, called, callSite, index, kind, true, null)); continue; }
                            if ((kind == "Type.GetType" || kind == "Assembly.GetType") && argument.text.IndexOf('[') >= 0)
                            {
                                ReflectionTypeLiteralResolver.Component[] components;
                                if (!IsTrustedConstructedTypeLookup(called, method.Module, modules, kind) ||
                                    !ReflectionTypeLiteralResolver.TryResolve(modules, argument.text, requiredAssembly, out components))
                                { unknown.Add(callSite); acquisitions.Add(Acquisition(method, called, callSite, index, kind, true, null)); continue; }
                                // Resolve the whole tree before exposing any
                                // evidence: an unresolved argument invalidates
                                // the entire acquisition, never just that edge.
                                foreach (var component in components)
                                    evidence.Add(new ReflectionDependencyEvidence { callSite = callSite, target = argument.text,
                                        provider = component.provider, typeName = component.typeName, kind = kind });
                                continue;
                            }
                            ResolveType(modules, argument.text, requiredAssembly, kind == "GetComponent", out provider, out typeName);
                        }
                        if (provider == null || !modules.ContainsKey(provider)) { unknown.Add(callSite); acquisitions.Add(Acquisition(method, called, callSite, index, kind, true, null)); continue; }
                        evidence.Add(new ReflectionDependencyEvidence { callSite = callSite, target = argument.text,
                            provider = provider, typeName = typeName, kind = kind });
                    }
                }
            }
            definition.references = staticReferences.OrderBy(value => value, StringComparer.Ordinal).ToArray();
            definition.reflectionDependencies = evidence.GroupBy(item => item.callSite + "\n" + item.kind + "\n" + item.target + "\n" + item.provider + "\n" + item.typeName)
                .Select(group => group.First()).ToArray();
            definition.unknownReflectionCallSites = unknown.OrderBy(value => value, StringComparer.Ordinal).ToArray();
            definition.managedAcquisitions = acquisitions.ToArray();
        }

        private static bool NeedsValueAnalysis(MethodDef method)
        {
            foreach (Instruction instruction in method.Body.Instructions)
            {
                if (instruction.OpCode.Code != Code.Call && instruction.OpCode.Code != Code.Callvirt) continue;
                string kind; int argument;
                if (IsReflectionCall(instruction.Operand as IMethod, out kind, out argument)) return true;
            }
            return false;
        }

        private static ManagedAcquisitionEvidence Acquisition(MethodDef method, IMethod called, string callSite, int index,
            string kind, bool requiresContract, VerifiedReflectionBinding binding)
        {
            return new ManagedAcquisitionEvidence { kind = kind, callSite = callSite,
                methodSignature = ReflectionBindingFingerprint.MethodSignature(method), methodHash = ReflectionBindingFingerprint.Compute(method),
                operationIndex = index, operationSignature = called.FullName, requiresContract = requiresContract,
                verified = binding != null, configurationHash = binding == null ? null : binding.ConfigurationHash,
                siteId = binding == null ? null : binding.SiteId, provider = binding == null || binding.Providers.Length != 1 ? null : binding.Providers[0] };
        }

        private static string AcquisitionKind(IMethod method)
        {
            if (method.DeclaringType == null || method.MethodSig == null) return null;
            string owner = method.DeclaringType.FullName, name = method.Name.String;
            if (owner == "System.AppDomain" && name == "GetAssemblies") return "AppDomain.GetAssemblies";
            if (owner == "System.AppDomain" && name == "Load") return "AppDomain.Load";
            if (owner == "System.Reflection.Module")
            {
                // Member and attribute metadata can expose declaring/attribute
                // types just as directly as a type enumeration or token lookup.
                // Except for independently verified literal raw-admission sites,
                // these operations have no supported finite contract.
                switch (name)
                {
                    case "GetTypes": case "GetType": case "FindTypes":
                    case "ResolveType": case "ResolveMember": case "ResolveMethod": case "ResolveField":
                    case "GetMethod": case "GetMethods": case "GetField": case "GetFields":
                    case "GetCustomAttributes": case "GetCustomAttributesData": case "get_CustomAttributes":
                    case "get_ModuleHandle": case "get_Assembly":
                        return "Module." + name;
                }
            }
            if (owner == "System.ModuleHandle")
            {
                switch (name)
                {
                    case "ResolveTypeHandle": case "ResolveMethodHandle": case "ResolveFieldHandle":
                    case "GetRuntimeTypeHandleFromMetadataToken": case "GetRuntimeMethodHandleFromMetadataToken": case "GetRuntimeFieldHandleFromMetadataToken":
                        return "ModuleHandle." + name;
                }
            }
            if (owner != "System.Reflection.Assembly") return null;
            if (name == "GetModule" || name == "GetModules" || name == "GetLoadedModules" || name == "get_Modules" || name == "get_ManifestModule") return "Assembly." + name;
            if (name == "GetTypes" || name == "GetExportedTypes" || name == "get_DefinedTypes" || name == "get_ExportedTypes") return "Assembly." + name;
            if (name == "Load" && method.MethodSig.Params.Count > 0 && method.MethodSig.Params[0].FullName == "System.Byte[]") return "Assembly.LoadBytes";
            // Other acquisition entrypoints are not string-name dependencies.
            if (name == "LoadFrom" || name == "LoadFile" || name == "UnsafeLoadFrom" || name == "ReflectionOnlyLoad" || name == "ReflectionOnlyLoadFrom") return "Assembly." + name;
            return null;
        }

        private static bool IsHandleOnlyAcquisition(string kind)
        {
            return kind == "AppDomain.GetAssemblies" || kind == "Assembly.GetModule" || kind == "Assembly.GetModules" ||
                kind == "Assembly.GetLoadedModules" || kind == "Assembly.get_Modules" || kind == "Assembly.get_ManifestModule" ||
                kind == "Module.get_ModuleHandle" || kind == "Module.get_Assembly";
        }

        private static State[] Analyze(MethodDef method, IDictionary<MethodDef, VerifiedReflectionBinding> fixedGuards,
            IDictionary<Instruction, string> freshNames, IReadOnlyDictionary<string, ModuleDefMD> modules)
        {
            IList<Instruction> instructions = method.Body.Instructions;
            var indices = new Dictionary<Instruction, int>();
            for (int index = 0; index < instructions.Count; ++index) indices[instructions[index]] = index;
            var inputs = new State[instructions.Count];
            var pending = new Queue<int>();
            Action<int, State> offer = (index, state) =>
            {
                if (index < 0 || index >= inputs.Length) return;
                if (inputs[index] == null) { inputs[index] = state.Copy(); pending.Enqueue(index); }
                else if (inputs[index].Merge(state)) pending.Enqueue(index);
            };
            offer(0, new State(method.Body.Variables.Count));
            foreach (ExceptionHandler handler in method.Body.ExceptionHandlers)
            {
                var state = new State(method.Body.Variables.Count);
                if (handler.HandlerType == ExceptionHandlerType.Catch || handler.HandlerType == ExceptionHandlerType.Filter) state.stack.Add(null);
                if (handler.HandlerStart != null) offer(indices[handler.HandlerStart], state);
                if (handler.FilterStart != null) offer(indices[handler.FilterStart], state);
            }
            while (pending.Count != 0)
            {
                int index = pending.Dequeue();
                State output = inputs[index].Copy();
                Instruction instruction = instructions[index];
                Execute(instruction, output, fixedGuards, freshNames, method.Module, modules);
                FlowControl flow = instruction.OpCode.FlowControl;
                Instruction target = instruction.Operand as Instruction;
                if (target != null && (flow == FlowControl.Branch || flow == FlowControl.Cond_Branch)) offer(indices[target], output);
                IList<Instruction> targets = instruction.Operand as IList<Instruction>;
                if (targets != null) foreach (Instruction branch in targets) offer(indices[branch], output);
                if (flow != FlowControl.Branch && flow != FlowControl.Return && flow != FlowControl.Throw) offer(index + 1, output);
            }
            return inputs;
        }

        private static void Execute(Instruction instruction, State state, IDictionary<MethodDef, VerifiedReflectionBinding> fixedGuards,
            IDictionary<Instruction, string> freshNames, ModuleDef module, IReadOnlyDictionary<string, ModuleDefMD> modules)
        {
            Code code = instruction.OpCode.Code;
            if (code == Code.Ldstr) { state.stack.Add(Value.String(instruction.Operand as string)); return; }
            if (code == Code.Dup) { Value value = state.Pop(); state.stack.Add(value); state.stack.Add(value); return; }
            int local = LocalIndex(instruction);
            if (local >= 0)
            {
                bool store = code == Code.Stloc || code == Code.Stloc_S || code == Code.Stloc_0 || code == Code.Stloc_1 || code == Code.Stloc_2 || code == Code.Stloc_3;
                if (local >= state.locals.Length) { state.invalid = true; return; }
                if (store) state.locals[local] = state.Pop(); else state.stack.Add(state.locals[local]);
                return;
            }
            if (code == Code.Ldloca || code == Code.Ldloca_S)
            {
                Local variable = instruction.Operand as Local;
                if (variable != null && variable.Index < state.locals.Length) state.locals[variable.Index] = null;
            }
            if (code == Code.Ldtoken)
            {
                ITypeDefOrRef type = instruction.Operand as ITypeDefOrRef;
                state.stack.Add(type == null || type.DefinitionAssembly == null ? null : Value.Type(type.FullName, type.DefinitionAssembly.Name.String)); return;
            }
            if (code == Code.Call || code == Code.Callvirt || code == Code.Newobj)
            {
                IMethod method = instruction.Operand as IMethod;
                if (method == null || method.MethodSig == null) { state.invalid = true; return; }
                var args = new Value[method.MethodSig.Params.Count];
                for (int index = args.Length - 1; index >= 0; --index) args[index] = state.Pop();
                Value receiver = method.MethodSig.HasThis && code != Code.Newobj ? state.Pop() : null;
                Value value = null;
                string owner = method.DeclaringType.FullName;
                VerifiedReflectionBinding bound;
                string freshName;
                if (method is MethodDef && fixedGuards.TryGetValue((MethodDef)method, out bound)) value = Value.Assembly(bound.Providers.Single());
                else if (freshNames.TryGetValue(instruction, out freshName)) value = Value.Assembly(NameLoadProvider(freshName));
                else if (owner == "System.Reflection.Assembly" && method.Name == "Load" && args.Length > 0 && args[0] != null && args[0].kind == "string")
                {
                    string provider = StringNameLoadProvider(method, module, modules, args[0].text);
                    if (provider != null && modules.ContainsKey(provider)) value = Value.Assembly(provider);
                }
                else if (owner == "System.Type" && method.Name == "GetTypeFromHandle" && args.Length == 1 && args[0] != null && args[0].kind == "type") value = args[0];
                else if (owner == "System.Type" && method.Name == "get_Assembly" && receiver != null && receiver.kind == "type") value = Value.Assembly(receiver.assembly);
                else if (owner == "System.String" && method.Name == "Concat" && args.Length >= 2 && args.All(arg => arg != null && arg.kind == "string"))
                    value = Value.String(string.Concat(args.Select(arg => arg.text).ToArray()));
                if (code == Code.Newobj || method.MethodSig.RetType.ElementType != ElementType.Void) state.stack.Add(value);
                return;
            }
            if (code == Code.Leave || code == Code.Leave_S) { state.stack.Clear(); return; }
            int pushes, pops;
            instruction.CalculateStackUsage(out pushes, out pops);
            if (pops < 0) state.stack.Clear(); else for (int index = 0; index < pops; ++index) state.Pop();
            for (int index = 0; index < pushes; ++index) state.stack.Add(null);
        }

        private static int LocalIndex(Instruction instruction)
        {
            switch (instruction.OpCode.Code)
            {
                case Code.Ldloc_0: case Code.Stloc_0: return 0;
                case Code.Ldloc_1: case Code.Stloc_1: return 1;
                case Code.Ldloc_2: case Code.Stloc_2: return 2;
                case Code.Ldloc_3: case Code.Stloc_3: return 3;
                case Code.Ldloc: case Code.Ldloc_S: case Code.Stloc: case Code.Stloc_S:
                    Local local = instruction.Operand as Local; return local == null ? -1 : local.Index;
                default: return -1;
            }
        }

        private static Value Argument(State state, IMethod method, int parameter)
        {
            if (state == null || state.invalid || parameter < 0) return null;
            int index = state.stack.Count - method.MethodSig.Params.Count + parameter;
            return index < 0 || index >= state.stack.Count ? null : state.stack[index];
        }
        private static Value Receiver(State state, IMethod method)
        {
            if (state == null || state.invalid || !method.MethodSig.HasThis) return null;
            int index = state.stack.Count - method.MethodSig.Params.Count - 1;
            return index < 0 ? null : state.stack[index];
        }
        private static bool IsReflectionCall(IMethod method, out string kind, out int parameter)
        {
            kind = null; parameter = -1;
            if (method.MethodSig == null || method.DeclaringType == null) return false;
            string owner = method.DeclaringType.FullName, name = method.Name.String;
            for (int index = 0; index < method.MethodSig.Params.Count; ++index)
                if (method.MethodSig.Params[index].FullName == "System.String") { parameter = index; break; }
            if (owner == "System.Type" && name == "GetType" && parameter >= 0) kind = "Type.GetType";
            else if (owner == "System.Reflection.Assembly" && name == "GetType" && parameter >= 0) kind = "Assembly.GetType";
            else if (owner == "System.Reflection.Assembly" && name == "Load")
            {
                // Byte arrays are tracked separately as typed acquisition evidence.
                if (method.MethodSig.Params.Count > 0 && method.MethodSig.Params[0].FullName == "System.Byte[]") return false;
                kind = "Assembly.Load";
            }
            else if ((owner == "UnityEngine.GameObject" || owner == "UnityEngine.Component") && name == "GetComponent" && parameter >= 0) kind = "GetComponent";
            else if ((name == "Register" || name == "RegisterType" || name == "AddSingleton" || name == "AddTransient") && parameter >= 0) kind = "DI";
            return kind != null;
        }

        // AssemblyName is mutable. Never propagate it through the abstract
        // interpreter: prove only this literal/constructor/call instruction
        // sequence, with no alternate entry or alias-producing instruction.
        private static Dictionary<Instruction, string> FindFreshAssemblyNames(MethodDef method,
            IReadOnlyDictionary<string, ModuleDefMD> modules)
        {
            var result = new Dictionary<Instruction, string>();
            IList<Instruction> il = method.Body.Instructions;
            var entries = new HashSet<Instruction>();
            foreach (Instruction instruction in il)
            {
                var target = instruction.Operand as Instruction;
                if (target != null) entries.Add(target);
                var targets = instruction.Operand as IList<Instruction>;
                if (targets != null) foreach (Instruction item in targets) entries.Add(item);
            }
            foreach (ExceptionHandler handler in method.Body.ExceptionHandlers)
            {
                entries.Add(handler.TryStart); entries.Add(handler.TryEnd);
                entries.Add(handler.HandlerStart); entries.Add(handler.HandlerEnd); entries.Add(handler.FilterStart);
            }
            for (int index = 0; index < il.Count; ++index)
            {
                if (il[index].OpCode.Code != Code.Call || !IsTrustedNameLoad(il[index].Operand as IMethod, method.Module, modules, "System.Reflection.AssemblyName")) continue;
                int constructor = PreviousNonNop(il, index), literal = PreviousNonNop(il, constructor);
                if (literal < 0 || il[literal].OpCode.Code != Code.Ldstr || il[constructor].OpCode.Code != Code.Newobj) continue;
                var called = il[constructor].Operand as IMethod;
                if (called == null || called is MethodSpec || called.Name != ".ctor" || called.MethodSig == null) continue;
                MethodSig sig = called.MethodSig;
                if (sig.CallingConvention != CallingConvention.HasThis || sig.GenParamCount != 0 || sig.ParamsAfterSentinel != null ||
                    sig.Params.Count != 1 || sig.Params[0].ElementType != ElementType.String || sig.RetType.ElementType != ElementType.Void ||
                    !IsTrustedCoreType(called.DeclaringType, "System.Reflection.AssemblyName", method.Module, modules)) continue;
                bool singleEntry = true;
                for (int cursor = literal + 1; cursor <= index; ++cursor) if (entries.Contains(il[cursor])) singleEntry = false;
                string name = il[literal].Operand as string, provider = NameLoadProvider(name);
                if (singleEntry && provider != null && modules.ContainsKey(provider)) result.Add(il[index], name);
            }
            return result;
        }

        private static int PreviousNonNop(IList<Instruction> il, int index)
        {
            while (--index >= 0 && il[index].OpCode.Code == Code.Nop) { }
            return index;
        }

        private static bool IsTrustedNameLoad(IMethod method, ModuleDef module,
            IReadOnlyDictionary<string, ModuleDefMD> modules, string parameter)
        {
            if (method == null || method is MethodSpec || method.Name != "Load" || method.MethodSig == null) return false;
            MethodSig sig = method.MethodSig;
            return sig.CallingConvention == CallingConvention.Default && sig.GenParamCount == 0 && sig.ParamsAfterSentinel == null && sig.Params.Count == 1 &&
                IsTrustedCoreType(method.DeclaringType, "System.Reflection.Assembly", module, modules) &&
                sig.RetType is ClassSig && IsTrustedCoreType(sig.RetType.ToTypeDefOrRef(), "System.Reflection.Assembly", module, modules) &&
                (parameter == "System.String" ? sig.Params[0].ElementType == ElementType.String :
                 sig.Params[0] is ClassSig && IsTrustedCoreType(sig.Params[0].ToTypeDefOrRef(), parameter, module, modules));
        }

        private static bool IsTrustedConstructedTypeLookup(IMethod method, ModuleDef module,
            IReadOnlyDictionary<string, ModuleDefMD> modules, string kind)
        {
            if (method == null || method is MethodSpec || method.MethodSig == null) return false;
            MethodSig signature = method.MethodSig;
            // No resolver callbacks or ignoreCase overload. The latter needs a
            // separate ambiguity proof and is not part of this bounded grammar.
            return signature.CallingConvention == (kind == "Assembly.GetType" ? CallingConvention.HasThis : CallingConvention.Default) &&
                signature.GenParamCount == 0 && signature.ParamsAfterSentinel == null &&
                signature.Params.Count >= 1 && signature.Params.Count <= 2 && signature.Params[0].ElementType == ElementType.String &&
                (signature.Params.Count == 1 || signature.Params[1].ElementType == ElementType.Boolean) &&
                IsTrustedCoreType(method.DeclaringType, kind == "Assembly.GetType" ? "System.Reflection.Assembly" : "System.Type", module, modules) &&
                signature.RetType is ClassSig && IsTrustedCoreType(signature.RetType.ToTypeDefOrRef(), "System.Type", module, modules);
        }

        private static string StringNameLoadProvider(IMethod method, ModuleDef module,
            IReadOnlyDictionary<string, ModuleDefMD> modules, string value)
        {
            if (method == null || method.DeclaringType == null || method.DeclaringType.FullName != "System.Reflection.Assembly" ||
                method.Name != "Load" || method.MethodSig == null || method.MethodSig.Params.Count == 0 ||
                method.MethodSig.Params[0].ElementType != ElementType.String) return null;
            // Preserve the existing string-name API/compatible-core analysis.
            // Only newly supported path forms require the stronger exact
            // captured-framework proof; never apply it as a global resolver rule.
            if (value != null && (value.IndexOf('/') >= 0 || value.IndexOf('\\') >= 0) &&
                !IsTrustedNameLoad(method, module, modules, "System.String")) return null;
            return NameLoadProvider(value);
        }

        private static bool IsTrustedCoreType(ITypeDefOrRef type, string name, ModuleDef module,
            IReadOnlyDictionary<string, ModuleDefMD> modules)
        {
            // Production loading authenticates the supplied framework catalog.
            // Do not resolve against the Editor domain/GAC or accept a business
            // assembly merely because it declares a framework-looking type.
            if (!(type is TypeRef) || type.FullName != name || type.DefinitionAssembly == null) return false;
            IAssembly scope = type.DefinitionAssembly;
            ModuleDefMD captured;
            return scope.FullName == module.CorLibTypes.AssemblyRef.FullName &&
                modules.TryGetValue(AssemblyIdentityUtil.CanonicalName(scope.Name), out captured) &&
                captured.Assembly != null && captured.Assembly.FullName == scope.FullName;
        }

        // Contextual NAME lookup only. Do not use this for type AQNs, file loads
        // or policy roles. The finite ASCII grammar avoids native NUL truncation,
        // Unicode folding differences and ambiguous/escaped display names.
        private static string NameLoadProvider(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Any(character => character < ' ' || character > '~')) return null;
            string[] parts = value.Split(',');
            string name = parts[0].Trim();
            if (name.Length == 0 || name.Any(character => !IsNameCharacter(character) && character != '/' && character != '\\')) return null;
            var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 1; index < parts.Length; ++index)
            {
                string[] pair = parts[index].Trim().Split('=');
                if (pair.Length != 2 || !fields.Add(pair[0])) return null;
                string field = pair[0], text = pair[1];
                if (field.Equals("Version", StringComparison.OrdinalIgnoreCase))
                {
                    string[] numbers = text.Split('.'); ushort number;
                    if (numbers.Length != 4 || numbers.Any(item => item.Length == 0 || item.Any(character => character < '0' || character > '9') || !ushort.TryParse(item, out number))) return null;
                }
                else if (field.Equals("Culture", StringComparison.OrdinalIgnoreCase))
                { if (text.Length == 0 || text.Any(character => !IsNameCharacter(character) || character == '.')) return null; }
                else if (field.Equals("PublicKeyToken", StringComparison.OrdinalIgnoreCase))
                { if (text != "null" && (text.Length != 16 || text.Any(character => !Uri.IsHexDigit(character)))) return null; }
                else return null;
            }
            int slash = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
            if (slash >= 0) name = name.Substring(slash + 1);
            if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
            return name.Length == 0 || name == "." || name == ".." ? null : name.ToLowerInvariant();
        }

        private static bool IsNameCharacter(char value)
        {
            return value >= 'a' && value <= 'z' || value >= 'A' && value <= 'Z' || value >= '0' && value <= '9' || value == '.' || value == '_' || value == '-';
        }

        internal static string AssemblyNameFromQualifiedType(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            int depth = 0;
            for (int index = 0; index < name.Length; ++index)
            {
                if (name[index] == '[') ++depth;
                else if (name[index] == ']') --depth;
                else if (name[index] == ',' && depth == 0) return AssemblySimpleName(name.Substring(index + 1));
            }
            return null;
        }
        private static string AssemblySimpleName(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? null : AssemblyNamePolicy.Canonical(value.Split(',')[0]);
        }
        private static void ResolveType(IReadOnlyDictionary<string, ModuleDefMD> modules, string argument, string requiredAssembly,
            bool allowSimpleName, out string provider, out string typeName)
        {
            provider = null; typeName = null;
            string qualifiedAssembly = AssemblyNameFromQualifiedType(argument);
            if (qualifiedAssembly != null && requiredAssembly != null && !string.Equals(qualifiedAssembly, requiredAssembly, StringComparison.OrdinalIgnoreCase)) return;
            string expectedAssembly = qualifiedAssembly ?? requiredAssembly;
            string expectedType = (qualifiedAssembly == null ? argument : argument.Substring(0, argument.IndexOf(','))).Trim().Replace('+', '/');
            foreach (var pair in modules)
            {
                if (expectedAssembly != null && !string.Equals(expectedAssembly, pair.Key, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (TypeDef type in pair.Value.GetTypes())
                    if (type.FullName == expectedType || (allowSimpleName && type.Name == expectedType))
                    {
                        if (provider != null) { provider = null; typeName = null; return; }
                        provider = pair.Key; typeName = type.FullName;
                    }
            }
        }
    }
}
