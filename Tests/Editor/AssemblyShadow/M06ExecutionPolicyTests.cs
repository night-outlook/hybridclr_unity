using System;
using System.IO;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using NUnit.Framework;

namespace HybridCLR.Editor.AssemblyShadow.Tests
{
    public sealed class M06ExecutionPolicyTests
    {
        [Test] public void CandidateBurstBodyAndOrdinaryBurstCandidateCallAreRejectedFromEmittedIl()
        {
            using (var fixture = new Fixture())
            {
                fixture.Attribute(fixture.BusinessRun, "Unity.Burst", "BurstCompileAttribute");
                fixture.AssertCode("BurstCandidateExecution");
            }
            using (var fixture = new Fixture())
            {
                fixture.Attribute(fixture.ConsumerRun, "Unity.Burst", "BurstCompileAttribute");
                fixture.Call(fixture.ConsumerRun, fixture.BusinessMethodReference());
                fixture.AssertCode("BurstCandidateExecution");
            }
        }

        [Test] public void OrdinaryBurstAndPrimitivePInvokeRemainValid()
        {
            using (var fixture = new Fixture())
            {
                fixture.Attribute(fixture.ConsumerRun, "Unity.Burst", "BurstCompileAttribute");
                fixture.PInvoke(fixture.Consumer, fixture.Consumer.CorLibTypes.Int32);
                fixture.AssertValid();
            }
        }

        [Test] public void PInvokeRejectsCandidateReturnParameterAndNestedWrapperShape()
        {
            foreach (int shape in new[] { 0, 1, 2 })
            using (var fixture = new Fixture())
            {
                TypeSig candidate = fixture.BusinessTypeReference().ToTypeSig();
                if (shape == 2)
                {
                    var wrapper = new TypeDefUser("Fixture", "Wrapper", fixture.Consumer.CorLibTypes.Object.TypeDefOrRef) { Attributes = TypeAttributes.Public };
                    wrapper.Fields.Add(new FieldDefUser("Payload", new FieldSig(candidate), FieldAttributes.Public)); fixture.Consumer.Types.Add(wrapper); candidate = wrapper.ToTypeSig();
                }
                MethodDef method = fixture.PInvoke(fixture.Consumer, shape == 0 ? candidate : fixture.Consumer.CorLibTypes.Void);
                if (shape != 0) method.MethodSig.Params.Add(new ByRefSig(candidate));
                fixture.AssertCode("CandidatePInvokeSignature");
            }
        }

        [Test] public void BootstrapGenericTypeAndGenericMethodArgumentsAreRejected()
        {
            using (var fixture = new Fixture(true))
            {
                var list = new ClassSig(new TypeRefUser(fixture.Consumer, "System.Collections.Generic", "List`1", fixture.Consumer.CorLibTypes.AssemblyRef));
                fixture.ConsumerRun.Body.Variables.Add(new Local(new GenericInstSig(list, fixture.BusinessTypeReference().ToTypeSig())));
                fixture.AssertCode("BootstrapCandidateGeneric");
            }
            using (var fixture = new Fixture(true))
            {
                var generic = fixture.Method(fixture.Consumer, fixture.ConsumerHost, "Generic"); generic.GenericParameters.Add(new GenericParamUser(0, GenericParamAttributes.NonVariant, "T"));
                generic.MethodSig.GenParamCount = 1; generic.MethodSig.Generic = true;
                fixture.Call(fixture.ConsumerRun, new MethodSpecUser(generic, new GenericInstMethodSig(fixture.BusinessTypeReference().ToTypeSig())));
                fixture.AssertCode("BootstrapCandidateGeneric");
            }
        }

        [Test] public void BootstrapGenericOverOrdinaryValueIsNotBanned()
        {
            using (var fixture = new Fixture(true))
            {
                var list = new ClassSig(new TypeRefUser(fixture.Consumer, "System.Collections.Generic", "List`1", fixture.Consumer.CorLibTypes.AssemblyRef));
                fixture.ConsumerRun.Body.Variables.Add(new Local(new GenericInstSig(list, fixture.Consumer.CorLibTypes.Int32)));
                fixture.AssertValid();
            }
        }

