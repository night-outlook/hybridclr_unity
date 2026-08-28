using System;
using System.IO;
using System.Linq;
using System.Reflection;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using NUnit.Framework;

namespace HybridCLR.Editor.AssemblyShadow.Tests
{
    public sealed class M04NameAcquisitionTests
    {
        [Test] public void RealLoaderProvesFreshLiteralConstructorAndDownstreamType()
        {
            foreach (bool nops in new[] { false, true })
            using (var fixture = new Fixture("Image", true, true, (module, method) =>
            {
                if (nops) { method.Body.Instructions.Insert(1, Instruction.Create(OpCodes.Nop)); method.Body.Instructions.Insert(3, Instruction.Create(OpCodes.Nop)); }
            }))
            {
                CollectionAssert.Contains(fixture.Set.Assemblies.Keys, "consumer");
                var scan = fixture.Scan(); Assert.IsEmpty(scan.unknownReflectionCallSites);
                CollectionAssert.AreEquivalent(new[] { "Assembly.Load", "Assembly.GetType" }, scan.reflectionDependencies.Select(value => value.kind));
                Assert.AreEqual("Fixture.Payload", scan.reflectionDependencies.Single(value => value.kind == "Assembly.GetType").typeName);
                Assert.IsTrue(fixture.Validate().IsValid, fixture.Validate().ToString());
            }
        }

        [Test] public void LiteralPathSuffixCaseAndDisplayIdentityKeepTheExactApprovalTarget()
        {
            foreach (bool named in new[] { false, true })
            foreach (string literal in new[] { "Image", "IMAGE.DLL", "Folder/Image.dll", "Folder\\IMAGE.DlL", "Image, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null" })
            using (var fixture = new Fixture(literal, named))
            {
                var evidence = fixture.Scan().reflectionDependencies.Single();
                Assert.AreEqual("image", evidence.provider); Assert.AreEqual(literal, evidence.target);
                fixture.Set.Get("Consumer").isBootstrap = true; fixture.Set.Get("Image").isShadowCapable = true;
                fixture.Policy.dependencies.runtimeDependencies = new DeclaredRuntimeDependency[0];
                fixture.Policy.dependencies.bootstrapEntrypoints = new[] { new BootstrapEntrypointDeclaration
                { consumer = "Consumer", provider = "Image", typeName = "Fixture.Payload", method = "Fixture.Host::Run", target = literal, reason = "Exact M04 lookup" } };
                Assert.IsTrue(fixture.Validate().IsValid, fixture.Validate().ToString());
                fixture.Policy.dependencies.bootstrapEntrypoints[0].target = literal + " ";
                StringAssert.Contains("BootstrapReflection", fixture.Validate().ToString());
            }
        }

        [Test] public void NameProofRejectsMissingMalformedAndAmbiguousInputs()
        {
            foreach (bool named in new[] { false, true })
            foreach (string literal in new[] { "Missing", "", ".dll", "Folder/", "Image\0/Other", "Folder/Im\tage.dll", "Ímage", "Image, Bogus=1",
                "Image, Version=1.0", "Image, Version=-1.0.0.0", "Image, Version=65536.0.0.0", "Image, PublicKeyToken=bad",
                "Image, Culture=neutral, Culture=neutral", "file://Folder/Image.dll", "\"Image\"", "Image, CodeBase=Image.dll" })
            using (var fixture = new Fixture(literal, named)) AssertDenied(fixture);
        }

