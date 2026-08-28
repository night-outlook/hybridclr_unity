using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using NUnit.Framework;

namespace HybridCLR.Editor.AssemblyShadow.Tests
{
    public sealed class M05TypeLiteralAcquisitionTests
    {
        private const string Box = "Fixture.Box`1[[Fixture.Payload, Candidate]], Stable";
        private const string Pair = "Fixture.Pair`2[[Fixture.Payload, Candidate],[Fixture.Other, Candidate]], Candidate";

        [Test] public void StableOuterCannotHideCandidateArgumentAndTruthfulEdgesReachClosure()
        {
            using (var fixture = new Fixture(Box))
            {
                CollectionAssert.AreEquivalent(new[] { "mscorlib" }, fixture.Set.Get("Consumer").references);
                var scan = fixture.Scan(); Assert.IsEmpty(scan.unknownReflectionCallSites);
                CollectionAssert.AreEquivalent(new[] { "stable", "candidate" }, scan.reflectionDependencies.Select(item => item.provider));
                StringAssert.Contains("UndeclaredReflectionDependency", fixture.Validate().ToString());
                fixture.Declare("Stable"); StringAssert.Contains("candidate", fixture.Validate().ToString());
                fixture.Declare("Stable", "Candidate"); Assert.IsTrue(fixture.Validate().IsValid, fixture.Validate().ToString());
                var graph = new AssemblyReferenceGraph(fixture.Set.Assemblies.Values, fixture.Policy.dependencies);
                Assert.Throws<ShadowBuildException>(() => graph.ReverseClosure(new[] { "Candidate" }));
                fixture.Set.Get("Consumer").isShadowCapable = true;
                fixture.Set.Get("Facade").isShadowCapable = true;
                CollectionAssert.AreEquivalent(new[] { "candidate", "consumer", "facade" }, graph.ReverseClosure(new[] { "Candidate" }).Select(item => item.ToLowerInvariant()));
            }
        }