        [Test] public void CandidateRuntimeInitializeCannotMasqueradeAsOrdinaryModuleInitializer()
        {
            using (var fixture = new Fixture())
            { fixture.Attribute(fixture.BusinessRun, "UnityEngine", "RuntimeInitializeOnLoadMethodAttribute"); fixture.AssertCode("CandidateRuntimeInitialize"); }
            using (var fixture = new Fixture())
            { fixture.Attribute(fixture.ConsumerRun, "UnityEngine", "RuntimeInitializeOnLoadMethodAttribute"); fixture.AssertValid(); }
            using (var fixture = new Fixture())
            {
                fixture.Attribute(fixture.ConsumerRun, "UnityEngine", "RuntimeInitializeOnLoadMethodAttribute");
                fixture.Call(fixture.ConsumerRun, fixture.BusinessMethodReference()); fixture.AssertCode("CandidateRuntimeInitialize");
            }
        }

        [Test] public void CandidateReachableBurstFunctionPointerCompilationIsRejected()
        {
            using (var fixture = new Fixture())
            {
                var compiler = new TypeRefUser(fixture.Business, "Unity.Burst", "BurstCompiler", fixture.Business.CorLibTypes.AssemblyRef);
                var callback = new ClassSig(new TypeRefUser(fixture.Business, "System", "Action", fixture.Business.CorLibTypes.AssemblyRef));
                var compile = new MemberRefUser(fixture.Business, "CompileFunctionPointer", MethodSig.CreateStatic(fixture.Business.CorLibTypes.IntPtr, callback), compiler);
                fixture.Insert(fixture.BusinessRun, Instruction.Create(OpCodes.Ldnull), Instruction.Create(OpCodes.Call, compile), Instruction.Create(OpCodes.Pop));
                fixture.AssertCode("BurstCandidateExecution");
            }
        }

        [Test] public void CandidateRawFunctionAddressAndCalliRejectButOrdinaryCAbiCalliRemainsLegal()
        {
            using (var fixture = new Fixture())
            {
                fixture.Insert(fixture.ConsumerRun, Instruction.Create(OpCodes.Ldftn, fixture.BusinessMethodReference()), Instruction.Create(OpCodes.Conv_I), Instruction.Create(OpCodes.Pop));
                fixture.AssertCode("CandidateNativePointerEscape");
            }
            using (var fixture = new Fixture())
            { fixture.Calli(fixture.BusinessRun); fixture.AssertCode("CandidateIndirectNativeCall"); }
            using (var fixture = new Fixture())
            { fixture.Calli(fixture.ConsumerRun); fixture.AssertValid(); }
        }

        [Test] public void CandidateNativePointerSignaturesAndUnmanagedEntrypointsFailClosed()
        {
            using (var fixture = new Fixture())
            {
                fixture.BusinessHost.Fields.Add(new FieldDefUser("Native", new FieldSig(new FnPtrSig(MethodSig.CreateStatic(fixture.Business.CorLibTypes.Void))), FieldAttributes.Public));
                fixture.AssertCode("CandidateFunctionPointerBoundary");
            }
            using (var fixture = new Fixture())
            { fixture.Attribute(fixture.BusinessRun, "System.Runtime.InteropServices", "UnmanagedCallersOnlyAttribute"); fixture.AssertCode("CandidateUnmanagedEntrypoint"); }
        }