        [Test] public void MutableAndEscapedAssemblyNamesAreNeverProven()
        {
            foreach (string mutation in new[] { "local", "dup", "field", "setter", "codebase", "unknown", "concat", "escape" })
            using (var fixture = new Fixture("Image", true, false, (module, method) =>
            {
                var il = method.Body.Instructions; var nameType = ((IMethod)il[1].Operand).DeclaringType;
                if (mutation == "unknown") { il[0] = Instruction.Create(OpCodes.Ldarg_0); return; }
                if (mutation == "concat")
                {
                    il[0] = Instruction.Create(OpCodes.Ldstr, "Im"); il.Insert(1, Instruction.Create(OpCodes.Ldstr, "age"));
                    il.Insert(2, Instruction.Create(OpCodes.Call, new Importer(module).Import(typeof(string).GetMethod("Concat", new[] { typeof(string), typeof(string) })))); return;
                }
                if (mutation == "local")
                {
                    var local = new Local(new ClassSig(nameType)); method.Body.Variables.Add(local);
                    il.Insert(2, Instruction.Create(OpCodes.Stloc, local)); il.Insert(3, Instruction.Create(OpCodes.Ldloc, local));
                }
                else if (mutation == "field")
                {
                    var field = new FieldDefUser("Alias", new FieldSig(new ClassSig(nameType)), dnlib.DotNet.FieldAttributes.Static); method.DeclaringType.Fields.Add(field);
                    il.Insert(2, Instruction.Create(OpCodes.Stsfld, field)); il.Insert(3, Instruction.Create(OpCodes.Ldsfld, field));
                }
                else
                {
                    il.Insert(2, Instruction.Create(OpCodes.Dup));
                    if (mutation == "setter" || mutation == "codebase")
                    {
                        il.Insert(3, Instruction.Create(OpCodes.Ldstr, mutation == "codebase" ? "Other.dll" : "Other"));
                        il.Insert(4, Instruction.Create(OpCodes.Callvirt, new Importer(module).Import(typeof(AssemblyName).GetProperty(mutation == "codebase" ? "CodeBase" : "Name").GetSetMethod())));
                    }
                    else if (mutation == "escape")
                    { il.Insert(3, Instruction.Create(OpCodes.Callvirt, new Importer(module).Import(typeof(object).GetMethod("ToString")))); il.Insert(4, Instruction.Create(OpCodes.Pop)); }
                    else il.Insert(3, Instruction.Create(OpCodes.Pop));
                }
            })) AssertDenied(fixture);
        }

        [Test] public void BranchSwitchAndExceptionEntriesCannotBypassTheLiteralProof()
        {
            foreach (string bypass in new[] { "constructor", "call", "nop", "switch", "catch" })
            using (var fixture = new Fixture("Image", true, false, (module, method) =>
            {
                var il = method.Body.Instructions;
                if (bypass == "nop") il.Insert(2, Instruction.Create(OpCodes.Nop));
                Instruction target = bypass == "constructor" ? il[1] : bypass == "nop" ? il[2] : il[il.Count - 2];
                if (bypass == "catch")
                {
                    // An exception entry can reach Load with an object that did
                    // not originate from the apparent preceding constructor.
                    method.Body.ExceptionHandlers.Add(new ExceptionHandler(ExceptionHandlerType.Catch)
                    { TryStart = il[0], TryEnd = target, HandlerStart = target, HandlerEnd = il[il.Count - 1], CatchType = module.CorLibTypes.Object.TypeDefOrRef });
                }
                else
                {
                    il.Insert(0, Instruction.Create(OpCodes.Ldnull));
                    if (bypass == "switch") { il.Insert(1, Instruction.Create(OpCodes.Ldc_I4_0)); il.Insert(2, Instruction.Create(OpCodes.Switch, new[] { target })); il.Insert(3, Instruction.Create(OpCodes.Pop)); }
                    else il.Insert(1, Instruction.Create(OpCodes.Br, target));
                }
                method.Body.KeepOldMaxStack = true; method.Body.MaxStack = 8;
            })) AssertDenied(fixture);
        }

