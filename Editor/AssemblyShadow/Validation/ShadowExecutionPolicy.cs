using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace HybridCLR.Editor.AssemblyShadow
{
    /// <summary>
    /// Execution-boundary checks over the supplied, byte-loaded Player modules.
    /// This is a bounded static check, not a sandbox or whole-program effects proof.
    /// It never resolves through the Editor AppDomain, GAC or an ambient search path.
    /// </summary>
    public static partial class ShadowExecutionPolicy
    {
        public static ShadowPolicyValidationResult ValidateCompiled(CompiledAssemblySet set, ShadowPolicyConfiguration policy)
        {
            var result = new ShadowPolicyValidationResult();
            if (set == null) { result.Error("ExecutionPolicyMissingCompiledSet", "Actual compiled modules are required."); return result; }
            new Scanner(set, policy, result).Run();
            return result;
        }

        private sealed class Scanner
        {
            private const int MaxReachableMethods = 8192;
            private const int MaxReachableInstructions = 250000;
            private readonly CompiledAssemblySet set;
            private readonly ShadowPolicyValidationResult result;
            private readonly HashSet<string> candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> bootstraps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> runtime = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> candidateDependent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            private readonly HashSet<string> emitted = new HashSet<string>(StringComparer.Ordinal);
            private readonly Dictionary<IMethod, MethodDef> methods = new Dictionary<IMethod, MethodDef>();

            internal Scanner(CompiledAssemblySet set, ShadowPolicyConfiguration policy, ShadowPolicyValidationResult result)
            {
                this.set = set; this.result = result;
                foreach (var descriptor in set.Assemblies.Values)
                {
                    if (descriptor.classification != AssemblyClassification.Runtime && descriptor.classification != AssemblyClassification.NormalHotUpdate) continue;
                    runtime.Add(descriptor.name);
                    if (descriptor.isShadowCapable) candidates.Add(descriptor.name);
                    if (descriptor.isBootstrap) bootstraps.Add(descriptor.name);
                }
                foreach (var capability in (policy == null ? null : policy.assemblies) ?? new AssemblyCapability[0])
                {
                    if (capability == null) continue;
                    if (capability.isShadowCapable) candidates.Add(AssemblyNamePolicy.Canonical(capability.name));
                    if (capability.isBootstrap) bootstraps.Add(AssemblyNamePolicy.Canonical(capability.name));
                }
                candidateDependent.UnionWith(candidates);
                var declared = new List<KeyValuePair<string, string>>();
                if (policy != null && policy.dependencies != null)
                {
                    declared.AddRange((policy.dependencies.runtimeDependencies ?? new DeclaredRuntimeDependency[0]).Where(edge => edge != null)
                        .Select(edge => new KeyValuePair<string, string>(AssemblyNamePolicy.Canonical(edge.consumer), AssemblyNamePolicy.Canonical(edge.provider))));
                    declared.AddRange((policy.dependencies.bootstrapEntrypoints ?? new BootstrapEntrypointDeclaration[0]).Where(edge => edge != null)
                        .Select(edge => new KeyValuePair<string, string>(AssemblyNamePolicy.Canonical(edge.consumer), AssemblyNamePolicy.Canonical(edge.provider))));
                }
                bool changed;
                do
                {
                    changed = false;
                    foreach (var module in set.Modules.Values)
                        if (module.GetAssemblyRefs().Any(reference => candidateDependent.Contains(reference.Name.String)) && candidateDependent.Add(module.Assembly.Name.String)) changed = true;
                    foreach (var edge in declared)
                        if (candidateDependent.Contains(edge.Value) && candidateDependent.Add(edge.Key)) changed = true;
                } while (changed);
            }

            internal void Run()
            {
                if (candidates.Count == 0) return;
                foreach (var pair in set.Modules.OrderBy(item => item.Key, StringComparer.Ordinal))
                {
                    if (!runtime.Contains(pair.Key)) continue;
                    try { ScanModule(pair.Value); }
                    catch (Exception error) { result.Error("ExecutionPolicyAnalysisFailed", pair.Value.Assembly.FullName + ": " + error.Message); }
                }
            }

            private void ScanModule(ModuleDefMD module)
            {
                bool candidate = candidates.Contains(module.Assembly.Name.String), bootstrap = bootstraps.Contains(module.Assembly.Name.String);
                foreach (TypeDef type in module.GetTypes())
                {
                    bool burstType = HasAttribute(type.CustomAttributes, "Unity.Burst.BurstCompileAttribute");
                    if (burstType && (candidate || TypeSignatures(type).Any(ContainsCandidateShape)))
                        Error("BurstCandidateExecution", type, null, -1, "Burst type contains a concrete candidate definition or signature.");
                    if (bootstrap && TypeSignatures(type).Any(ContainsCandidateGeneric))
                        Error("BootstrapCandidateGeneric", type, null, -1, "Fixed Bootstrap type storage/base/interface closes a generic over a candidate.");
                    foreach (var field in type.Fields)
                        if (HasFunctionPointer(field.FieldType) && (candidate || ContainsCandidate(field.FieldType)))
                            Error("CandidateFunctionPointerBoundary", type, null, -1, "Native function-pointer field has no MethodInfo identity: " + field.FullName);
                    foreach (MethodDef method in type.Methods)
                    {
                        if (candidate && HasAttribute(method.CustomAttributes, "UnityEngine.RuntimeInitializeOnLoadMethodAttribute"))
                            Error("CandidateRuntimeInitialize", type, method, -1, "Unity startup callback can execute before shadow publication.");
                        else if (HasAttribute(method.CustomAttributes, "UnityEngine.RuntimeInitializeOnLoadMethodAttribute"))
                            ScanReachable(method, (body, instruction, index) =>
                            {
                                if (candidates.Contains(body.Module.Assembly.Name.String) || OperandHasCandidate(instruction.Operand))
                                    Error("CandidateRuntimeInitialize", type, method, index, "Unity startup callback reaches candidate code before Bootstrap via " + body.FullName + ".");
                            }, false);
                        if (candidate && HasAttribute(method.CustomAttributes, "System.Runtime.InteropServices.UnmanagedCallersOnlyAttribute"))
                            Error("CandidateUnmanagedEntrypoint", type, method, -1, "UnmanagedCallersOnly does not use a verified MethodInfo-bearing reverse wrapper.");
                        if (method.IsPinvokeImpl && Signatures(method.MethodSig).Any(signature => ContainsCandidateShape(signature, new HashSet<TypeDef>())))
                            Error("CandidatePInvokeSignature", type, method, -1, "Candidate-containing P/Invoke has no supported frozen ABI contract.");
                        if (Signatures(method.MethodSig).Any(HasFunctionPointer) && (candidate || Signatures(method.MethodSig).Any(ContainsCandidate)))
                            Error("CandidateFunctionPointerBoundary", type, method, -1, "Native function-pointer signature cannot preserve candidate method identity.");
                        if (bootstrap && (MethodSignatures(method).Any(ContainsCandidateGeneric) || (method.HasBody && method.Body.Instructions
                            .Select(instruction => instruction.Operand).OfType<MethodSpec>().Any(spec => spec.GenericInstMethodSig.GenericArguments.Any(ContainsCandidate)))))
                            Error("BootstrapCandidateGeneric", type, method, -1, "Fixed Bootstrap closes a generic over a concrete candidate type.");
                        if (HasAttribute(method.CustomAttributes, "Unity.Burst.BurstCompileAttribute") && (candidate || MethodSignatures(method).Any(ContainsCandidateShape)))
                            Error("BurstCandidateExecution", type, method, -1, "Burst method is a candidate definition or has a concrete candidate signature.");
                        if (burstType || HasAttribute(method.CustomAttributes, "Unity.Burst.BurstCompileAttribute"))
                            ScanReachable(method, (body, instruction, index) =>
                            {
                                if (candidates.Contains(body.Module.Assembly.Name.String) || MethodSignatures(body).Any(ContainsCandidateShape) || OperandHasCandidate(instruction.Operand))
                                    Error("BurstCandidateExecution", type, method, index, "Burst reachable code uses candidate metadata at " + body.FullName + ".");
                            }, false);
                        if (candidate || MethodSignatures(method).Any(ContainsCandidate) ||
                            (method.HasBody && method.Body.Instructions.Any(instruction => OperandHasCandidate(instruction.Operand))))
                            ScanReachable(method, (body, instruction, index) => CheckNativeEscape(body, instruction, index), false);
                        if (method.HasBody && candidateDependent.Contains(module.Assembly.Name.String))
                            for (int index = 0; index < method.Body.Instructions.Count; ++index) CheckOpaqueNativeEscape(method, method.Body.Instructions[index], index);
                        if (candidate && type.IsGlobalModuleType && method.IsStaticConstructor)
                            ScanReachable(method, (body, instruction, index) => CheckInitializer(method, body, instruction, index), true);
                    }
                }
            }

            private void CheckOpaqueNativeEscape(MethodDef method, Instruction instruction, int index)
            {
                var target = instruction.Operand as IMethod;
                if (target == null || target.DeclaringType == null) return;
                string owner = target.DeclaringType.FullName, name = target.Name.String;
                bool handle = owner == "System.RuntimeMethodHandle" && name == "GetFunctionPointer";
                bool marshal = owner == "System.Runtime.InteropServices.Marshal" && (name == "GetFunctionPointerForDelegate" || name == "GetDelegateForFunctionPointer");
                if (handle || (marshal && !IsVerifiedReverseWrapper(method, index)))
                    Error("NativePointerIdentityUnproved", method.DeclaringType, method, index, "Candidate-dependent assembly erases method identity at " + target.FullName + "; only an exact MethodInfo-bearing reverse-wrapper proof is supported.");
            }

            private void CheckNativeEscape(MethodDef method, Instruction instruction, int index)
            {
                if (instruction.OpCode.Code == Code.Calli)
                    Error("CandidateIndirectNativeCall", method.DeclaringType, method, index, "Candidate-reachable calli has only a native pointer, not an authorized MethodInfo.");
                var target = instruction.Operand as IMethod;
                if (target != null && target.DeclaringType != null && target.DeclaringType.FullName == "Unity.Burst.BurstCompiler" && target.Name == "CompileFunctionPointer")
                    Error("BurstCandidateExecution", method.DeclaringType, method, index, "Candidate-reachable Burst function-pointer compilation escapes the managed method boundary.");
                if ((instruction.OpCode.Code == Code.Ldftn || instruction.OpCode.Code == Code.Ldvirtftn) && target != null &&
                    (IsCandidate(target.DeclaringType) || OperandHasCandidate(target)) && !IsManagedDelegateConsumption(method, index, target))
                    Error("CandidateNativePointerEscape", method.DeclaringType, method, index, "Candidate method address is not consumed directly by a resolved managed delegate constructor.");
                if (target != null && target.DeclaringType != null && target.DeclaringType.FullName == "System.Runtime.InteropServices.Marshal" &&
                    (target.Name == "GetFunctionPointerForDelegate" || target.Name == "GetDelegateForFunctionPointer") && !IsVerifiedReverseWrapper(method, index))
                    Error("CandidateNativePointerEscape", method.DeclaringType, method, index, "Native delegate conversion lacks a statically selected, signature-matched MonoPInvokeCallback wrapper.");
            }

            private bool IsManagedDelegateConsumption(MethodDef body, int index, IMethod target)
            {
                var instructions = body.Body.Instructions;
                if (index + 1 >= instructions.Count || instructions[index + 1].OpCode.Code != Code.Newobj) return false;
                var constructor = instructions[index + 1].Operand as IMethod;
                TypeDef delegateType = constructor == null ? null : set.ResolveType(constructor.DeclaringType);
                if (constructor == null || constructor.Name != ".ctor" || !IsDelegate(delegateType)) return false;
                // Standard managed delegate construction retains the target MethodInfo;
                // signature validity is already required by the managed IL verifier.
                return target.MethodSig != null;
            }

            private bool IsVerifiedReverseWrapper(MethodDef body, int index)
            {
                // Deliberately narrow: no local/field/argument provenance is invented.
                // Managed delegate caching stays legal; publishing a cached pointer
                // needs separate byte-bound provenance and is not admitted here.
                if (index < 2 || body.Body.Instructions[index - 2].OpCode.Code != Code.Ldftn || body.Body.Instructions[index - 1].OpCode.Code != Code.Newobj) return false;
                var selected = body.Body.Instructions[index - 2].Operand as IMethod;
                MethodDef method = Resolve(selected);
                var constructor = body.Body.Instructions[index - 1].Operand as IMethod;
                TypeDef delegateType = constructor == null ? null : set.ResolveType(constructor.DeclaringType);
                if (method == null || !method.IsStatic || method.HasGenericParameters || !IsDelegate(delegateType) || delegateType.HasGenericParameters) return false;
                MethodDef invoke = delegateType.Methods.SingleOrDefault(item => item.Name == "Invoke");
                if (invoke == null || !SameCallbackSignature(method.MethodSig, invoke.MethodSig)) return false;
                var attributes = method.CustomAttributes.Where(attribute => attribute.TypeFullName == "AOT.MonoPInvokeCallbackAttribute").ToArray();
                if (attributes.Length != 1 || attributes[0].ConstructorArguments.Count != 1) return false;
                var declared = attributes[0].ConstructorArguments[0].Value as TypeSig;
                return declared != null && set.ResolveType(declared.ToTypeDefOrRef()) == delegateType;
            }

            private static bool SameCallbackSignature(MethodSig target, MethodSig callback)
            {
                var comparer = new SigComparer();
                return target != null && callback != null && target.GenParamCount == 0 && callback.GenParamCount == 0 &&
                    comparer.Equals(target.RetType, callback.RetType) && target.Params.Count == callback.Params.Count &&
                    target.Params.Zip(callback.Params, (one, two) => comparer.Equals(one, two)).All(equal => equal);
            }

            private void CheckInitializer(MethodDef root, MethodDef body, Instruction instruction, int index)
            {
                var target = instruction.Operand as IMethod;
                if (instruction.OpCode.Code == Code.Calli)
                    Error("InitializerIndirectExecution", root.DeclaringType, root, index, "Initializer reaches calli at " + body.FullName + ".");
                if (target == null || target.DeclaringType == null) return;
                string owner = target.DeclaringType.FullName, name = target.Name.String, risk = null;
                if ((owner == "System.Threading.Thread" && (name == ".ctor" || name == "Start")) || owner == "System.Threading.ThreadPool" ||
                    ((owner == "System.Threading.Tasks.Task" || owner.StartsWith("System.Threading.Tasks.TaskFactory", StringComparison.Ordinal)) && (name == "Run" || name == "Start" || name == "StartNew" || name == "Delay")) ||
                    owner == "System.Threading.Timer" || owner == "System.Timers.Timer") risk = "InitializerThreadScheduling";
                else if (owner.StartsWith("System.Net.", StringComparison.Ordinal) || owner.StartsWith("UnityEngine.Networking.UnityWebRequest", StringComparison.Ordinal)) risk = "InitializerNetworkEffect";
                else if ((owner == "System.IO.File" && (name.StartsWith("Write", StringComparison.Ordinal) || name.StartsWith("Append", StringComparison.Ordinal) || name.StartsWith("Set", StringComparison.Ordinal) || new[] { "Create", "CreateText", "Copy", "Move", "Delete", "Replace", "Open", "OpenWrite" }.Contains(name))) ||
                    (owner == "System.IO.FileInfo" && (name.StartsWith("set_", StringComparison.Ordinal) || new[] { "Create", "CreateText", "CopyTo", "MoveTo", "Delete", "Replace", "Open", "OpenWrite", "AppendText" }.Contains(name))) ||
                    ((owner == "System.IO.Directory" || owner == "System.IO.DirectoryInfo") && (name.StartsWith("Create", StringComparison.Ordinal) || name.StartsWith("set_", StringComparison.Ordinal) || new[] { "Delete", "Move", "MoveTo" }.Contains(name))) ||
                    ((owner == "System.IO.FileStream" || owner == "System.IO.StreamWriter") && name == ".ctor") ||
                    ((owner == "System.IO.FileStream" || owner == "System.IO.StreamWriter") && (name.StartsWith("Write", StringComparison.Ordinal) || name == "Flush" || name == "SetLength")) ||
                    (owner == "UnityEngine.PlayerPrefs" && (name.StartsWith("Set", StringComparison.Ordinal) || name.StartsWith("Delete", StringComparison.Ordinal) || name == "Save"))) risk = "InitializerPersistentWrite";
                else if ((owner == "UnityEngine.SceneManagement.SceneManager" && (name.StartsWith("LoadScene", StringComparison.Ordinal) || name.StartsWith("UnloadScene", StringComparison.Ordinal) || name == "SetActiveScene" || name == "CreateScene")) ||
                    (owner == "UnityEngine.Application" && name.StartsWith("LoadLevel", StringComparison.Ordinal)) ||
                    (owner == "UnityEngine.Object" && name == "Instantiate") || (owner == "UnityEngine.GameObject" && name == "AddComponent")) risk = "InitializerSceneStart";
                else if (owner == "System.Diagnostics.Process" && name == "Start") risk = "InitializerProcessStart";
                MethodDef resolved = Resolve(target);
                if (resolved != null && resolved.IsPinvokeImpl) risk = "InitializerUnmanagedEffect";
                if (risk != null) Error(risk, root.DeclaringType, root, index, "Initializer reaches " + target.FullName + " via " + body.FullName + ".");
                if (instruction.OpCode.Code == Code.Callvirt && resolved != null && resolved.IsVirtual && !resolved.IsFinal && !resolved.DeclaringType.IsSealed)
                    Error("InitializerIndirectExecution", root.DeclaringType, root, index, "Virtual dispatch is not a bounded direct initializer call: " + target.FullName);
                if ((resolved != null && IsDelegate(resolved.DeclaringType) && (name == "Invoke" || name == "BeginInvoke")) ||
                    (resolved == null && instruction.OpCode.Code == Code.Callvirt && (name == "Invoke" || name == "BeginInvoke")) ||
                    (owner == "System.Delegate" && name == "DynamicInvoke") ||
                    ((owner == "System.Reflection.MethodBase" || owner == "System.Reflection.MethodInfo" || owner == "System.Reflection.ConstructorInfo") && name == "Invoke"))
                    Error("InitializerIndirectExecution", root.DeclaringType, root, index, "Initializer callback/reflection target is outside bounded direct-call analysis: " + target.FullName);
            }

            private void ScanReachable(MethodDef root, Action<MethodDef, Instruction, int> inspect, bool strict)
            {
                var visited = new HashSet<MethodDef>(); var pending = new Stack<MethodDef>(); pending.Push(root); int instructions = 0;
                while (pending.Count > 0)
                {
                    MethodDef method = pending.Pop();
                    if (!visited.Add(method) || !method.HasBody) continue;
                    if (visited.Count > MaxReachableMethods || (instructions += method.Body.Instructions.Count) > MaxReachableInstructions)
                    { Error("ExecutionPolicyAnalysisLimit", root.DeclaringType, root, -1, "Direct reachable-code analysis exceeded its explicit bound."); return; }
                    for (int index = 0; index < method.Body.Instructions.Count; ++index)
                    {
                        Instruction instruction = method.Body.Instructions[index]; inspect(method, instruction, index);
                        var field = instruction.Operand as IField;
                        if (strict && field != null && field.FieldSig != null && (instruction.OpCode.Code == Code.Ldsfld || instruction.OpCode.Code == Code.Ldsflda || instruction.OpCode.Code == Code.Stsfld))
                            PushTypeInitializer(set.ResolveType(field.DeclaringType), pending);
                        var reference = instruction.Operand as IMethod;
                        if (reference == null || (instruction.OpCode.Code != Code.Call && instruction.OpCode.Code != Code.Callvirt && instruction.OpCode.Code != Code.Newobj)) continue;
                        MethodDef resolved = Resolve(reference);
                        if (resolved != null && runtime.Contains(resolved.Module.Assembly.Name.String))
                        {
                            pending.Push(resolved);
                            if (strict && (resolved.IsStatic || instruction.OpCode.Code == Code.Newobj)) PushTypeInitializer(resolved.DeclaringType, pending);
                        }
                        else if (strict && reference.DeclaringType != null && reference.DeclaringType.DefinitionAssembly != null && runtime.Contains(reference.DeclaringType.DefinitionAssembly.Name.String))
                            Error("InitializerUnresolvedExecution", root.DeclaringType, root, index, "Cannot resolve actual runtime call " + reference.FullName + ".");
                    }
                }
            }

            private void PushTypeInitializer(TypeDef type, Stack<MethodDef> pending)
            {
                if (type != null && runtime.Contains(type.Module.Assembly.Name.String))
                    foreach (MethodDef initializer in type.Methods.Where(method => method.IsStaticConstructor)) pending.Push(initializer);
            }

            private MethodDef Resolve(IMethod reference)
            {
                if (reference == null) return null;
                var spec = reference as MethodSpec; if (spec != null) reference = spec.Method;
                MethodDef cached; if (methods.TryGetValue(reference, out cached)) return cached;
                TypeDef owner = set.ResolveType(reference.DeclaringType);
                var matches = owner == null ? new MethodDef[0] : owner.Methods.Where(method => method.Name == reference.Name && new SigComparer().Equals(method.MethodSig, reference.MethodSig)).ToArray();
                cached = matches.Length == 1 ? matches[0] : null; methods[reference] = cached; return cached;
            }

            private bool IsCandidate(ITypeDefOrRef type)
            {
                if (type == null) return false;
                if (type.DefinitionAssembly != null && candidates.Contains(type.DefinitionAssembly.Name.String)) return true;
                TypeDef resolved = set.ResolveType(type);
                return resolved != null && candidates.Contains(resolved.Module.Assembly.Name.String);
            }

            private bool ContainsCandidate(TypeSig signature)
            {
                if (signature == null) return false;
                var named = signature as TypeDefOrRefSig;
                if (named != null && IsCandidate(named.TypeDefOrRef)) return true;
                var generic = signature as GenericInstSig;
                if (generic != null && (ContainsCandidate(generic.GenericType) || generic.GenericArguments.Any(ContainsCandidate))) return true;
                var pointer = signature as FnPtrSig;
                if (pointer != null && Signatures(pointer.Signature as MethodSig).Any(ContainsCandidate)) return true;
                var modifier = signature as ModifierSig;
                return (modifier != null && IsCandidate(modifier.Modifier)) || ContainsCandidate(signature.Next);
            }

            private bool ContainsCandidateShape(TypeSig signature, HashSet<TypeDef> seen)
            {
                if (ContainsCandidate(signature)) return true;
                var generic = signature as GenericInstSig;
                if (generic != null && generic.GenericArguments.Any(argument => ContainsCandidateShape(argument, seen))) return true;
                if (signature != null && signature.Next != null && ContainsCandidateShape(signature.Next, seen)) return true;
                TypeDef type = signature == null ? null : set.ResolveType(signature.ToTypeDefOrRef());
                if (type == null || !runtime.Contains(type.Module.Assembly.Name.String) || !seen.Add(type)) return false;
                return type.Fields.Where(field => !field.IsStatic).Any(field => ContainsCandidateShape(field.FieldType, seen)) ||
                    (IsDelegate(type) && type.Methods.Where(method => method.Name == "Invoke").Any(method => Signatures(method.MethodSig).Any(item => ContainsCandidateShape(item, seen))));
            }
            private bool ContainsCandidateShape(TypeSig signature) { return ContainsCandidateShape(signature, new HashSet<TypeDef>()); }

            private bool ContainsCandidateGeneric(TypeSig signature)
            {
                if (signature == null) return false;
                var generic = signature as GenericInstSig;
                return (generic != null && generic.GenericArguments.Any(ContainsCandidate)) || ContainsCandidateGeneric(signature.Next);
            }

            private bool OperandHasCandidate(object operand)
            {
                var type = operand as ITypeDefOrRef; if (type != null) return ContainsCandidate(type.ToTypeSig());
                var field = operand as IField; if (field != null && field.FieldSig != null) return IsCandidate(field.DeclaringType) || ContainsCandidate(field.FieldSig.Type);
                var method = operand as IMethod;
                if (method != null) return ContainsCandidate(method.DeclaringType == null ? null : method.DeclaringType.ToTypeSig()) || Signatures(method.MethodSig).Any(ContainsCandidate) ||
                    (method is MethodSpec && ((MethodSpec)method).GenericInstMethodSig.GenericArguments.Any(ContainsCandidate));
                var call = operand as MethodSig; return call != null && Signatures(call).Any(ContainsCandidate);
            }

            private static bool HasFunctionPointer(TypeSig signature)
            {
                if (signature == null) return false;
                var generic = signature as GenericInstSig;
                return signature is FnPtrSig || (generic != null && generic.GenericArguments.Any(HasFunctionPointer)) || HasFunctionPointer(signature.Next);
            }

            private static bool IsDelegate(TypeDef type)
            { return type != null && type.BaseType != null && (type.BaseType.FullName == "System.MulticastDelegate" || type.BaseType.FullName == "System.Delegate"); }
            private static bool HasAttribute(CustomAttributeCollection attributes, string name) { return attributes.Any(attribute => attribute.TypeFullName == name); }
            private static IEnumerable<TypeSig> Signatures(MethodSig signature)
            { return signature == null ? Enumerable.Empty<TypeSig>() : new[] { signature.RetType }.Concat(signature.Params).Concat(signature.ParamsAfterSentinel ?? new List<TypeSig>()); }
            private static IEnumerable<TypeSig> TypeSignatures(TypeDef type)
            { return new[] { type.BaseType == null ? null : type.BaseType.ToTypeSig() }.Concat(type.Interfaces.Select(item => item.Interface.ToTypeSig())).Concat(type.Fields.Select(field => field.FieldType)); }
            private static IEnumerable<TypeSig> MethodSignatures(MethodDef method)
            {
                var signatures = Signatures(method.MethodSig);
                if (!method.HasBody) return signatures;
                return signatures.Concat(method.Body.Variables.Select(variable => variable.Type)).Concat(method.Body.Instructions.SelectMany(instruction =>
                {
                    var spec = instruction.Operand as MethodSpec;
                    if (spec != null) return spec.GenericInstMethodSig.GenericArguments.Concat(new[] { spec.DeclaringType.ToTypeSig() });
                    var type = instruction.Operand as ITypeDefOrRef;
                    if (type != null) return new[] { type.ToTypeSig() };
                    var field = instruction.Operand as IField;
                    if (field != null && field.FieldSig != null) return new[] { field.FieldSig.Type, field.DeclaringType.ToTypeSig() };
                    var target = instruction.Operand as IMethod;
                    return target == null ? Enumerable.Empty<TypeSig>() : Signatures(target.MethodSig).Concat(new[] { target.DeclaringType.ToTypeSig() });
                }));
            }

            private void Error(string code, TypeDef type, MethodDef method, int index, string detail)
            {
                string owner = method == null ? type.FullName : method.FullName;
                string message = type.Module.Assembly.FullName + " | " + owner + " | IL index " + index + " | " + detail;
                if (emitted.Add(code + "\n" + message)) result.Error(code, message);
            }
        }
    }
}