        [Test] public void ReflectedMethodHandlePointersRequireIdentityProofOnlyInCandidateDependentConsumers()
        {
            foreach (bool dependent in new[] { false, true })
            using (var fixture = new Fixture())
            {
                if (dependent) fixture.Call(fixture.Method(fixture.Consumer, fixture.ConsumerHost, "Dependency"), fixture.BusinessMethodReference());
                var handle = new TypeRefUser(fixture.Consumer, "System", "RuntimeMethodHandle", fixture.Consumer.CorLibTypes.AssemblyRef);
                var local = new Local(new ValueTypeSig(handle)); fixture.ConsumerRun.Body.Variables.Add(local);
                fixture.Insert(fixture.ConsumerRun, Instruction.Create(OpCodes.Ldloca, local), Instruction.Create(OpCodes.Call,
                    new MemberRefUser(fixture.Consumer, "GetFunctionPointer", MethodSig.CreateInstance(fixture.Consumer.CorLibTypes.IntPtr), handle)), Instruction.Create(OpCodes.Pop));
                if (dependent) fixture.AssertCode("NativePointerIdentityUnproved"); else fixture.AssertValid();
            }
        }

        [Test] public void ImmediateManagedDelegateAndCachedManagedDelegateRetainMethodIdentity()
        {
            foreach (bool cached in new[] { false, true })
            using (var fixture = new Fixture())
            {
                var callback = fixture.Delegate();
                fixture.Insert(fixture.ConsumerRun, Instruction.Create(OpCodes.Ldnull), Instruction.Create(OpCodes.Ldftn, fixture.BusinessMethodReference()), Instruction.Create(OpCodes.Newobj, fixture.DelegateConstructor(callback)));
                if (cached)
                {
                    var field = new FieldDefUser("Cached", new FieldSig(callback.ToTypeSig()), FieldAttributes.Static | FieldAttributes.Public); fixture.ConsumerHost.Fields.Add(field);
                    fixture.Insert(fixture.ConsumerRun, Instruction.Create(OpCodes.Dup), Instruction.Create(OpCodes.Stsfld, field));
                }
                fixture.Insert(fixture.ConsumerRun, Instruction.Create(OpCodes.Pop)); fixture.AssertValid();
            }
        }

        [Test] public void ReverseWrapperRequiresSelectedMonoPInvokeCallbackAndExactDelegateSignature()
        {
            foreach (int proof in new[] { 0, 1, 2 })
            using (var fixture = new Fixture())
            {
                TypeDef callback = fixture.Delegate();
                if (proof != 0)
                {
                    var attributeType = new TypeRefUser(fixture.Business, "AOT", "MonoPInvokeCallbackAttribute", fixture.Business.CorLibTypes.AssemblyRef);
                    var typeArg = new ClassSig(new TypeRefUser(fixture.Business, "System", "Type", fixture.Business.CorLibTypes.AssemblyRef));
                    var attribute = new CustomAttribute(new MemberRefUser(fixture.Business, ".ctor", MethodSig.CreateInstance(fixture.Business.CorLibTypes.Void, typeArg), attributeType));
                    attribute.ConstructorArguments.Add(new CAArgument(typeArg, new TypeRefUser(fixture.Business, "Fixture", "Callback", new AssemblyRefUser(fixture.Consumer.Assembly)).ToTypeSig()));
                    fixture.BusinessRun.CustomAttributes.Add(attribute);
                    if (proof == 2) callback.Methods.Single(method => method.Name == "Invoke").MethodSig.RetType = fixture.Consumer.CorLibTypes.Int32;
                }
                var marshal = new TypeRefUser(fixture.Consumer, "System.Runtime.InteropServices", "Marshal", fixture.Consumer.CorLibTypes.AssemblyRef);
                var getPointer = new MemberRefUser(fixture.Consumer, "GetFunctionPointerForDelegate", MethodSig.CreateStatic(fixture.Consumer.CorLibTypes.IntPtr,
                    new ClassSig(new TypeRefUser(fixture.Consumer, "System", "Delegate", fixture.Consumer.CorLibTypes.AssemblyRef))), marshal);
                fixture.Insert(fixture.ConsumerRun, Instruction.Create(OpCodes.Ldnull), Instruction.Create(OpCodes.Ldftn, fixture.BusinessMethodReference()), Instruction.Create(OpCodes.Newobj, fixture.DelegateConstructor(callback)), Instruction.Create(OpCodes.Call, getPointer), Instruction.Create(OpCodes.Pop));
                if (proof == 1) fixture.AssertValid(); else fixture.AssertCode("CandidateNativePointerEscape");
            }
        }