        [Test] public void ActualAssemblyRefsSatisfyEveryBusinessComponentWithoutInventedEdges()
        {
            using (var fixture = new Fixture(Box, (module, method) =>
            {
                foreach (string name in new[] { "Stable", "Candidate" })
                {
                    var reference = new TypeRefUser(module, "Fixture", "Payload", new AssemblyRefUser(name, new Version(1, 0, 0, 0)));
                    method.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Pop));
                    method.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Ldtoken, reference));
                }
            }))
            {
                Assert.IsTrue(fixture.Validate().IsValid, fixture.Validate().ToString());
                var graph = new AssemblyReferenceGraph(fixture.Set.Assemblies.Values, fixture.Policy.dependencies);
                Assert.IsTrue(graph.Edges.Any(edge => edge.consumer.Equals("consumer", StringComparison.OrdinalIgnoreCase) && edge.provider.Equals("candidate", StringComparison.OrdinalIgnoreCase)));
            }
        }

        [Test] public void BootstrapMustApproveEverySameProviderTypeWithTheOriginalWholeLiteral()
        {
            using (var fixture = new Fixture(Pair))
            {
                fixture.Set.Get("Consumer").isBootstrap = true;
                var scan = fixture.Scan(); Assert.AreEqual(3, scan.reflectionDependencies.Length);
                fixture.Policy.dependencies.bootstrapEntrypoints = scan.reflectionDependencies.Select(item => new BootstrapEntrypointDeclaration
                { consumer = "Consumer", provider = item.provider, typeName = item.typeName, callSite = "Fixture.Host::Run", target = Pair, reason = "Exact finite generic composition" }).ToArray();
                Assert.IsTrue(fixture.Validate().IsValid, fixture.Validate().ToString());
                new AssemblyReferenceGraph(fixture.Set.Assemblies.Values, fixture.Policy.dependencies);
                fixture.Policy.dependencies.bootstrapEntrypoints = fixture.Policy.dependencies.bootstrapEntrypoints.Take(2).ToArray();
                StringAssert.Contains("BootstrapReflection", fixture.Validate().ToString());
                fixture.Policy.dependencies.bootstrapEntrypoints = scan.reflectionDependencies.Select(item => new BootstrapEntrypointDeclaration
                { consumer = "Consumer", provider = item.provider, typeName = item.typeName, callSite = "Fixture.Host::Run", target = Pair + " ", reason = "Wrong literal" }).ToArray();
                StringAssert.Contains("BootstrapReflection", fixture.Validate().ToString());
            }
        }

        [Test] public void ClosedNestedGenericDefinitionsRequireCombinedMetadataArity()
        {
            foreach (string literal in new[] {
                "Fixture.Outer`1+Inner`1[[Fixture.Payload, Candidate],[Fixture.Other, Candidate]], Candidate",
                "Fixture.Box`1[[Fixture.Outer`1+Inner`1[[Fixture.Payload, Candidate],[Fixture.Other, Candidate]], Candidate]], Stable",
                "Fixture.Box`1[[Fixture.PlainOuter+Inner, Candidate]], Stable" })
            using (var fixture = new Fixture(literal))
            {
                Assert.IsEmpty(fixture.Scan().unknownReflectionCallSites, literal);
                fixture.Declare("Stable", "Candidate"); Assert.IsTrue(fixture.Validate().IsValid, fixture.Validate().ToString());
            }
        }

        [Test] public void BusinessForwarderAndTerminalCandidateBothRequireTruthfulDependencies()
        {
            using (var fixture = new Fixture("Fixture.Box`1[[Fixture.Payload, Facade]], Stable"))
            {
                CollectionAssert.AreEquivalent(new[] { "stable", "facade", "candidate" }, fixture.Scan().reflectionDependencies.Select(item => item.provider));
                fixture.Declare("Stable", "Facade"); StringAssert.Contains("candidate", fixture.Validate().ToString());
                fixture.Declare("Stable", "Candidate"); StringAssert.Contains("facade", fixture.Validate().ToString());
                fixture.Declare("Stable", "Facade", "Candidate"); Assert.IsTrue(fixture.Validate().IsValid, fixture.Validate().ToString());
                var graph = new AssemblyReferenceGraph(fixture.Set.Assemblies.Values, fixture.Policy.dependencies);
                Assert.IsTrue(graph.Edges.Any(edge => edge.provider.Equals("Candidate", StringComparison.OrdinalIgnoreCase) && edge.consumer.Equals("Facade", StringComparison.OrdinalIgnoreCase)));
            }
        }

        [Test] public void AuthenticatedReferenceFacadeDoesNotNeedInventedBusinessEdges()
        {
            using (var fixture = new Fixture("Fixture.Box`1[[System.Int32, Profile]], Stable"))
            {
                CollectionAssert.AreEquivalent(new[] { "stable", "profile", "mscorlib" }, fixture.Scan().reflectionDependencies.Select(item => item.provider));
                fixture.Declare("Stable"); Assert.IsTrue(fixture.Validate().IsValid, fixture.Validate().ToString());
                Assert.IsFalse(fixture.Policy.dependencies.runtimeDependencies.Any(edge => edge.provider == "Profile"));
                foreach (AssemblyClassification role in new[] { AssemblyClassification.Runtime, AssemblyClassification.NormalHotUpdate, AssemblyClassification.BuildFiltered })
                {
                    fixture.Policy.assemblies = new[] { new AssemblyCapability { name = "Profile", classification = role, capabilityDeclared = true } };
                    Assert.IsFalse(fixture.Validate().IsValid, role.ToString());
                }
            }
        }

        [Test] public void RuntimeAndFilteredBusinessProvidersNeverAcquireTheReferenceExemption()
        {
            foreach (AssemblyClassification role in new[] { AssemblyClassification.Runtime, AssemblyClassification.NormalHotUpdate, AssemblyClassification.BuildFiltered })
            using (var fixture = new Fixture(Box))
            {
                fixture.Set.Get("Candidate").classification = role; fixture.Set.Get("Candidate").isShadowCapable = false;
                fixture.Declare("Stable"); Assert.IsFalse(fixture.Validate().IsValid, role.ToString());
                if (role == AssemblyClassification.BuildFiltered) StringAssert.Contains("RuntimeReferencesFilteredAssembly", fixture.Validate().ToString());
                else StringAssert.Contains("UndeclaredReflectionDependency", fixture.Validate().ToString());
            }
        }

        [Test] public void MalformedUnsupportedAndMissingComponentsFailAtomically()
        {
            string[] literals = {
                "Fixture.Box`1[[Fixture.Payload, Missing]], Stable", "Fixture.Box`1[[Fixture.Missing, Candidate]], Stable",
                "Fixture.Box`1[[Fixture.Payload, Candidate],[Fixture.Other, Candidate]], Stable",
                "Fixture.Payload[[Fixture.Payload, Candidate]], Candidate", "Fixture.Box`2[[Fixture.Payload, Candidate]], Stable",
                "Fixture.Box`1[Fixture.Payload], Stable", "Fixture.Box`1[[Fixture.Payload]], Stable", "Fixture.Box`1[[Fixture.Payload, Candidate]], Missing",
                "Fixture.Box`1[[Fixture.Payload, Candidate]], Stable]", "Fixture.Box`1[[Fixture.Payload, Candidate], Stable",
                "Fixture.Box`1[[Fixture.Payload, Candidate]], Stable, Bogus=x", "Fixture.Box`1[[Fixture.Payload, Candidate, Bogus=x]], Stable",
                "Fixture.Box`1[[Fixture.Payload, Candidate, Version=1.0.0.0, Version=1.0.0.0]], Stable",
                "Fixture.Box`1[[Fixture.Payload, Candidate, Culture=neutral, culture=neutral]], Stable",
                "Fixture.Box`1[[Fixture.Payload, Candidate, PublicKeyToken=null, PublicKeyToken=null]], Stable",
                "Fixture.Box`1[[Fixture.Payload, Candidate, Version=2.0.0.0]], Stable",
                "Fixture.Box`1[[Fixture.Payload, Candidate, Culture=en-US]], Stable",
                "Fixture.Box`1[[Fixture.Payload, Candidate, PublicKeyToken=1234567890abcdef]], Stable",
                "Fixture.Box`1[[Fixture.Payload, Candidate, Version=1.0]], Stable",
                "Fixture.Box`1[[Fixture.Payload, Candidate, Version=65536.0.0.0]], Stable",
                "Fixture.Box`1[[Fixture.Payload, Candidate, PublicKeyToken=bad]], Stable",
                "Fixture.Box`1[[Fixture.Payload, folder/Candidate.dll]], Stable", "Fixture.Box`1[[Fixture.Payload, Candidate.dll]], Stable",
                "Fixture.Box`1[[Fixture.Payload, Candidate]], Stable,", "Fixture.Box`1[[Fixture.Payload, Candidate]], Stable,,Version=1.0.0.0",
                "Fixture.Box`1[[Fixture.Payload[], Candidate]], Stable", "Fixture.Box`1[[Fixture.Payload*, Candidate]], Stable",
                "Fixture.Box`1[[Fixture.Payload&, Candidate]], Stable", "Fixture.Box`1[[Fixture.Pay\\load, Candidate]], Stable",
                "Fixture.Box`1[[Fixture.Payload, Candidate]], Stáble", "Fixture.Box`1[[Fixture.Payload, Candidate]], Stable\0",
                "Fixture.Box`1[[Fixture.Payload, Candidate]], Stable, Version=0.0.0.0"
            };
            foreach (string literal in literals) using (var fixture = new Fixture(literal)) AssertDenied(fixture, literal);
        }

        [Test] public void ExplicitFullIdentitiesAreComparedWithoutParserNormalization()
        {
            string literal = "Fixture.Box`1[[Fixture.Payload, Candidate, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null]], Stable, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";
            using (var fixture = new Fixture(literal)) { Assert.IsEmpty(fixture.Scan().unknownReflectionCallSites); Assert.AreEqual(literal, fixture.Scan().reflectionDependencies[0].target); }
            string core = typeof(object).Assembly.FullName;
            using (var fixture = new Fixture("Fixture.Box`1[[System.Int32, " + core + "]], Stable")) Assert.IsEmpty(fixture.Scan().unknownReflectionCallSites);
            using (var fixture = new Fixture("Fixture.Box`1[[System.Int32, " + core.Replace("Version=4.0.0.0", "Version=9.0.0.0") + "]], Stable")) AssertDenied(fixture, "wrong primitive scope version");
        }

        [Test] public void DynamicArgumentsAndConflictingBranchesRemainUnknown()
        {
            foreach (bool branch in new[] { false, true })
            using (var fixture = new Fixture(Box, (module, method) =>
            {
                var il = method.Body.Instructions;
                if (!branch) il[0] = Instruction.Create(OpCodes.Ldarg_0);
                else
                {
                    Instruction alternate = Instruction.Create(OpCodes.Ldstr, Pair), next = il[1];
                    il.Insert(0, Instruction.Create(OpCodes.Ldarg_0)); il.Insert(1, Instruction.Create(OpCodes.Brtrue, alternate));
                    il.Insert(3, Instruction.Create(OpCodes.Br, next)); il.Insert(4, alternate);
                }
            })) AssertDenied(fixture, branch.ToString());
        }

        [Test] public void AssemblyGetTypeRequiresItsProvenReceiverAndAllGenericArgumentProviders()
        {
            foreach (bool knownReceiver in new[] { true, false })
            using (var fixture = new Fixture("Fixture.Box`1[[Fixture.Payload, Candidate]]", (module, method) =>
            {
                var il = method.Body.Instructions; var called = (MemberRef)il[2].Operand;
                called.Class = new TypeRefUser(module, "System.Reflection", "Assembly", module.CorLibTypes.AssemblyRef);
                called.MethodSig.HasThis = true; il[2].OpCode = OpCodes.Callvirt;
                if (knownReceiver)
                {
                    il.Insert(0, Instruction.Create(OpCodes.Call, new Importer(module).Import(typeof(Assembly).GetMethod("Load", new[] { typeof(string) }))));
                    il.Insert(0, Instruction.Create(OpCodes.Ldstr, "Stable"));
                }
                else il.Insert(0, Instruction.Create(OpCodes.Ldnull));
            }))
            {
                if (!knownReceiver) { AssertDenied(fixture, "unknown assembly receiver"); continue; }
                CollectionAssert.AreEquivalent(new[] { "stable", "candidate" }, fixture.Scan().reflectionDependencies.Where(item => item.kind == "Assembly.GetType").Select(item => item.provider));
                fixture.Declare("Stable", "Candidate"); Assert.IsTrue(fixture.Validate().IsValid, fixture.Validate().ToString());
            }
        }

        [Test] public void MetadataArityAndCatalogAmbiguityAreNotGuessedFromNames()
        {
            foreach (string mode in new[] { "wrongNumber", "missingParameter", "nestedArity", "duplicateType", "duplicateModule" })
            using (var fixture = new Fixture(mode == "nestedArity" ? "Fixture.Outer`1+Inner`1[[Fixture.Payload, Candidate],[Fixture.Other, Candidate]], Candidate" : Box))
            {
                // Mutations are serialized and reloaded: these are malformed
                // metadata bytes, not fabricated descriptor reference lists.
                ModuleDefMD original = fixture.Set.GetModule(mode == "nestedArity" ? "Candidate" : "Stable");
                using (var copy = ModuleDefMD.Load(fixture.ReadBytes(original.Assembly.Name.String)))
                {
                    TypeDef type = copy.GetTypes().Single(item => item.FullName == (mode == "nestedArity" ? "Fixture.Outer`1/Inner`1" : "Fixture.Box`1"));
                    if (mode == "wrongNumber") type.GenericParameters[0].Number = 1;
                    else if (mode == "missingParameter") type.GenericParameters.Clear();
                    else if (mode == "nestedArity") type.Name = "Inner`2";
                    else if (mode == "duplicateType") copy.Types.Add(new TypeDefUser("Fixture", "Box`1", copy.CorLibTypes.Object.TypeDefOrRef));
                    byte[] bytes; using (var stream = new MemoryStream()) { copy.Write(stream); bytes = stream.ToArray(); }
                    using (var changed = ModuleDefMD.Load(bytes))
                    {
                        var modules = fixture.Set.Modules.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
                        if (mode == "duplicateModule") modules.Add("second-stable", changed); else modules[original.Assembly.Name.String.ToLowerInvariant()] = changed;
                        Assert.IsEmpty(ScanModule(modules, "consumer").reflectionDependencies, mode);
                    }
                }
            }
        }

        [Test] public void ParserLimitsRejectExcessiveNestingWithoutPartialEvidence()
        {
            string literal = "Fixture.Payload, Candidate";
            for (int depth = 0; depth < 35; ++depth) literal = "Fixture.Box`1[[" + literal + "]], Stable";
            using (var fixture = new Fixture(literal)) AssertDenied(fixture, "depth limit");
        }

        [Test] public void ResolverDelegatesIgnoreCaseAndFrameworkLookalikesCannotClaimLiteralProof()
        {
            foreach (string mode in new[] { "ignoreCase", "resolver", "simpleResolver", "owner", "return", "generic" })
            using (var fixture = new Fixture(mode == "simpleResolver" ? "Fixture.Payload, Candidate" : Box, (module, method) =>
            {
                var il = method.Body.Instructions; var call = il[2]; var member = (MemberRef)call.Operand;
                if (mode == "ignoreCase")
                { member.MethodSig.Params.Add(module.CorLibTypes.Boolean); il.Insert(2, Instruction.Create(OpCodes.Ldc_I4_0)); }
                else if (mode == "resolver" || mode == "simpleResolver")
                { member.MethodSig.Params[1] = module.CorLibTypes.Object; il[1] = Instruction.Create(OpCodes.Ldnull); }
                else if (mode == "owner") member.Class = new TypeRefUser(module, "System", "Type", new AssemblyRefUser("Lookalike", new Version(1, 0, 0, 0)));
                else if (mode == "return") member.MethodSig.RetType = new ClassSig(new TypeRefUser(module, "System", "Type", new AssemblyRefUser("Lookalike", new Version(1, 0, 0, 0))));
                else { member.MethodSig.GenParamCount = 1; member.MethodSig.Generic = true; call.Operand = new MethodSpecUser(member, new GenericInstMethodSig(module.CorLibTypes.Int32)); }
            })) AssertDenied(fixture, mode);
        }

        [Test] public void CyclicAmbiguousAndWrongIdentityForwardersRemainUnknown()
        {
            foreach (string mode in new[] { "cycle", "duplicate", "definitionAndForwarder", "version", "token", "nestedCycle" })
            {
                TestDelegate check = () =>
                {
                    using (var fixture = new Fixture("Fixture.Box`1[[Fixture.Payload, Facade]], Stable", null, (module) =>
                    {
                        var export = module.ExportedTypes.Single();
                        if (mode == "cycle") export.Implementation = new AssemblyRefUser("Facade", new Version(1, 0, 0, 0));
                        // Distinct rows; dnlib coalesces byte-identical exports.
                        else if (mode == "duplicate") module.ExportedTypes.Add(new ExportedTypeUser(module, 0, "Fixture", "Payload", export.Attributes | dnlib.DotNet.TypeAttributes.Public, export.Implementation));
                        else if (mode == "definitionAndForwarder") AddType(module, "Payload");
                        else if (mode == "nestedCycle") export.Implementation = export;
                        else { var target = (AssemblyRef)export.Implementation; if (mode == "version") target.Version = new Version(2, 0, 0, 0); else target.PublicKeyOrToken = new PublicKeyToken("1234567890abcdef"); }
                    }, false)) AssertDenied(fixture, mode);
                };
                // Identity mismatch is rejected even before scanner entry.
                if (mode == "version" || mode == "token") StringAssert.Contains("AssemblyIdentityMismatch", Assert.Throws<ShadowBuildException>(check).Message);
                else if (mode == "nestedCycle") StringAssert.Contains("UnsupportedExportedType", Assert.Throws<ShadowBuildException>(check).Message);
                else check();
            }
        }

        [Test] public void NestedForwarderChildrenUseActualOuterForwarderIdentity()
        {
            using (var fixture = new Fixture("Fixture.Box`1[[Fixture.PlainOuter+Inner, Facade]], Stable", null, module =>
            {
                module.ExportedTypes.Clear(); var outer = new ExportedTypeUser(module, 0, "Fixture", "PlainOuter", dnlib.DotNet.TypeAttributes.Forwarder, new AssemblyRefUser("Candidate", new Version(1, 0, 0, 0)));
                module.ExportedTypes.Add(outer); module.ExportedTypes.Add(new ExportedTypeUser(module, 0, "", "Inner", 0, outer));
            }))
            { Assert.IsEmpty(fixture.Scan().unknownReflectionCallSites); CollectionAssert.AreEquivalent(new[] { "stable", "facade", "candidate" }, fixture.Scan().reflectionDependencies.Select(item => item.provider)); }
        }

        [Test] public void AllFiveCandidateAssemblyNamesAcceptRequiredP03GenericLiteralShape()
        {
            foreach (string[] item in WitnessNames)
            foreach (string form in new[] { "Payload", "Outer+Inner", "Generic`1[[System.Int32, mscorlib]]" })
            using (var fixture = new Fixture(item[1] + "." + item[2] + form + ", " + item[0], null, null, true, item[0]))
            { Assert.IsEmpty(fixture.Scan().unknownReflectionCallSites, item[0] + ":" + form); CollectionAssert.Contains(fixture.Scan().reflectionDependencies.Select(value => value.provider).ToArray(), item[0].ToLowerInvariant()); }
        }

        private static readonly string[][] WitnessNames = {
            new[] { "AssemblyA.Contracts", "AssemblyA.Contracts", "M05Contract" },
            new[] { "AssemblyA.Implementation.Extensibility", "AssemblyA.Implementation.Extensibility", "M05Extensibility" },
            new[] { "AssemblyA.Implementation.Internal", "AssemblyA.Implementation.Internal", "M05Internal" },
            new[] { "AssemblyShadowDemo.ContractsConsumer", "AssemblyShadowDemo.Consumers", "M05ContractsConsumer" },
            new[] { "AssemblyShadowDemo.ExtensibilityConsumer", "AssemblyShadowDemo.Consumers", "M05ExtensibilityConsumer" }
        };

        private static void AssertDenied(Fixture fixture, string context)
        {
            var scan = fixture.Scan(); Assert.IsEmpty(scan.reflectionDependencies, context);
            Assert.IsTrue(scan.managedAcquisitions.Any(item => item.requiresContract && !item.verified), context);
            StringAssert.Contains("UnboundedManagedAcquisition", fixture.Validate().ToString(), context);
        }

        private sealed class Fixture : IDisposable
        {
            private readonly string root = Path.Combine(Path.GetTempPath(), "M05TypeLiteral-" + Guid.NewGuid().ToString("N"));
            internal CompiledAssemblySet Set;
            internal readonly ShadowPolicyConfiguration Policy = new ShadowPolicyConfiguration();
            internal Fixture(string literal, Action<ModuleDefUser, MethodDef> mutate = null, Action<ModuleDefUser> mutateFacade = null, bool strict = true, string extraAssembly = null)
            {
                string assemblies = Path.Combine(root, "Assemblies"), references = Path.Combine(root, "References");
                Directory.CreateDirectory(assemblies); Directory.CreateDirectory(references);
                File.Copy(typeof(object).Assembly.Location, Path.Combine(references, "mscorlib.dll"));
                foreach (string name in new[] { "Stable", "Candidate" }.Concat(extraAssembly == null ? new string[0] : new[] { extraAssembly }))
                using (var module = NewModule(name))
                {
                    AddType(module, "Payload"); AddType(module, "Other"); AddType(module, "Box`1", 1); AddType(module, "Pair`2", 2);
                    var outer = AddType(module, "Outer`1", 1); var inner = new TypeDefUser("", "Inner`1", module.CorLibTypes.Object.TypeDefOrRef); outer.NestedTypes.Add(inner);
                    inner.GenericParameters.Add(new GenericParamUser(0)); inner.GenericParameters.Add(new GenericParamUser(1));
                    var plain = AddType(module, "PlainOuter"); plain.NestedTypes.Add(new TypeDefUser("", "Inner", module.CorLibTypes.Object.TypeDefOrRef));
                    if (name == extraAssembly)
                    {
                        string[] witness = WitnessNames.Single(item => item[0] == name);
                        foreach (string suffix in new[] { "Payload", "Generic`1", "Outer" })
                        {
                            TypeDef type = AddType(module, witness[2] + suffix, suffix == "Generic`1" ? 1 : 0); type.Namespace = witness[1];
                            if (suffix == "Outer") type.NestedTypes.Add(new TypeDefUser("", "Inner", module.CorLibTypes.Object.TypeDefOrRef));
                        }
                    }
                    module.Write(Path.Combine(assemblies, name + ".dll"));
                }
                using (var module = NewModule("Lookalike"))
                { module.Types.Add(new TypeDefUser("System", "Type", module.CorLibTypes.Object.TypeDefOrRef)); module.Write(Path.Combine(assemblies, "Lookalike.dll")); }
                using (var module = NewModule("Facade"))
                {
                    module.ExportedTypes.Add(new ExportedTypeUser(module, 0, "Fixture", "Payload", dnlib.DotNet.TypeAttributes.Forwarder, new AssemblyRefUser("Candidate", new Version(1, 0, 0, 0))));
                    if (mutateFacade != null) mutateFacade(module); module.Write(Path.Combine(assemblies, "Facade.dll"));
                }
                using (var module = NewModule("Profile"))
                { module.ExportedTypes.Add(new ExportedTypeUser(module, 0, "System", "Int32", dnlib.DotNet.TypeAttributes.Forwarder, module.CorLibTypes.AssemblyRef)); module.Write(Path.Combine(references, "Profile.dll")); }
                using (var module = NewModule("Consumer"))
                {
                    var type = AddType(module, "Host"); var importer = new Importer(module);
                    var method = new MethodDefUser("Run", MethodSig.CreateStatic(importer.Import(typeof(Type)).ToTypeSig(), module.CorLibTypes.String),
                        dnlib.DotNet.MethodImplAttributes.IL, dnlib.DotNet.MethodAttributes.Public | dnlib.DotNet.MethodAttributes.Static) { Body = new CilBody() }; type.Methods.Add(method);
                    method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldstr, literal)); method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_1));
                    method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, importer.Import(typeof(Type).GetMethod("GetType", new[] { typeof(string), typeof(bool) }))));
                    method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); if (mutate != null) mutate(module, method);
                    module.Write(Path.Combine(assemblies, "Consumer.dll"));
                }
                try { Set = DnlibAssemblyLoader.Load(assemblies, new[] { references }, new[] { new AssemblyCapability { name = "Candidate", isShadowCapable = true, capabilityDeclared = true } }, strict); }
                catch { Directory.Delete(root, true); throw; }
            }
            internal byte[] ReadBytes(string assembly) { return File.ReadAllBytes(Path.Combine(root, "Assemblies", assembly + ".dll")); }
            internal void Declare(params string[] providers)
            { Policy.dependencies.runtimeDependencies = providers.Select(provider => new DeclaredRuntimeDependency { consumer = "Consumer", provider = provider, kind = "Reflection", evidence = "Exact literal component" }).ToArray(); }
            internal AssemblyPolicyDefinition Scan() { return ScanModule(Set.Modules, "consumer"); }
            internal ShadowPolicyValidationResult Validate() { return ShadowAssemblyPolicyValidator.ValidateCompiled(Set, Policy, DateTime.UtcNow); }
            public void Dispose() { if (Set != null) Set.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
        }

        private static ModuleDefUser NewModule(string name)
        {
            var result = new ModuleDefUser(name + ".dll", Guid.NewGuid(), new AssemblyRefUser(new AssemblyNameInfo(typeof(object).Assembly.FullName))) { Kind = ModuleKind.Dll };
            new AssemblyDefUser(name, new Version(1, 0, 0, 0)).Modules.Add(result); return result;
        }
        private static TypeDef AddType(ModuleDef module, string name, int arity = 0)
        {
            var type = new TypeDefUser("Fixture", name, module.CorLibTypes.Object.TypeDefOrRef) { Attributes = dnlib.DotNet.TypeAttributes.Public }; module.Types.Add(type);
            for (int index = 0; index < arity; ++index) type.GenericParameters.Add(new GenericParamUser((ushort)index)); return type;
        }
        private static AssemblyPolicyDefinition ScanModule(IReadOnlyDictionary<string, ModuleDefMD> modules, string name)
        {
            var definition = new AssemblyPolicyDefinition { name = name };
            typeof(ShadowAssemblyPolicyValidator).Assembly.GetType("HybridCLR.Editor.AssemblyShadow.ReflectionDependencyScanner")
                .GetMethod("Scan", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { modules, name, definition });
            return definition;
        }

        // Standalone read-only replay hook. Normal NUnit tests never depend on
        // a machine-local build artifact or silently skip missing fixtures.
        public static string VerifyCapturedP01(string root)
        {
            var modules = new Dictionary<string, ModuleDefMD>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (string directory in new[] { "Assemblies", "References" })
                foreach (string path in Directory.GetFiles(Path.Combine(root, directory), "*.dll", SearchOption.AllDirectories))
                {
                    var module = ModuleDefMD.Load(File.ReadAllBytes(path)); string key = module.Assembly.Name.String.ToLowerInvariant();
                    if (modules.ContainsKey(key)) { module.Dispose(); continue; } modules.Add(key, module);
                }
                var scan = ScanModule(modules, "assemblya.implementation.internal");
                const string site = "AssemblyA.Implementation.Internal.InternalEntry::GetM05TypeNameForms";
                Assert.IsFalse(scan.unknownReflectionCallSites.Contains(site)); Assert.IsFalse(scan.managedAcquisitions.Any(item => item.callSite == site && item.requiresContract && !item.verified));
                var evidence = scan.reflectionDependencies.Where(item => item.callSite == site).ToArray();
                Assert.AreEqual(5, evidence.Length); // two plain literals + generic definition + both facade scopes
                CollectionAssert.Contains(evidence.Select(item => item.provider).ToArray(), "mscorlib");
                CollectionAssert.Contains(evidence.Select(item => item.provider).ToArray(), "netstandard");
                return "CAPTURED_P01 site=" + site + " evidence=" + evidence.Length + " unknown=0 providers=" + string.Join(",", evidence.Select(item => item.provider).Distinct().ToArray());
            }
            finally { foreach (var module in modules.Values) module.Dispose(); }
        }
    }
}
