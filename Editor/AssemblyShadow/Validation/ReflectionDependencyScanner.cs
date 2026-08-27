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
            IEnumerable<VerifiedReflectionBinding> verifiedBindings)
        {
            ModuleDefMD module;
            if (modules == null || !modules.TryGetValue(assemblyName, out module))
            { definition.unknownReflectionDependencies = true; return; }
            var evidence = new List<ReflectionDependencyEvidence>();
            var acquisitions = new List<ManagedAcquisitionEvidence>();
            var assemblyBindings = verifiedBindings.Where(binding => binding.Assembly == assemblyName).ToArray();
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
                    State[] states = Analyze(method, fixedGuards);
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
                                acquisitionKind != "AppDomain.GetAssemblies", binding));
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
                        State input = states[index];
                        Value argument = Argument(input, called, argumentIndex);
                        if (argument == null || argument.kind != "string" || string.IsNullOrWhiteSpace(argument.text))
                        { unknown.Add(callSite); acquisitions.Add(Acquisition(method, called, callSite, index, kind, true, null)); continue; }
                        string provider = null, typeName = null;
                        if (kind == "Assembly.Load") provider = AssemblySimpleName(argument.text);
                        else
                        {
                            Value receiver = Receiver(input, called);
                            string requiredAssembly = kind == "Assembly.GetType" && receiver != null && receiver.kind == "assembly" ? receiver.assembly : null;
                            if (kind == "Assembly.GetType" && requiredAssembly == null) { unknown.Add(callSite); acquisitions.Add(Acquisition(method, called, callSite, index, kind, true, null)); continue; }
                            ResolveType(modules, argument.text, requiredAssembly, kind == "GetComponent", out provider, out typeName);
                        }
                        if (provider == null || !modules.ContainsKey(provider)) { unknown.Add(callSite); acquisitions.Add(Acquisition(method, called, callSite, index, kind, true, null)); continue; }
                        evidence.Add(new ReflectionDependencyEvidence { callSite = callSite, target = argument.text,
                            provider = provider, typeName = typeName, kind = kind });
                    }
                }
            }
            definition.references = staticReferences.OrderBy(value => value, StringComparer.Ordinal).ToArray();
            definition.reflectionDependencies = evidence.GroupBy(item => item.callSite + "\n" + item.kind + "\n" + item.target + "\n" + item.provider)
                .Select(group => group.First()).ToArray();
            definition.unknownReflectionCallSites = unknown.OrderBy(value => value, StringComparer.Ordinal).ToArray();
            definition.managedAcquisitions = acquisitions.ToArray();
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
            if (owner != "System.Reflection.Assembly") return null;
            if (name == "GetTypes" || name == "GetExportedTypes" || name == "get_DefinedTypes" || name == "get_ExportedTypes") return "Assembly." + name;
            if (name == "Load" && method.MethodSig.Params.Count > 0 && method.MethodSig.Params[0].FullName == "System.Byte[]") return "Assembly.LoadBytes";
            // Other acquisition entrypoints are not string-name dependencies.
            if (name == "LoadFrom" || name == "LoadFile" || name == "UnsafeLoadFrom" || name == "ReflectionOnlyLoad" || name == "ReflectionOnlyLoadFrom") return "Assembly." + name;
            return null;
        }

        private static State[] Analyze(MethodDef method, IDictionary<MethodDef, VerifiedReflectionBinding> fixedGuards)
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
                Execute(instruction, output, fixedGuards);
                FlowControl flow = instruction.OpCode.FlowControl;
                Instruction target = instruction.Operand as Instruction;
                if (target != null && (flow == FlowControl.Branch || flow == FlowControl.Cond_Branch)) offer(indices[target], output);
                IList<Instruction> targets = instruction.Operand as IList<Instruction>;
                if (targets != null) foreach (Instruction branch in targets) offer(indices[branch], output);
                if (flow != FlowControl.Branch && flow != FlowControl.Return && flow != FlowControl.Throw) offer(index + 1, output);
            }
            return inputs;
        }

        private static void Execute(Instruction instruction, State state, IDictionary<MethodDef, VerifiedReflectionBinding> fixedGuards)
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
                if (method is MethodDef && fixedGuards.TryGetValue((MethodDef)method, out bound)) value = Value.Assembly(bound.Providers.Single());
                else if (owner == "System.Reflection.Assembly" && method.Name == "Load" && args.Length >= 1 && args[0] != null && args[0].kind == "string")
                    value = Value.Assembly(AssemblySimpleName(args[0].text));
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