        [Test] public void InitializerDirectAndHelperEffectsAreDiagnosedFromActualModuleCctor()
        {
            var effects = new[] {
                new[] { "System.Threading", "ThreadPool", "QueueUserWorkItem", "InitializerThreadScheduling" },
                new[] { "System.Net", "WebRequest", "Create", "InitializerNetworkEffect" },
                new[] { "System.IO", "File", "Delete", "InitializerPersistentWrite" },
                new[] { "UnityEngine.SceneManagement", "SceneManager", "LoadScene", "InitializerSceneStart" },
            };
            foreach (var effect in effects)
            using (var fixture = new Fixture())
            {
                MethodDef initializer = fixture.ModuleInitializer(); fixture.Call(initializer, fixture.BusinessRun);
                var api = new TypeRefUser(fixture.Business, effect[0], effect[1], fixture.Business.CorLibTypes.AssemblyRef);
                bool thread = effect[0] == "System.Threading", network = effect[0] == "System.Net";
                TypeSig parameter = thread ? new ClassSig(new TypeRefUser(fixture.Business, "System.Threading", "WaitCallback", fixture.Business.CorLibTypes.AssemblyRef)) : (TypeSig)fixture.Business.CorLibTypes.String;
                TypeSig returnType = thread ? (TypeSig)fixture.Business.CorLibTypes.Boolean : network ? new ClassSig(api) : (TypeSig)fixture.Business.CorLibTypes.Void;
                fixture.Insert(fixture.BusinessRun, thread ? Instruction.Create(OpCodes.Ldnull) : Instruction.Create(OpCodes.Ldstr, "not-executed"));
                fixture.Call(fixture.BusinessRun, new MemberRefUser(fixture.Business, effect[2], MethodSig.CreateStatic(returnType, parameter), api));
                if (thread || network) fixture.Insert(fixture.BusinessRun, Instruction.Create(OpCodes.Pop));
                fixture.AssertCode(effect[3]);
            }
        }

        [Test] public void BoundedStaticInitializerAndUnrelatedOrdinarySideEffectsRemainLegal()
        {
            using (var fixture = new Fixture())
            {
                MethodDef initializer = fixture.ModuleInitializer();
                var value = new FieldDefUser("Value", new FieldSig(fixture.Business.CorLibTypes.Int32), FieldAttributes.Static); fixture.BusinessHost.Fields.Add(value);
                fixture.Insert(initializer, Instruction.Create(OpCodes.Ldc_I4_1), Instruction.Create(OpCodes.Stsfld, value));
                var api = new TypeRefUser(fixture.Consumer, "System.IO", "File", fixture.Consumer.CorLibTypes.AssemblyRef);
                fixture.Insert(fixture.ConsumerRun, Instruction.Create(OpCodes.Ldstr, "not-executed"), Instruction.Create(OpCodes.Call,
                    new MemberRefUser(fixture.Consumer, "Delete", MethodSig.CreateStatic(fixture.Consumer.CorLibTypes.Void, fixture.Consumer.CorLibTypes.String), api)));
                fixture.AssertValid();
            }
        }