        [Test] public void BranchToTheLiteralStartRemainsAValidFreshConstruction()
        {
            using (var fixture = new Fixture("Image", true, false, (module, method) =>
            { method.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Br, method.Body.Instructions[0])); }))
                Assert.IsTrue(fixture.Validate().IsValid, fixture.Validate().ToString());
        }

        [Test] public void FrameworkLookalikesWrongSignaturesAndIndirectCallsRemainUnknown()
        {
            foreach (string corruption in new[] { "constructor-owner", "load-owner", "parameter-owner", "return-owner", "version", "token", "overload", "constructor-overload", "indirect", "callvirt" })
            using (var fixture = new Fixture("Image", true))
            {
                // Start with actual resolver-loaded bytes; metadata corruptions
                // are scanner unit adversaries, never accepted loader evidence.
                var module = fixture.Set.GetModule("Consumer"); var method = module.GetTypes().SelectMany(type => type.Methods).Single();
                var il = method.Body.Instructions; var constructor = (MemberRef)il[1].Operand; var load = (MemberRef)il[2].Operand;
                var fake = new AssemblyRefUser("Lookalike", new Version(1, 0, 0, 0));
                if (corruption == "version" || corruption == "token")
                {
                    fake = new AssemblyRefUser(new AssemblyNameInfo(module.CorLibTypes.AssemblyRef.FullName));
                    if (corruption == "version") fake.Version = new Version(9, 0, 0, 0); else fake.PublicKeyOrToken = new PublicKeyToken("0011223344556677");
                }
                if (corruption == "constructor-owner") constructor.Class = new TypeRefUser(module, "System.Reflection", "AssemblyName", fake);
                else if (corruption == "load-owner" || corruption == "version" || corruption == "token") load.Class = new TypeRefUser(module, "System.Reflection", "Assembly", fake);
                else if (corruption == "parameter-owner") load.MethodSig.Params[0] = new ClassSig(new TypeRefUser(module, "System.Reflection", "AssemblyName", fake));
                else if (corruption == "return-owner") load.MethodSig.RetType = new ClassSig(new TypeRefUser(module, "System.Reflection", "Assembly", fake));
                else if (corruption == "overload") load.MethodSig.Params.Add(module.CorLibTypes.Object);
                else if (corruption == "constructor-overload") constructor.MethodSig.Params[0] = module.CorLibTypes.Object;
                else if (corruption == "indirect") il[2].OpCode = OpCodes.Ldftn;
                else il[2].OpCode = OpCodes.Callvirt;
                AssertDenied(fixture);
            }
        }

        [Test] public void FileLoadsAndUnknownAliasesCannotBeApprovedByProse()
        {
            foreach (string operation in new[] { "LoadFrom", "LoadFile", "UnsafeLoadFrom", "ReflectionOnlyLoad", "ReflectionOnlyLoadFrom" })
            using (var fixture = new Fixture("Image.dll", false, false, (module, method) => ((MemberRef)method.Body.Instructions[1].Operand).Name = operation))
                AssertDenied(fixture);
            using (var fixture = new Fixture("Missing", false)) AssertDenied(fixture);
        }

        [Test] public void ContextualNameNormalizationDoesNotBroadenTypeQualifiedNames()
        {
            using (var fixture = new Fixture("Fixture.Payload, Folder/Image.dll", false, false, (module, method) =>
            {
                method.Body.Instructions[1].Operand = new Importer(module).Import(typeof(Type).GetMethod("GetType", new[] { typeof(string) }));
                method.MethodSig.RetType = new Importer(module).Import(typeof(Type)).ToTypeSig();
            })) AssertDenied(fixture);
        }

        [Test] public void ExistingStringConstantPropagationAndPathReceiverProvenanceRemainIntact()
        {
            using (var fixture = new Fixture("Folder/Image.dll", false, true))
            {
                Assert.AreEqual(2, fixture.Scan().reflectionDependencies.Length);
                Assert.IsTrue(fixture.Validate().IsValid, fixture.Validate().ToString());
            }
            using (var fixture = new Fixture("Image", false, true, (module, method) =>
            {
                var il = method.Body.Instructions; il[0] = Instruction.Create(OpCodes.Ldstr, "Im");
                il.Insert(1, Instruction.Create(OpCodes.Ldstr, "age"));
                il.Insert(2, Instruction.Create(OpCodes.Call, new Importer(module).Import(typeof(string).GetMethod("Concat", new[] { typeof(string), typeof(string) }))));
            })) Assert.IsTrue(fixture.Validate().IsValid, fixture.Validate().ToString());
        }

        [Test] public void NewPathSupportRequiresTheExactCapturedFrameworkIdentity()
        {
            using (var fixture = new Fixture("Folder/Image.dll", false))
            {
                var module = fixture.Set.GetModule("Consumer"); var load = (MemberRef)module.GetTypes().SelectMany(type => type.Methods).Single().Body.Instructions[1].Operand;
                load.Class = new TypeRefUser(module, "System.Reflection", "Assembly", new AssemblyRefUser("Lookalike"));
                AssertDenied(fixture);
            }
            using (var fixture = new Fixture("Image", true))
            {
                fixture.Set.GetModule("mscorlib").Assembly.Version = new Version(9, 0, 0, 0);
                AssertDenied(fixture);
            }
        }

        [Test] public void FiniteNameProofDoesNotAuthorizeAnAuthenticallyFilteredProvider()
        {
            foreach (bool named in new[] { false, true })
            using (var fixture = new Fixture("Image", named))
            {
                fixture.Set.Get("Image").classification = AssemblyClassification.BuildFiltered;
                StringAssert.Contains("RuntimeReferencesFilteredAssembly", fixture.Validate().ToString());
            }
        }

        private static void AssertDenied(Fixture fixture)
        {
            var scan = fixture.Scan(); Assert.IsTrue(scan.managedAcquisitions.Any(value => value.requiresContract && !value.verified));
            StringAssert.Contains("UnboundedManagedAcquisition", fixture.Validate().ToString());
        }

        private sealed class Fixture : IDisposable
        {
            private readonly string root = Path.Combine(Path.GetTempPath(), "M04NameAcquisition-" + Guid.NewGuid().ToString("N"));
            internal CompiledAssemblySet Set;
            internal ShadowPolicyConfiguration Policy = new ShadowPolicyConfiguration();
            internal Fixture(string literal, bool named, bool getType = false, Action<ModuleDefUser, MethodDef> mutate = null)
            {
                Directory.CreateDirectory(root); string references = Path.Combine(root, "References"), assemblies = Path.Combine(root, "Assemblies");
                Directory.CreateDirectory(references); Directory.CreateDirectory(assemblies);
                File.Copy(typeof(object).Assembly.Location, Path.Combine(references, "mscorlib.dll"));
                using (var image = NewModule("Image"))
                { image.Types.Add(new TypeDefUser("Fixture", "Payload", image.CorLibTypes.Object.TypeDefOrRef) { Attributes = dnlib.DotNet.TypeAttributes.Public }); image.Write(Path.Combine(assemblies, "Image.dll")); }
                using (var module = NewModule("Consumer"))
                {
                    var importer = new Importer(module); var host = new TypeDefUser("Fixture", "Host", module.CorLibTypes.Object.TypeDefOrRef); module.Types.Add(host);
                    var method = new MethodDefUser("Run", MethodSig.CreateStatic(importer.Import(getType ? typeof(Type) : typeof(Assembly)).ToTypeSig(), module.CorLibTypes.String),
                        dnlib.DotNet.MethodImplAttributes.IL, dnlib.DotNet.MethodAttributes.Public | dnlib.DotNet.MethodAttributes.Static) { Body = new CilBody() }; host.Methods.Add(method);
                    var il = method.Body.Instructions; il.Add(Instruction.Create(OpCodes.Ldstr, literal));
                    if (named) il.Add(Instruction.Create(OpCodes.Newobj, importer.Import(typeof(AssemblyName).GetConstructor(new[] { typeof(string) }))));
                    il.Add(Instruction.Create(OpCodes.Call, importer.Import(typeof(Assembly).GetMethod("Load", new[] { named ? typeof(AssemblyName) : typeof(string) }))));
                    if (getType) { il.Add(Instruction.Create(OpCodes.Ldstr, "Fixture.Payload")); il.Add(Instruction.Create(OpCodes.Callvirt, importer.Import(typeof(Assembly).GetMethod("GetType", new[] { typeof(string) })))); }
                    il.Add(Instruction.Create(OpCodes.Ret)); if (mutate != null) mutate(module, method); module.Write(Path.Combine(assemblies, "Consumer.dll"));
                }
                Set = DnlibAssemblyLoader.Load(assemblies, new[] { references }, new[] { new AssemblyCapability { name = "Consumer" }, new AssemblyCapability { name = "Image" } });
                Policy.dependencies.runtimeDependencies = new[] { new DeclaredRuntimeDependency
                { consumer = "Consumer", provider = "Image", callSite = "Fixture.Host::Run", kind = "finite name", evidence = "Intended finite Image dependency, not an unknown-operation waiver" } };
            }
            internal ShadowPolicyValidationResult Validate() { return ShadowAssemblyPolicyValidator.ValidateCompiled(Set, Policy, DateTime.UtcNow); }
            internal AssemblyPolicyDefinition Scan()
            {
                var result = new AssemblyPolicyDefinition { name = "consumer" };
                typeof(ShadowAssemblyPolicyValidator).Assembly.GetType("HybridCLR.Editor.AssemblyShadow.ReflectionDependencyScanner")
                    .GetMethod("Scan", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { Set.Modules, "consumer", result });
                return result;
            }
            private static ModuleDefUser NewModule(string name)
            {
                var module = new ModuleDefUser(name + ".dll", Guid.NewGuid(), new AssemblyRefUser(new AssemblyNameInfo(typeof(object).Assembly.FullName))) { Kind = ModuleKind.Dll };
                new AssemblyDefUser(name, new Version(1, 0, 0, 0)).Modules.Add(module); return module;
            }
            public void Dispose() { if (Set != null) Set.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }
}