        [Test] public void ImplicitTypeInitializerEffectsAndDelegateInvocationCannotHideFromModuleInitializer()
        {
            using (var fixture = new Fixture())
            {
                MethodDef initializer = fixture.ModuleInitializer();
                var value = new FieldDefUser("Value", new FieldSig(fixture.Business.CorLibTypes.Int32), FieldAttributes.Static); fixture.BusinessHost.Fields.Add(value);
                fixture.Insert(initializer, Instruction.Create(OpCodes.Ldsfld, value), Instruction.Create(OpCodes.Pop));
                var cctor = fixture.Method(fixture.Business, fixture.BusinessHost, ".cctor"); cctor.Attributes = MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName;
                var file = new TypeRefUser(fixture.Business, "System.IO", "File", fixture.Business.CorLibTypes.AssemblyRef);
                fixture.Insert(cctor, Instruction.Create(OpCodes.Ldstr, "not-executed"), Instruction.Create(OpCodes.Call, new MemberRefUser(fixture.Business, "Delete", MethodSig.CreateStatic(fixture.Business.CorLibTypes.Void, fixture.Business.CorLibTypes.String), file)));
                fixture.AssertCode("InitializerPersistentWrite");
            }
            using (var fixture = new Fixture())
            {
                MethodDef initializer = fixture.ModuleInitializer();
                var action = new TypeRefUser(fixture.Business, "System", "Action", fixture.Business.CorLibTypes.AssemblyRef);
                fixture.Insert(initializer, Instruction.Create(OpCodes.Ldnull), Instruction.Create(OpCodes.Callvirt, new MemberRefUser(fixture.Business, "Invoke", MethodSig.CreateInstance(fixture.Business.CorLibTypes.Void), action)));
                fixture.AssertCode("InitializerIndirectExecution");
            }
        }

        [Test] public void ExistingCompiledValidationAutomaticallyIncludesExecutionBoundaryChecks()
        {
            using (var fixture = new Fixture())
            {
                fixture.Attribute(fixture.BusinessRun, "UnityEngine", "RuntimeInitializeOnLoadMethodAttribute");
                using (var set = fixture.Load())
                    StringAssert.Contains("CandidateRuntimeInitialize", ShadowAssemblyPolicyValidator.ValidateCompiled(set, fixture.Policy, DateTime.UtcNow).ToString());
            }
        }

        private sealed class Fixture : IDisposable
        {
            internal readonly ModuleDefUser Business, Consumer;
            internal readonly TypeDef BusinessHost, ConsumerHost;
            internal readonly MethodDef BusinessRun, ConsumerRun;
            internal readonly ShadowPolicyConfiguration Policy;
            private readonly string root = Path.Combine(Path.GetTempPath(), "m06-policy-il-" + Guid.NewGuid().ToString("N"));
            internal Fixture(bool bootstrap = false)
            {
                Directory.CreateDirectory(root); Business = Module("Business"); Consumer = Module("Consumer");
                BusinessHost = Host(Business, "Payload"); ConsumerHost = Host(Consumer, "Host"); BusinessRun = Method(Business, BusinessHost, "Run"); ConsumerRun = Method(Consumer, ConsumerHost, "Run");
                Policy = new ShadowPolicyConfiguration { assemblies = new[] {
                    new AssemblyCapability { name = "Business", classification = AssemblyClassification.Runtime, isShadowCapable = true },
                    new AssemblyCapability { name = "Consumer", classification = AssemblyClassification.Runtime, isBootstrap = bootstrap },
                } };
            }
            private static ModuleDefUser Module(string name)
            { var module = new ModuleDefUser(name + ".dll") { Kind = ModuleKind.Dll }; new AssemblyDefUser(name, new Version(1, 0, 0, 0)).Modules.Add(module); return module; }
            private static TypeDef Host(ModuleDef module, string name)
            { var type = new TypeDefUser("Fixture", name, module.CorLibTypes.Object.TypeDefOrRef) { Attributes = TypeAttributes.Public }; module.Types.Add(type); return type; }
            internal MethodDef Method(ModuleDef module, TypeDef type, string name)
            { var method = new MethodDefUser(name, MethodSig.CreateStatic(module.CorLibTypes.Void), MethodImplAttributes.IL, MethodAttributes.Public | MethodAttributes.Static) { Body = new CilBody() }; method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); type.Methods.Add(method); return method; }
            internal ITypeDefOrRef BusinessTypeReference() { return new TypeRefUser(Consumer, "Fixture", "Payload", new AssemblyRefUser(Business.Assembly)); }
            internal IMethod BusinessMethodReference() { return new MemberRefUser(Consumer, "Run", MethodSig.CreateStatic(Consumer.CorLibTypes.Void), BusinessTypeReference()); }
            internal void Attribute(MethodDef method, string ns, string name)
            { var type = new TypeRefUser(method.Module, ns, name, method.Module.CorLibTypes.AssemblyRef); method.CustomAttributes.Add(new CustomAttribute(new MemberRefUser(method.Module, ".ctor", MethodSig.CreateInstance(method.Module.CorLibTypes.Void), type))); }
            internal void Insert(MethodDef method, params Instruction[] instructions)
            { foreach (Instruction instruction in instructions) method.Body.Instructions.Insert(method.Body.Instructions.Count - 1, instruction); }
            internal void Call(MethodDef method, IMethod target) { Insert(method, Instruction.Create(OpCodes.Call, target)); }
            internal MethodDef PInvoke(ModuleDef module, TypeSig result)
            {
                var method = new MethodDefUser("Native", MethodSig.CreateStatic(result), MethodImplAttributes.PreserveSig, MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.PinvokeImpl);
                method.ImplMap = new ImplMapUser(new ModuleRefUser(module, "OrdinaryNative"), "Native", PInvokeAttributes.CallConvCdecl); module.GlobalType.Methods.Add(method); return method;
            }
            internal void Calli(MethodDef method)
            { Insert(method, Instruction.Create(OpCodes.Ldc_I4_0), Instruction.Create(OpCodes.Conv_I), new Instruction(OpCodes.Calli, MethodSig.CreateStatic(method.Module.CorLibTypes.Void))); }
            internal MethodDef ModuleInitializer()
            { var method = Method(Business, Business.GlobalType, ".cctor"); method.Attributes = MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName; return method; }
            internal TypeDef Delegate()
            {
                var type = new TypeDefUser("Fixture", "Callback", new TypeRefUser(Consumer, "System", "MulticastDelegate", Consumer.CorLibTypes.AssemblyRef)) { Attributes = TypeAttributes.Public | TypeAttributes.Sealed };
                type.Methods.Add(new MethodDefUser(".ctor", MethodSig.CreateInstance(Consumer.CorLibTypes.Void, Consumer.CorLibTypes.Object, Consumer.CorLibTypes.IntPtr), MethodImplAttributes.Runtime, MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName));
                type.Methods.Add(new MethodDefUser("Invoke", MethodSig.CreateInstance(Consumer.CorLibTypes.Void), MethodImplAttributes.Runtime, MethodAttributes.Public | MethodAttributes.Virtual)); Consumer.Types.Add(type); return type;
            }
            internal IMethod DelegateConstructor(TypeDef type) { return type.Methods.Single(method => method.IsInstanceConstructor); }
            internal CompiledAssemblySet Load()
            {
                Business.Write(Path.Combine(root, "Business.dll")); Consumer.Write(Path.Combine(root, "Consumer.dll"));
                // Framework resolution is intentionally outside these fixture checks;
                // the actual loader still reloads emitted DLL identity, IL and hashes.
                var set = DnlibAssemblyLoader.Load(root, new string[0], Policy.assemblies, false);
                Assert.AreEqual(ShadowHash.File(Path.Combine(root, "Business.dll")), set.Get("Business").sha256);
                Assert.IsInstanceOf<ModuleDefMD>(set.GetModule("Consumer")); return set;
            }
            internal void AssertCode(string code)
            { using (var set = Load()) { var result = ShadowExecutionPolicy.ValidateCompiled(set, Policy); Assert.IsFalse(result.IsValid); StringAssert.Contains(code, result.ToString()); Assert.IsFalse(result.Diagnostics.Any(item => item.code == "ExecutionPolicyAnalysisFailed"), result.ToString()); } }
            internal void AssertValid()
            { using (var set = Load()) { var result = ShadowExecutionPolicy.ValidateCompiled(set, Policy); Assert.IsTrue(result.IsValid, result.ToString()); } }
            public void Dispose() { Business.Dispose(); Consumer.Dispose(); Directory.Delete(root, true); }
        }
    }
}
