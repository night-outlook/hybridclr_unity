using System;
using System.IO;
using System.Linq;
using System.Reflection;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using HybridCLR.AssemblyShadow.CodeGen;
using NUnit.Framework;

namespace HybridCLR.Editor.AssemblyShadow.Tests
{
    public sealed class RawTypeAdmissionPolicyTests
    {
        [Test] public void RealLoaderRequiresTheNewDomainAndAcceptsOnlyItsExactBootstrapOperations()
        {
            using (var fixture = new Fixture())
            {
                CollectionAssert.Contains(fixture.Set.Modules.Keys, "consumer");
                StringAssert.Contains("UnboundedManagedAcquisition", fixture.Validate(null).ToString());
                var result = fixture.Validate(fixture.Configuration);
                Assert.IsTrue(result.IsValid, result.ToString());
                fixture.Configuration.sites[0].methodHash = new string('0', 64);
                StringAssert.Contains("InvalidRawTypeAdmissionContract", fixture.Validate(fixture.Configuration).ToString());
                StringAssert.Contains("UnboundedManagedAcquisition", fixture.Validate(fixture.Configuration).ToString());
            }
        }

        [Test] public void MethodProseAndOldBootstrapDeclarationsDoNotAuthorizeRawAcquisition()
        {
            using (var fixture = new Fixture())
            {
                fixture.Policy.dependencies.bootstrapEntrypoints = new[] { new BootstrapEntrypointDeclaration
                { consumer = "Consumer", provider = "Candidate", typeName = "Fixture.Payload", method = "Fixture.Host::Run", target = "Candidate", reason = "All raw operations are intended" } };
                StringAssert.Contains("UnboundedManagedAcquisition", fixture.Validate(null).ToString());
                fixture.Set.Get("Consumer").isBootstrap = false;
                fixture.Policy.dependencies.bootstrapEntrypoints = new BootstrapEntrypointDeclaration[0];
                fixture.Policy.dependencies.runtimeDependencies = new[] { new DeclaredRuntimeDependency
                { consumer = "Consumer", provider = "Candidate", callSite = "Fixture.Host::Run", kind = "RawTypeAdmission", evidence = "Method prose is not a typed proof" } };
                StringAssert.Contains("UnboundedManagedAcquisition", fixture.Validate(null).ToString());
            }
        }

        [Test] public void ActualDescriptorRolesCannotBeInferredFromConfigurationOrResolverPresence()
        {
            foreach (var classification in new[] { AssemblyClassification.Reference, AssemblyClassification.NormalHotUpdate, AssemblyClassification.BuildFiltered, AssemblyClassification.EditorOnly, AssemblyClassification.TestOnly })
            using (var fixture = new Fixture())
            {
                fixture.Set.Get("Candidate").classification = classification;
                StringAssert.Contains("InvalidRawAdmissionProviderRole", fixture.Validate(fixture.Configuration).ToString(), classification.ToString());
            }
            using (var fixture = new Fixture())
            { fixture.Set.Get("Candidate").isBootstrap = true; StringAssert.Contains("InvalidRawAdmissionProviderRole", fixture.Validate(fixture.Configuration).ToString()); }
            using (var fixture = new Fixture())
            { fixture.Set.Get("Candidate").isShadowCapable = false; StringAssert.Contains("InvalidRawAdmissionProviderRole", fixture.Validate(fixture.Configuration).ToString()); }
            using (var fixture = new Fixture())
            { fixture.Set.Get("Consumer").classification = AssemblyClassification.BuildFiltered; StringAssert.Contains("InvalidRawAdmissionConsumerRole", fixture.Validate(fixture.Configuration).ToString()); }
        }

        [Test] public void NonBootstrapConsumersStillNeedTruthfulDependencyEdges()
        {
            using (var fixture = new Fixture())
            {
                fixture.Set.Get("Consumer").isBootstrap = false;
                StringAssert.Contains("UndeclaredReflectionDependency", fixture.Validate(fixture.Configuration).ToString());
                fixture.Policy.dependencies.runtimeDependencies = new[] { new DeclaredRuntimeDependency
                { consumer = "Consumer", provider = "Candidate", kind = "raw type query", evidence = "The compiled exact helper acquires Candidate definitions" } };
                Assert.IsTrue(fixture.Validate(fixture.Configuration).IsValid, fixture.Validate(fixture.Configuration).ToString());
            }
        }

        [Test] public void UnconfiguredSiblingEnumerationAndIndirectLookupRemainRejected()
        {
            using (var fixture = new Fixture(true))
            {
                var result = fixture.Validate(fixture.Configuration);
                StringAssert.Contains("UnboundedManagedAcquisition", result.ToString());
                StringAssert.Contains("Other", result.ToString());
            }
        }

        [Test] public void OutsideDirectBrokerCallRequiresCandidateDependency()
        { AssertUntrackedBrokerRejected("direct"); }

        [Test] public void SameBootstrapForwarderCannotHideCandidateDependency()
        { AssertUntrackedBrokerRejected("same-bootstrap"); }

        [Test] public void CrossBootstrapForwarderCannotHideCandidateDependency()
        { AssertUntrackedBrokerRejected("cross-bootstrap"); }

        [Test] public void LiteralReflectionOnBrokerRequiresCandidateNotMerelyBrokerDependency()
        { AssertUntrackedBrokerRejected("reflection"); }

        [Test] public void OutsideBrokerDelegateCannotHideCandidateDependency()
        { AssertUntrackedBrokerRejected("delegate"); }

        [Test] public void BrokerTypeTokenAndReflectiveInvocationCannotHideCandidateDependency()
        { AssertUntrackedBrokerRejected("type-token"); }

        [Test] public void GenericMethodSpecForwarderCannotHideCandidateDependency()
        { AssertUntrackedBrokerRejected("generic"); }

        [Test] public void TruthfulCandidateEdgesPutOutsideSelectorsIntoActualReverseClosure()
        {
            foreach (string access in new[] { "direct", "same-bootstrap", "cross-bootstrap", "generic" })
            using (var fixture = new Fixture(brokerAccess: access, shadowOutside: true))
            {
                fixture.Policy.dependencies.runtimeDependencies = new[] { new DeclaredRuntimeDependency {
                    consumer = "Outside", provider = "Candidate", kind = "admitted selector", evidence = "Outside selects Candidate through the exact Bootstrap broker." } };
                Assert.IsTrue(fixture.Validate(fixture.Configuration).IsValid, fixture.Validate(fixture.Configuration).ToString());
                var graph = new AssemblyReferenceGraph(fixture.Set.Assemblies.Values, fixture.Policy.dependencies);
                CollectionAssert.AreEquivalent(new[] { "Candidate", "Outside" }, graph.ReverseClosure(new[] { "Candidate" }));
                Assert.IsFalse(fixture.Set.Get("Outside").references.Contains("candidate"), "The explicit Candidate edge must not be fabricated as an AssemblyRef.");
            }
        }

        [Test] public void TruthfulNonShadowSelectorIsCaughtByReverseClosure()
        {
            using (var fixture = new Fixture(brokerAccess: "direct"))
            {
                fixture.Policy.dependencies.runtimeDependencies = new[] { new DeclaredRuntimeDependency {
                    consumer = "Outside", provider = "Candidate", kind = "admitted selector", evidence = "Actual finite provider selection." } };
                Assert.IsTrue(fixture.Validate(fixture.Configuration).IsValid, fixture.Validate(fixture.Configuration).ToString());
                var graph = new AssemblyReferenceGraph(fixture.Set.Assemblies.Values, fixture.Policy.dependencies);
                StringAssert.Contains("NonShadowConsumer", Assert.Throws<ShadowBuildException>(() => graph.ReverseClosure(new[] { "Candidate" })).Message);
            }
        }

        [Test] public void WrapperLambdaExportIsRejectedWithoutAnOutsideBootstrapReference()
        {
            using (var fixture = new Fixture(customizeConsumer: module =>
            {
                var host = module.Types.Single(type => type.Name == "Host"); var original = host.Methods.Single(value => value.Name == "Run");
                Fixture.AddForwarder(module, host, "<Factory>b__0", original);
                var importer = new Importer(module); var factory = Fixture.Method(host, "Factory", MethodSig.CreateStatic(importer.Import(typeof(Func<Type[]>)).ToTypeSig()));
                factory.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull));
                factory.Body.Instructions.Add(Instruction.Create(OpCodes.Ldftn, host.Methods.Single(method => method.Name == "<Factory>b__0")));
                factory.Body.Instructions.Add(Instruction.Create(OpCodes.Newobj, importer.Import(typeof(Func<Type[]>).GetConstructor(new[] { typeof(object), typeof(IntPtr) }))));
                factory.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            }))
                StringAssert.Contains("RawSelectorDelegateExposure", fixture.Validate(fixture.Configuration).ToString());
        }

        [Test] public void OutsideInterfaceDispatchCannotSelectThroughAStoredBootstrapImplementation()
        {
            using (var fixture = new Fixture(interfaceBridge: true))
            {
                CollectionAssert.AreEquivalent(new[] { "mscorlib" }, fixture.Set.Get("Bridge").references);
                CollectionAssert.AreEquivalent(new[] { "mscorlib", "bridge" }, fixture.Set.Get("Consumer").references);
                var graph = new AssemblyReferenceGraph(fixture.Set.Assemblies.Values, fixture.Policy.dependencies);
                CollectionAssert.AreEqual(new[] { "Candidate" }, graph.ReverseClosure(new[] { "Candidate" }));
                var result = fixture.Validate(fixture.Configuration);
                StringAssert.Contains("RawSelectorVirtualExposure", result.ToString());
                StringAssert.Contains("RawSelectorDependencyMissing", result.ToString());
            }
        }

        [Test] public void AlreadySelectedTypePlumbingLinqCacheAndBooleanIteratorRemainAllowed()
        {
            using (var fixture = new Fixture(customizeConsumer: module => Fixture.AddOpaquePlumbing(module)))
                Assert.IsTrue(fixture.Validate(fixture.Configuration).IsValid, fixture.Validate(fixture.Configuration).ToString());
        }

        [Test] public void ActualSelectedHandleFieldStorageIsRejected()
        {
            using (var fixture = new Fixture(customizeConsumer: module =>
            {
                var host = module.Types.Single(type => type.Name == "Host");
                var field = new FieldDefUser("Selected", new FieldSig(new Importer(module).Import(typeof(Type[])).ToTypeSig()), dnlib.DotNet.FieldAttributes.Public | dnlib.DotNet.FieldAttributes.Static); host.Fields.Add(field);
                var method = Fixture.Method(host, "Store", MethodSig.CreateStatic(module.CorLibTypes.Void));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, host.Methods.Single(value => value.Name == "Run")));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Stsfld, field)); method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            })) StringAssert.Contains("RawSelectorFieldExposure", fixture.Validate(fixture.Configuration).ToString());
        }

        [Test] public void CallerOwnedMutableOutputsCannotHideProviderSelection()
        {
            foreach (string access in new[] { "array", "alias", "nested-array", "byref", "field", "writer", "collection", "return-alias", "published-local" })
            using (var fixture = new Fixture(brokerAccess: "output-" + access))
            {
                CollectionAssert.AreEquivalent(new[] { "mscorlib", "consumer" }, fixture.Set.Get("Outside").references, access);
                var graph = new AssemblyReferenceGraph(fixture.Set.Assemblies.Values, fixture.Policy.dependencies);
                CollectionAssert.AreEqual(new[] { "Candidate" }, graph.ReverseClosure(new[] { "Candidate" }), access);
                string diagnostic = access == "field" ? "RawSelectorFieldExposure" : access == "byref" ? "RawSelectorIndirectStoreExposure" : access == "collection" ? "RawSelectorUnprovedOutputCall" : "RawSelectorOutputExposure";
                StringAssert.Contains(diagnostic, fixture.Validate(fixture.Configuration).ToString(), access);
                fixture.Policy.dependencies.runtimeDependencies = new[] { new DeclaredRuntimeDependency { consumer = "Outside", provider = "Candidate", kind = "selector", evidence = "Truthful edges cannot authorize unsupported output storage." } };
                StringAssert.Contains(diagnostic, fixture.Validate(fixture.Configuration).ToString(), access + " must still reject unsupported output exposure after the edge is declared.");
            }
        }

        [Test] public void PublishingAnEmptyContainerThroughAHelperMustSurviveUntilLaterSelection()
        {
            using (var fixture = new Fixture(brokerAccess: "publication-helper-before"))
            {
                CollectionAssert.AreEquivalent(new[] { "mscorlib" }, fixture.Set.Get("Outside").references);
                CollectionAssert.AreEquivalent(new[] { "mscorlib", "outside" }, fixture.Set.Get("Consumer").references);
                CollectionAssert.AreEqual(new[] { "Candidate" }, new AssemblyReferenceGraph(fixture.Set.Assemblies.Values, fixture.Policy.dependencies).ReverseClosure(new[] { "Candidate" }));
                Assert.IsFalse(fixture.Validate(fixture.Configuration).IsValid,
                    "Publishing an empty fresh array must not hide the later Candidate Type[] write from an outside reader with no Bootstrap reference.");
            }
        }

        [Test] public void PublicationEffectsCrossReceiversReturnsNestedAliasesAndHelperChains()
        {
            foreach (string shape in new[] { "same-before", "helper-after", "multi-before", "multi-after", "return-before", "returned-global", "returned-wrapper", "receiver-before", "constructor-before", "nested-before", "related-arguments", "unknown-before", "unknown-return" })
            using (var fixture = new Fixture(brokerAccess: "publication-" + shape))
            {
                CollectionAssert.AreEquivalent(new[] { "mscorlib" }, fixture.Set.Get("Outside").references, shape);
                CollectionAssert.AreEqual(new[] { "Candidate" }, new AssemblyReferenceGraph(fixture.Set.Assemblies.Values, fixture.Policy.dependencies).ReverseClosure(new[] { "Candidate" }), shape);
                var result = fixture.Validate(fixture.Configuration);
                Assert.IsFalse(result.IsValid, shape + " loses publication across a physical helper/alias boundary.");
                Assert.IsFalse(result.ToString().Contains("InvalidRawSelectorPropagation"), shape + " must be rejected for actual output exposure, not a broken analyzer: " + result);
            }
        }

        [Test] public void ProvenReadOnlyCallsAndPrivateAliasMaterializationDoNotPublishRoots()
        {
            foreach (string shape in new[] { "readonly-before", "readonly-return", "readonly-constructor", "local-link" })
            using (var fixture = new Fixture(brokerAccess: "publication-" + shape))
                Assert.IsTrue(fixture.Validate(fixture.Configuration).IsValid, shape + ": " + fixture.Validate(fixture.Configuration));
        }

        [Test] public void FreshLocalMaterializationAndReadOnlyMutableInputsRemainAllowed()
        {
            using (var fixture = new Fixture(customizeConsumer: module => Fixture.AddLocalOutputPlumbing(module)))
                Assert.IsTrue(fixture.Validate(fixture.Configuration).IsValid, fixture.Validate(fixture.Configuration).ToString());
        }

        [Test] public void CustomDelegateInvokeSignatureCannotPublishSelectedHandles()
        {
            using (var fixture = new Fixture(brokerAccess: "custom-callback"))
            {
                CollectionAssert.AreEquivalent(new[] { "mscorlib", "consumer" }, fixture.Set.Get("Outside").references);
                CollectionAssert.AreEqual(new[] { "Candidate" }, new AssemblyReferenceGraph(fixture.Set.Assemblies.Values, fixture.Policy.dependencies).ReverseClosure(new[] { "Candidate" }));
                StringAssert.Contains("RawSelectorCallbackExposure", fixture.Validate(fixture.Configuration).ToString());
            }
        }

        [Test] public void CompositeBrokerTokensRemainForbiddenEvenWithTruthfulProviderEdges()
        {
            foreach (string access in new[] { "generic-token", "nested-generic-token", "array-token" })
            using (var fixture = new Fixture(brokerAccess: access, shadowOutside: true))
            {
                fixture.Policy.dependencies.runtimeDependencies = new[] { new DeclaredRuntimeDependency { consumer = "Outside", provider = "Candidate", kind = "selector", evidence = "Truthful provider edge does not authorize broker token exposure." } };
                StringAssert.Contains("RawSelectorTypeTokenExposure", fixture.Validate(fixture.Configuration).ToString(), access);
            }
        }

        [Test] public void IncomingMetadataOnlyReferencesRequireTruthfulProviderEdges()
        {
            foreach (string access in new[] { "metadata-field", "metadata-signature", "metadata-cross-bootstrap" })
            using (var fixture = new Fixture(brokerAccess: access, shadowOutside: true))
            {
                CollectionAssert.AreEquivalent(new[] { "mscorlib", access == "metadata-cross-bootstrap" ? "forwarder" : "consumer" }, fixture.Set.Get("Outside").references);
                StringAssert.Contains("RawSelectorDependencyMissing", fixture.Validate(fixture.Configuration).ToString(), access);
                fixture.Policy.dependencies.runtimeDependencies = new[] { new DeclaredRuntimeDependency { consumer = "Outside", provider = "Candidate", kind = "selector assembly", evidence = "Incoming metadata reference to the selector-bearing Bootstrap." } };
                Assert.IsTrue(fixture.Validate(fixture.Configuration).IsValid, fixture.Validate(fixture.Configuration).ToString());
                CollectionAssert.AreEquivalent(new[] { "Candidate", "Outside" }, new AssemblyReferenceGraph(fixture.Set.Assemblies.Values, fixture.Policy.dependencies).ReverseClosure(new[] { "Candidate" }));
            }
        }

        [Test] public void NonBrokerCompositeTokensDoNotGainSelectorRestrictions()
        {
            using (var fixture = new Fixture(brokerAccess: "nonbroker-token"))
                Assert.IsTrue(fixture.Validate(fixture.Configuration).IsValid, fixture.Validate(fixture.Configuration).ToString());
        }

        [Test] public void ClosedGenericScalarProjectionAndRecursiveReadOnlyPredicatesRemainAllowed()
        {
            using (var fixture = new Fixture(customizeConsumer: module => Fixture.AddScalarProjection(module), includeLinqReferences: true))
                Assert.IsTrue(fixture.Validate(fixture.Configuration).IsValid, fixture.Validate(fixture.Configuration).ToString());
        }

        [Test] public void AThrownLocalContainerCannotPublishSelectedHandles()
        {
            using (var fixture = new Fixture(customizeConsumer: module =>
            {
                var importer = new Importer(module); var host = module.Types.Single(type => type.Name == "Host");
                var envelope = new TypeDefUser("Fixture", "SelectedException", importer.Import(typeof(Exception))) { Attributes = dnlib.DotNet.TypeAttributes.Public }; module.Types.Add(envelope);
                var field = new FieldDefUser("Types", new FieldSig(importer.Import(typeof(Type[])).ToTypeSig()), dnlib.DotNet.FieldAttributes.Public); envelope.Fields.Add(field);
                var ctor = new MethodDefUser(".ctor", MethodSig.CreateInstance(module.CorLibTypes.Void, field.FieldSig.Type), dnlib.DotNet.MethodImplAttributes.IL,
                    dnlib.DotNet.MethodAttributes.Public | dnlib.DotNet.MethodAttributes.SpecialName | dnlib.DotNet.MethodAttributes.RTSpecialName) { Body = new CilBody() }; envelope.Methods.Add(ctor);
                ctor.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0)); ctor.Body.Instructions.Add(Instruction.Create(OpCodes.Call, importer.Import(typeof(Exception).GetConstructor(Type.EmptyTypes))));
                ctor.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0)); ctor.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_1)); ctor.Body.Instructions.Add(Instruction.Create(OpCodes.Stfld, field)); ctor.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                var method = Fixture.Method(host, "ThrowSelected", MethodSig.CreateStatic(module.CorLibTypes.Void)); method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, host.Methods.Single(value => value.Name == "Run")));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Newobj, ctor)); method.Body.Instructions.Add(Instruction.Create(OpCodes.Throw));
            })) StringAssert.Contains("RawSelectorThrownHandleExposure", fixture.Validate(fixture.Configuration).ToString());
        }

        private static void AssertUntrackedBrokerRejected(string access)
        {
            using (var fixture = new Fixture(brokerAccess: access))
            {
                // Every descriptor and AssemblyRef comes from emitted DLL bytes.
                // The reflective case truthfully declares Outside -> Consumer,
                // but deliberately has no Outside -> Candidate declaration.
                string target = access == "cross-bootstrap" ? "forwarder" : "consumer";
                string[] expectedReferences = access == "reflection" ? new[] { "mscorlib" } : new[] { "mscorlib", target };
                CollectionAssert.AreEquivalent(expectedReferences, fixture.Set.Get("Outside").references);
                CollectionAssert.AreEquivalent(new[] { "mscorlib" }, fixture.Set.Get("Consumer").references);
                if (access == "cross-bootstrap")
                    CollectionAssert.AreEquivalent(new[] { "mscorlib", "consumer" }, fixture.Set.Get("Forwarder").references);

                var graph = new AssemblyReferenceGraph(fixture.Set.Assemblies.Values, fixture.Policy.dependencies);
                Assert.IsTrue(graph.Edges.Any(edge => edge.consumer == "Outside" && edge.provider.Equals(target, StringComparison.OrdinalIgnoreCase)));
                if (access == "cross-bootstrap")
                    Assert.IsTrue(graph.Edges.Any(edge => edge.consumer == "Forwarder" && edge.provider == "Consumer"));
                Assert.IsFalse(graph.Edges.Any(edge => edge.provider == "Candidate"));
                CollectionAssert.AreEqual(new[] { "Candidate" }, graph.ReverseClosure(new[] { "Candidate" }),
                    "The declared graph cannot include this outside provider-selector until its Candidate dependency is represented.");

                var result = fixture.Validate(fixture.Configuration);
                Assert.IsFalse(result.IsValid, access + ": admitted Bootstrap provider selection must not be accepted while the graph only contains " +
                    string.Join(", ", graph.Edges.Select(edge => edge.consumer + " -> " + edge.provider)) + ". Actual policy: " + result);
            }
        }

        private sealed class Fixture : IDisposable
        {
            private readonly string root = Path.Combine(Path.GetTempPath(), "RawTypeAdmissionPolicy-" + Guid.NewGuid().ToString("N"));
            internal CompiledAssemblySet Set;
            internal readonly ShadowPolicyConfiguration Policy = new ShadowPolicyConfiguration();
            internal RawTypeAdmissionConfiguration Configuration;
            internal Fixture(bool extra = false, string brokerAccess = null, bool shadowOutside = false,
                Action<ModuleDefUser> customizeConsumer = null, bool interfaceBridge = false, bool includeLinqReferences = false)
            {
                bool crossBootstrap = brokerAccess == "cross-bootstrap" || brokerAccess == "metadata-cross-bootstrap";
                string assemblies = Path.Combine(root, "Assemblies"), references = Path.Combine(root, "References");
                Directory.CreateDirectory(assemblies); Directory.CreateDirectory(references);
                File.Copy(typeof(object).Assembly.Location, Path.Combine(references, "mscorlib.dll"));
                if (includeLinqReferences)
                {
                    var pending = new System.Collections.Generic.Queue<Assembly>(); var copied = new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
                    pending.Enqueue(typeof(Enumerable).Assembly);
                    while (pending.Count > 0)
                    {
                        var assembly = pending.Dequeue(); if (!copied.Add(assembly.FullName)) continue;
                        string path = Path.Combine(references, assembly.GetName().Name + ".dll"); if (!File.Exists(path)) File.Copy(assembly.Location, path);
                        foreach (var reference in assembly.GetReferencedAssemblies()) pending.Enqueue(Assembly.Load(reference));
                    }
                }
                using (var provider = Module("Candidate"))
                { provider.Types.Add(new TypeDefUser("Fixture", "Payload", provider.CorLibTypes.Object.TypeDefOrRef)); provider.Write(Path.Combine(assemblies, "Candidate.dll")); }
                if (interfaceBridge) using (var bridge = Module("Bridge")) { AddInterfaceBridge(bridge); bridge.Write(Path.Combine(assemblies, "Bridge.dll")); }
                using (var consumer = Module("Consumer"))
                {
                    var host = new TypeDefUser("Fixture", "Host", consumer.CorLibTypes.Object.TypeDefOrRef) { Attributes = dnlib.DotNet.TypeAttributes.Public }; consumer.Types.Add(host);
                    AddMethod(consumer, host, "Run", false); if (extra) AddMethod(consumer, host, "Other", true);
                    if (brokerAccess == "same-bootstrap" || brokerAccess == "generic")
                    {
                        AddForwarder(consumer, host, "Forward", host.Methods.Single(value => value.Name == "Run"));
                        if (brokerAccess == "generic")
                        {
                            var generic = host.Methods.Single(value => value.Name == "Forward");
                            generic.MethodSig.CallingConvention |= CallingConvention.Generic; generic.MethodSig.GenParamCount = 1;
                            generic.GenericParameters.Add(new GenericParamUser(0, GenericParamAttributes.NonVariant, "T"));
                        }
                    }
                    if (interfaceBridge) AddInterfaceBroker(consumer, host);
                    if (brokerAccess != null && brokerAccess.StartsWith("output-", StringComparison.Ordinal)) AddOutputBroker(consumer, brokerAccess.Substring(7));
                    if (brokerAccess == "custom-callback") AddCallbackBroker(consumer);
                    if (brokerAccess != null && brokerAccess.StartsWith("publication-", StringComparison.Ordinal)) AddPublicationFlow(consumer, brokerAccess.Substring(12));
                    if (customizeConsumer != null) customizeConsumer(consumer);
                    consumer.Write(Path.Combine(assemblies, "Consumer.dll"));
                }
                if (crossBootstrap)
                    using (var forwarder = Module("Forwarder"))
                    {
                        var host = new TypeDefUser("Fixture", "ForwarderHost", forwarder.CorLibTypes.Object.TypeDefOrRef) { Attributes = dnlib.DotNet.TypeAttributes.Public }; forwarder.Types.Add(host);
                        if (brokerAccess == "metadata-cross-bootstrap") host.Fields.Add(new FieldDefUser("Metadata", new FieldSig(new ClassSig(BrokerMethod(forwarder, "Consumer", "Host", "Run").DeclaringType)), dnlib.DotNet.FieldAttributes.Public));
                        else AddForwarder(forwarder, host, "Forward", BrokerMethod(forwarder, "Consumer", "Host", "Run"));
                        forwarder.Write(Path.Combine(assemblies, "Forwarder.dll"));
                    }
                if (brokerAccess != null)
                    using (var outside = Module("Outside"))
                    {
                        AddOutsideBrokerAccess(outside, brokerAccess);
                        outside.Write(Path.Combine(assemblies, "Outside.dll"));
                    }
                var capabilities = new System.Collections.Generic.List<AssemblyCapability> {
                    new AssemblyCapability { name = "Consumer", isBootstrap = true }, new AssemblyCapability { name = "Candidate", isShadowCapable = true } };
                if (brokerAccess != null) capabilities.Add(new AssemblyCapability { name = "Outside", isShadowCapable = shadowOutside });
                if (crossBootstrap) capabilities.Add(new AssemblyCapability { name = "Forwarder", isBootstrap = true });
                if (interfaceBridge) capabilities.Add(new AssemblyCapability { name = "Bridge" });
                Set = DnlibAssemblyLoader.Load(assemblies, new[] { references }, capabilities);
                if (brokerAccess == "reflection")
                    Policy.dependencies.runtimeDependencies = new[] { new DeclaredRuntimeDependency {
                        consumer = "Outside", provider = "Consumer", kind = "literal broker reflection", evidence = "Fixture.Host is literally acquired from Consumer; no Candidate dependency has been declared." } };
                var method = Set.GetModule("Consumer").GetTypes().SelectMany(type => type.Methods).Single(value => value.Name == "Run");
                Configuration = new RawTypeAdmissionConfiguration { sites = new[] { new RawTypeAdmissionSite
                { id = "raw", consumerAssembly = "Consumer", declaringType = "Fixture.Host", methodSignature = method.FullName,
                    methodHash = ReflectionBindingFingerprint.Compute(method), operationIndex = 2,
                    operationSignature = ((IMethod)method.Body.Instructions[2].Operand).FullName,
                    providerAssemblyIdentity = Set.GetModule("Candidate").Assembly.FullName, typeName = "", throwOnError = false, ignoreCase = false,
                    reason = "An exact raw query is observed; native state remains independently enforced." } } };
            }
            internal ShadowPolicyValidationResult Validate(RawTypeAdmissionConfiguration configuration)
            { return ShadowAssemblyPolicyValidator.ValidateCompiled(Set, Policy, DateTime.UtcNow, rawTypeAdmissionConfiguration: configuration); }
            private static MemberRef BrokerMethod(ModuleDef module, string assembly, string type, string method)
            {
                return new MemberRefUser(module, method, MethodSig.CreateStatic(new Importer(module).Import(typeof(Type[])).ToTypeSig()),
                    new TypeRefUser(module, "Fixture", type, new AssemblyRefUser(assembly, new Version(1, 0, 0, 0))));
            }
            internal static void AddForwarder(ModuleDef module, TypeDef host, string name, IMethod target)
            {
                var method = new MethodDefUser(name, MethodSig.CreateStatic(new Importer(module).Import(typeof(Type[])).ToTypeSig()),
                    dnlib.DotNet.MethodImplAttributes.IL, dnlib.DotNet.MethodAttributes.Public | dnlib.DotNet.MethodAttributes.Static) { Body = new CilBody() }; host.Methods.Add(method);
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, target)); method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            }
            internal static MethodDef Method(TypeDef host, string name, MethodSig signature, bool instance = false)
            {
                var method = new MethodDefUser(name, signature, dnlib.DotNet.MethodImplAttributes.IL,
                    dnlib.DotNet.MethodAttributes.Public | (instance ? dnlib.DotNet.MethodAttributes.Virtual | dnlib.DotNet.MethodAttributes.NewSlot | dnlib.DotNet.MethodAttributes.Final : dnlib.DotNet.MethodAttributes.Static)) { Body = new CilBody() };
                host.Methods.Add(method); return method;
            }
            private static void AddInterfaceBridge(ModuleDefUser module)
            {
                var importer = new Importer(module); var typeArray = importer.Import(typeof(Type[])).ToTypeSig();
                var contract = new TypeDefUser("Fixture", "ITypeQuery") { Attributes = dnlib.DotNet.TypeAttributes.Public | dnlib.DotNet.TypeAttributes.Interface | dnlib.DotNet.TypeAttributes.Abstract }; module.Types.Add(contract);
                var query = new MethodDefUser("Query", MethodSig.CreateInstance(typeArray, module.CorLibTypes.String), dnlib.DotNet.MethodImplAttributes.IL,
                    dnlib.DotNet.MethodAttributes.Public | dnlib.DotNet.MethodAttributes.Abstract | dnlib.DotNet.MethodAttributes.Virtual | dnlib.DotNet.MethodAttributes.NewSlot); contract.Methods.Add(query);
                var host = new TypeDefUser("Fixture", "BridgeHost", module.CorLibTypes.Object.TypeDefOrRef) { Attributes = dnlib.DotNet.TypeAttributes.Public }; module.Types.Add(host);
                var sink = new FieldDefUser("Sink", new FieldSig(new ClassSig(contract)), dnlib.DotNet.FieldAttributes.Public | dnlib.DotNet.FieldAttributes.Static); host.Fields.Add(sink);
                var caller = Method(host, "Select", MethodSig.CreateStatic(typeArray)); var il = caller.Body.Instructions;
                il.Add(Instruction.Create(OpCodes.Ldsfld, sink)); il.Add(Instruction.Create(OpCodes.Ldstr, "Candidate")); il.Add(Instruction.Create(OpCodes.Callvirt, query)); il.Add(Instruction.Create(OpCodes.Ret));
            }
            private static void AddInterfaceBroker(ModuleDefUser module, TypeDef host)
            {
                var importer = new Importer(module); var bridge = new AssemblyRefUser("Bridge", new Version(1, 0, 0, 0));
                var contract = new TypeRefUser(module, "Fixture", "ITypeQuery", bridge);
                var broker = new TypeDefUser("Fixture", "Broker", module.CorLibTypes.Object.TypeDefOrRef) { Attributes = dnlib.DotNet.TypeAttributes.Public | dnlib.DotNet.TypeAttributes.Sealed }; module.Types.Add(broker);
                broker.Interfaces.Add(new InterfaceImplUser(contract));
                var query = Method(broker, "Query", MethodSig.CreateInstance(importer.Import(typeof(Type[])).ToTypeSig(), module.CorLibTypes.String), true);
                query.Body.Instructions.Add(Instruction.Create(OpCodes.Call, host.Methods.Single(value => value.Name == "Run"))); query.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                var ctor = new MethodDefUser(".ctor", MethodSig.CreateInstance(module.CorLibTypes.Void), dnlib.DotNet.MethodImplAttributes.IL,
                    dnlib.DotNet.MethodAttributes.Public | dnlib.DotNet.MethodAttributes.SpecialName | dnlib.DotNet.MethodAttributes.RTSpecialName) { Body = new CilBody() }; broker.Methods.Add(ctor);
                ctor.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0)); ctor.Body.Instructions.Add(Instruction.Create(OpCodes.Call, importer.Import(typeof(object).GetConstructor(Type.EmptyTypes)))); ctor.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                var register = Method(host, "Register", MethodSig.CreateStatic(module.CorLibTypes.Void));
                register.Body.Instructions.Add(Instruction.Create(OpCodes.Newobj, ctor));
                register.Body.Instructions.Add(Instruction.Create(OpCodes.Stsfld, new MemberRefUser(module, "Sink", new FieldSig(new ClassSig(contract)), new TypeRefUser(module, "Fixture", "BridgeHost", bridge))));
                register.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            }
            internal static void AddOpaquePlumbing(ModuleDefUser module)
            {
                var importer = new Importer(module); var host = module.Types.Single(type => type.Name == "Host");
                var converter = Method(host, "ConvertInfo", MethodSig.CreateStatic(importer.Import(typeof(Type)).ToTypeSig(), importer.Import(typeof(TypeInfo)).ToTypeSig()));
                converter.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0)); converter.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, importer.Import(typeof(TypeInfo).GetMethod("AsType")))); converter.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                var cache = new FieldDefUser("CachedConverter", new FieldSig(importer.Import(typeof(Func<TypeInfo, Type>)).ToTypeSig()), dnlib.DotNet.FieldAttributes.Static); host.Fields.Add(cache);
                var observer = Method(host, "Observe", MethodSig.CreateStatic(module.CorLibTypes.Void)); var il = observer.Body.Instructions;
                var local = new Local(importer.Import(typeof(Type[])).ToTypeSig()); observer.Body.Variables.Add(local);
                il.Add(Instruction.Create(OpCodes.Call, host.Methods.Single(value => value.Name == "Run"))); il.Add(Instruction.Create(OpCodes.Stloc, local));
                var ready = Instruction.Create(OpCodes.Nop); il.Add(Instruction.Create(OpCodes.Ldsfld, cache)); il.Add(Instruction.Create(OpCodes.Brtrue, ready));
                il.Add(Instruction.Create(OpCodes.Ldnull)); il.Add(Instruction.Create(OpCodes.Ldftn, converter));
                il.Add(Instruction.Create(OpCodes.Newobj, importer.Import(typeof(Func<TypeInfo, Type>).GetConstructor(new[] { typeof(object), typeof(IntPtr) })))); il.Add(Instruction.Create(OpCodes.Stsfld, cache)); il.Add(ready);
                il.Add(Instruction.Create(OpCodes.Ldloc, local)); il.Add(Instruction.Create(OpCodes.Pop)); il.Add(Instruction.Create(OpCodes.Ret));
                var iterator = new TypeDefUser("Fixture", "ProbeIterator", module.CorLibTypes.Object.TypeDefOrRef); module.Types.Add(iterator);
                iterator.Interfaces.Add(new InterfaceImplUser(importer.Import(typeof(System.Collections.IEnumerator))));
                var move = Method(iterator, "MoveNext", MethodSig.CreateInstance(module.CorLibTypes.Boolean), true);
                move.Body.Instructions.Add(Instruction.Create(OpCodes.Call, observer)); move.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0)); move.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                var current = Method(iterator, "get_Current", MethodSig.CreateInstance(module.CorLibTypes.Object), true); current.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull)); current.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                var reset = Method(iterator, "Reset", MethodSig.CreateInstance(module.CorLibTypes.Void), true); reset.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                var complete = Method(host, "Complete", MethodSig.CreateStatic(module.CorLibTypes.Void, module.CorLibTypes.Int32)); complete.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                var completion = Method(host, "Completion", MethodSig.CreateStatic(importer.Import(typeof(Action<int>)).ToTypeSig()));
                completion.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull)); completion.Body.Instructions.Add(Instruction.Create(OpCodes.Ldftn, complete));
                completion.Body.Instructions.Add(Instruction.Create(OpCodes.Newobj, importer.Import(typeof(Action<int>).GetConstructor(new[] { typeof(object), typeof(IntPtr) })))); completion.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            }
            private static void AddOutsideBrokerAccess(ModuleDefUser outside, string access)
            {
                var importer = new Importer(outside);
                var host = new TypeDefUser("Fixture", "OutsideHost", outside.CorLibTypes.Object.TypeDefOrRef) { Attributes = dnlib.DotNet.TypeAttributes.Public }; outside.Types.Add(host);
                var method = new MethodDefUser("SelectCandidateTypes", MethodSig.CreateStatic(importer.Import(typeof(Type[])).ToTypeSig()),
                    dnlib.DotNet.MethodImplAttributes.IL, dnlib.DotNet.MethodAttributes.Public | dnlib.DotNet.MethodAttributes.Static) { Body = new CilBody() }; host.Methods.Add(method);
                var il = method.Body.Instructions;
                if (access.StartsWith("publication-", StringComparison.Ordinal))
                {
                    var published = new FieldDefUser("Published", new FieldSig(new SZArraySig(outside.CorLibTypes.Object)), dnlib.DotNet.FieldAttributes.Public | dnlib.DotNet.FieldAttributes.Static); host.Fields.Add(published);
                    il.Add(Instruction.Create(OpCodes.Ldsfld, published)); il.Add(Instruction.Create(OpCodes.Ldc_I4_0)); il.Add(Instruction.Create(OpCodes.Ldelem_Ref));
                    if (access == "publication-nested-before" || access == "publication-returned-wrapper" || access == "publication-related-arguments")
                    { il.Add(Instruction.Create(OpCodes.Castclass, published.FieldSig.Type.ToTypeDefOrRef())); il.Add(Instruction.Create(OpCodes.Ldc_I4_0)); il.Add(Instruction.Create(OpCodes.Ldelem_Ref)); }
                    il.Add(Instruction.Create(OpCodes.Castclass, importer.Import(typeof(Type[]))));
                }
                else if (access == "custom-callback")
                {
                    var callback = new TypeDefUser("Fixture", "SelectedCallback", importer.Import(typeof(MulticastDelegate))) { Attributes = dnlib.DotNet.TypeAttributes.Public | dnlib.DotNet.TypeAttributes.Sealed }; outside.Types.Add(callback);
                    var ctor = new MethodDefUser(".ctor", MethodSig.CreateInstance(outside.CorLibTypes.Void, outside.CorLibTypes.Object, outside.CorLibTypes.IntPtr), dnlib.DotNet.MethodImplAttributes.Runtime,
                        dnlib.DotNet.MethodAttributes.Public | dnlib.DotNet.MethodAttributes.SpecialName | dnlib.DotNet.MethodAttributes.RTSpecialName); callback.Methods.Add(ctor);
                    var invoke = new MethodDefUser("Invoke", MethodSig.CreateInstance(outside.CorLibTypes.Void, importer.Import(typeof(Type[])).ToTypeSig()), dnlib.DotNet.MethodImplAttributes.Runtime,
                        dnlib.DotNet.MethodAttributes.Public | dnlib.DotNet.MethodAttributes.Virtual | dnlib.DotNet.MethodAttributes.NewSlot); callback.Methods.Add(invoke);
                    var selected = new FieldDefUser("Selected", new FieldSig(importer.Import(typeof(Type[])).ToTypeSig()), dnlib.DotNet.FieldAttributes.Public | dnlib.DotNet.FieldAttributes.Static); host.Fields.Add(selected);
                    var receive = Method(host, "Receive", MethodSig.CreateStatic(outside.CorLibTypes.Void, selected.FieldSig.Type)); receive.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0)); receive.Body.Instructions.Add(Instruction.Create(OpCodes.Stsfld, selected)); receive.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                    var target = BrokerMethod(outside, "Consumer", "Host", "SelectWithCallback"); target.MethodSig = MethodSig.CreateStatic(outside.CorLibTypes.Void, outside.CorLibTypes.String, new ClassSig(callback));
                    il.Add(Instruction.Create(OpCodes.Ldstr, "Candidate")); il.Add(Instruction.Create(OpCodes.Ldnull)); il.Add(Instruction.Create(OpCodes.Ldftn, receive)); il.Add(Instruction.Create(OpCodes.Newobj, ctor)); il.Add(Instruction.Create(OpCodes.Call, target)); il.Add(Instruction.Create(OpCodes.Ldsfld, selected));
                }
                else if (access.EndsWith("-token", StringComparison.Ordinal) && access != "type-token")
                {
                    ITypeDefOrRef component = access == "nonbroker-token" ? outside.CorLibTypes.String.TypeDefOrRef : BrokerMethod(outside, "Consumer", "Host", "Run").DeclaringType;
                    TypeSig token = access == "array-token" ? (TypeSig)new SZArraySig(new ClassSig(component)) : new GenericInstSig(new ClassSig(importer.Import(typeof(System.Collections.Generic.List<>))), new ClassSig(component));
                    if (access == "nested-generic-token") token = new GenericInstSig(new ClassSig(importer.Import(typeof(System.Collections.Generic.List<>))), new SZArraySig(token));
                    il.Add(Instruction.Create(OpCodes.Ldtoken, new TypeSpecUser(token))); il.Add(Instruction.Create(OpCodes.Pop)); il.Add(Instruction.Create(OpCodes.Ldnull));
                }
                else if (access.StartsWith("metadata-", StringComparison.Ordinal))
                {
                    var broker = new ClassSig(BrokerMethod(outside, access == "metadata-cross-bootstrap" ? "Forwarder" : "Consumer", access == "metadata-cross-bootstrap" ? "ForwarderHost" : "Host", "Run").DeclaringType);
                    if (access == "metadata-field") host.Fields.Add(new FieldDefUser("BrokerMetadata", new FieldSig(broker), dnlib.DotNet.FieldAttributes.Public));
                    else { var signature = Method(host, "BrokerSignature", MethodSig.CreateStatic(broker)); signature.Body.Instructions.Add(Instruction.Create(OpCodes.Ldnull)); signature.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); }
                    il.Add(Instruction.Create(OpCodes.Ldnull));
                }
                else if (access.StartsWith("output-", StringComparison.Ordinal))
                {
                    string shape = access.Substring(7); var sink = new Local(new SZArraySig(outside.CorLibTypes.Object)); method.Body.Variables.Add(sink);
                    il.Add(Instruction.Create(OpCodes.Ldc_I4_1)); il.Add(Instruction.Create(OpCodes.Newarr, outside.CorLibTypes.Object.TypeDefOrRef)); il.Add(Instruction.Create(OpCodes.Stloc, sink));
                    if (shape == "nested-array")
                    { il.Add(Instruction.Create(OpCodes.Ldloc, sink)); il.Add(Instruction.Create(OpCodes.Ldc_I4_0)); il.Add(Instruction.Create(OpCodes.Ldc_I4_1)); il.Add(Instruction.Create(OpCodes.Newarr, outside.CorLibTypes.Object.TypeDefOrRef)); il.Add(Instruction.Create(OpCodes.Stelem_Ref)); }
                    if (shape == "field")
                    {
                        var holder = new TypeRefUser(outside, "Fixture", "OutputBox", new AssemblyRefUser("Consumer", new Version(1, 0, 0, 0)));
                        il.Add(Instruction.Create(OpCodes.Ldloc, sink)); il.Add(Instruction.Create(OpCodes.Ldc_I4_0)); il.Add(Instruction.Create(OpCodes.Newobj, new MemberRefUser(outside, ".ctor", MethodSig.CreateInstance(outside.CorLibTypes.Void), holder))); il.Add(Instruction.Create(OpCodes.Stelem_Ref));
                    }
                    if (shape == "collection")
                    { il.Add(Instruction.Create(OpCodes.Ldloc, sink)); il.Add(Instruction.Create(OpCodes.Ldc_I4_0)); il.Add(Instruction.Create(OpCodes.Newobj, importer.Import(typeof(System.Collections.ArrayList).GetConstructor(Type.EmptyTypes)))); il.Add(Instruction.Create(OpCodes.Stelem_Ref)); }
                    var target = BrokerMethod(outside, "Consumer", "Host", "SelectInto"); target.MethodSig = MethodSig.CreateStatic(outside.CorLibTypes.Void, outside.CorLibTypes.String, sink.Type);
                    il.Add(Instruction.Create(OpCodes.Ldstr, "Candidate")); il.Add(Instruction.Create(OpCodes.Ldloc, sink)); il.Add(Instruction.Create(OpCodes.Call, target));
                    if (shape == "published-local") il.Add(Instruction.Create(OpCodes.Ldsfld, new MemberRefUser(outside, "Published", new FieldSig(sink.Type), target.DeclaringType)));
                    else il.Add(Instruction.Create(OpCodes.Ldloc, sink));
                    il.Add(Instruction.Create(OpCodes.Ldc_I4_0)); il.Add(Instruction.Create(OpCodes.Ldelem_Ref));
                    if (shape == "nested-array")
                    { il.Add(Instruction.Create(OpCodes.Castclass, sink.Type.ToTypeDefOrRef())); il.Add(Instruction.Create(OpCodes.Ldc_I4_0)); il.Add(Instruction.Create(OpCodes.Ldelem_Ref)); }
                    if (shape == "field")
                    {
                        var holder = new TypeRefUser(outside, "Fixture", "OutputBox", new AssemblyRefUser("Consumer", new Version(1, 0, 0, 0)));
                        il.Add(Instruction.Create(OpCodes.Castclass, holder)); il.Add(Instruction.Create(OpCodes.Ldfld, new MemberRefUser(outside, "Value", new FieldSig(outside.CorLibTypes.Object), holder)));
                    }
                    if (shape == "collection")
                    { il.Add(Instruction.Create(OpCodes.Castclass, importer.Import(typeof(System.Collections.ArrayList)))); il.Add(Instruction.Create(OpCodes.Ldc_I4_0)); il.Add(Instruction.Create(OpCodes.Callvirt, importer.Import(typeof(System.Collections.ArrayList).GetProperty("Item").GetGetMethod()))); }
                    il.Add(Instruction.Create(OpCodes.Castclass, importer.Import(typeof(Type[]))));
                }
                else if (access == "reflection" || access == "type-token")
                {
                    if (access == "reflection")
                    {
                        il.Add(Instruction.Create(OpCodes.Ldstr, "Fixture.Host, Consumer"));
                        il.Add(Instruction.Create(OpCodes.Call, importer.Import(typeof(Type).GetMethod("GetType", new[] { typeof(string) }))));
                    }
                    else
                    {
                        il.Add(Instruction.Create(OpCodes.Ldtoken, BrokerMethod(outside, "Consumer", "Host", "Run").DeclaringType));
                        il.Add(Instruction.Create(OpCodes.Call, importer.Import(typeof(Type).GetMethod("GetTypeFromHandle", new[] { typeof(RuntimeTypeHandle) }))));
                    }
                    il.Add(Instruction.Create(OpCodes.Ldstr, "Run"));
                    il.Add(Instruction.Create(OpCodes.Callvirt, importer.Import(typeof(Type).GetMethod("GetMethod", new[] { typeof(string) }))));
                    il.Add(Instruction.Create(OpCodes.Ldnull)); il.Add(Instruction.Create(OpCodes.Ldnull));
                    il.Add(Instruction.Create(OpCodes.Callvirt, importer.Import(typeof(MethodBase).GetMethod("Invoke", new[] { typeof(object), typeof(object[]) }))));
                    il.Add(Instruction.Create(OpCodes.Castclass, importer.Import(typeof(Type[]))));
                }
                else if (access == "delegate")
                {
                    il.Add(Instruction.Create(OpCodes.Ldnull)); il.Add(Instruction.Create(OpCodes.Ldftn, BrokerMethod(outside, "Consumer", "Host", "Run")));
                    il.Add(Instruction.Create(OpCodes.Newobj, importer.Import(typeof(Func<Type[]>).GetConstructor(new[] { typeof(object), typeof(IntPtr) }))));
                    il.Add(Instruction.Create(OpCodes.Callvirt, importer.Import(typeof(Func<Type[]>).GetMethod("Invoke"))));
                }
                else
                {
                    var target = BrokerMethod(outside, access == "cross-bootstrap" ? "Forwarder" : "Consumer", access == "cross-bootstrap" ? "ForwarderHost" : "Host", access == "direct" ? "Run" : "Forward");
                    if (access == "generic")
                    {
                        target.MethodSig.CallingConvention |= CallingConvention.Generic; target.MethodSig.GenParamCount = 1;
                        il.Add(Instruction.Create(OpCodes.Call, new MethodSpecUser(target, new GenericInstMethodSig(outside.CorLibTypes.Int32))));
                    }
                    else il.Add(Instruction.Create(OpCodes.Call, target));
                }
                il.Add(Instruction.Create(OpCodes.Ret));
            }
            private static void AddOutputBroker(ModuleDefUser module, string shape)
            {
                var host = module.Types.Single(type => type.Name == "Host"); var original = host.Methods.Single(value => value.Name == "Run");
                var array = new SZArraySig(module.CorLibTypes.Object); var method = Method(host, "SelectInto", MethodSig.CreateStatic(module.CorLibTypes.Void, module.CorLibTypes.String, array));
                var il = method.Body.Instructions;
                il.Add(Instruction.Create(OpCodes.Ldarg_1));
                if (shape == "published-local")
                {
                    il.Add(Instruction.Create(OpCodes.Pop)); var local = new Local(array); method.Body.Variables.Add(local);
                    var field = new FieldDefUser("Published", new FieldSig(array), dnlib.DotNet.FieldAttributes.Public | dnlib.DotNet.FieldAttributes.Static); host.Fields.Add(field);
                    il.Add(Instruction.Create(OpCodes.Ldc_I4_1)); il.Add(Instruction.Create(OpCodes.Newarr, module.CorLibTypes.Object.TypeDefOrRef)); il.Add(Instruction.Create(OpCodes.Stloc, local));
                    il.Add(Instruction.Create(OpCodes.Ldloc, local)); il.Add(Instruction.Create(OpCodes.Stsfld, field)); il.Add(Instruction.Create(OpCodes.Ldloc, local));
                }
                if (shape == "return-alias")
                {
                    var alias = Method(host, "ReturnAlias", MethodSig.CreateStatic(array, array)); alias.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0)); alias.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                    il.Add(Instruction.Create(OpCodes.Call, alias));
                }
                if (shape == "alias")
                { var alias = new Local(array); method.Body.Variables.Add(alias); il.Add(Instruction.Create(OpCodes.Stloc, alias)); il.Add(Instruction.Create(OpCodes.Ldloc, alias)); }
                if (shape == "nested-array")
                { il.Add(Instruction.Create(OpCodes.Ldc_I4_0)); il.Add(Instruction.Create(OpCodes.Ldelem_Ref)); il.Add(Instruction.Create(OpCodes.Castclass, array.ToTypeDefOrRef())); }
                if (shape == "writer")
                {
                    var writer = Method(host, "WriteOpaque", MethodSig.CreateStatic(module.CorLibTypes.Void, array, module.CorLibTypes.Object));
                    writer.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0)); writer.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0)); writer.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_1)); writer.Body.Instructions.Add(Instruction.Create(OpCodes.Stelem_Ref)); writer.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                    il.Add(Instruction.Create(OpCodes.Call, original)); il.Add(Instruction.Create(OpCodes.Call, writer));
                }
                else if (shape == "collection")
                {
                    var importer = new Importer(module); il.Add(Instruction.Create(OpCodes.Ldc_I4_0)); il.Add(Instruction.Create(OpCodes.Ldelem_Ref)); il.Add(Instruction.Create(OpCodes.Castclass, importer.Import(typeof(System.Collections.ArrayList))));
                    il.Add(Instruction.Create(OpCodes.Call, original)); il.Add(Instruction.Create(OpCodes.Callvirt, importer.Import(typeof(System.Collections.ArrayList).GetMethod("Add")))); il.Add(Instruction.Create(OpCodes.Pop));
                }
                else if (shape == "field")
                {
                    var box = new TypeDefUser("Fixture", "OutputBox", module.CorLibTypes.Object.TypeDefOrRef) { Attributes = dnlib.DotNet.TypeAttributes.Public }; module.Types.Add(box);
                    var ctor = new MethodDefUser(".ctor", MethodSig.CreateInstance(module.CorLibTypes.Void), dnlib.DotNet.MethodImplAttributes.IL,
                        dnlib.DotNet.MethodAttributes.Public | dnlib.DotNet.MethodAttributes.SpecialName | dnlib.DotNet.MethodAttributes.RTSpecialName) { Body = new CilBody() }; box.Methods.Add(ctor);
                    ctor.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0)); ctor.Body.Instructions.Add(Instruction.Create(OpCodes.Call, new Importer(module).Import(typeof(object).GetConstructor(Type.EmptyTypes)))); ctor.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                    var field = new FieldDefUser("Value", new FieldSig(module.CorLibTypes.Object), dnlib.DotNet.FieldAttributes.Public); box.Fields.Add(field);
                    // The caller supplies a mutable box through its array slot.
                    il.Add(Instruction.Create(OpCodes.Ldc_I4_0)); il.Add(Instruction.Create(OpCodes.Ldelem_Ref)); il.Add(Instruction.Create(OpCodes.Castclass, box));
                    il.Add(Instruction.Create(OpCodes.Call, original)); il.Add(Instruction.Create(OpCodes.Stfld, field));
                }
                else
                {
                    il.Add(Instruction.Create(OpCodes.Ldc_I4_0));
                    if (shape == "byref") il.Add(Instruction.Create(OpCodes.Ldelema, module.CorLibTypes.Object.TypeDefOrRef));
                    il.Add(Instruction.Create(OpCodes.Call, original)); il.Add(Instruction.Create(shape == "byref" ? OpCodes.Stind_Ref : OpCodes.Stelem_Ref));
                }
                il.Add(Instruction.Create(OpCodes.Ret));
            }
            private static void AddPublicationFlow(ModuleDefUser module, string shape)
            {
                var host = module.Types.Single(type => type.Name == "Host"); var array = new SZArraySig(module.CorLibTypes.Object);
                var published = new MemberRefUser(module, "Published", new FieldSig(array), new TypeRefUser(module, "Fixture", "OutsideHost", new AssemblyRefUser("Outside", new Version(1, 0, 0, 0))));
                var publish = Method(host, "Publish", MethodSig.CreateStatic(module.CorLibTypes.Void, array));
                publish.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0)); publish.Body.Instructions.Add(Instruction.Create(OpCodes.Stsfld, published)); publish.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                IMethod selectedPublisher = publish;
                if (shape.StartsWith("multi-", StringComparison.Ordinal) || shape == "return-before")
                {
                    var forward = Method(host, "ForwardPublish", MethodSig.CreateStatic(shape == "return-before" ? (TypeSig)array : module.CorLibTypes.Void, array));
                    forward.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0)); forward.Body.Instructions.Add(Instruction.Create(OpCodes.Call, publish));
                    if (shape == "return-before") forward.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0)); forward.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); selectedPublisher = forward;
                }
                if (shape == "readonly-before" || shape == "readonly-return")
                {
                    var read = Method(host, "ReadOnly", MethodSig.CreateStatic(shape == "readonly-return" ? (TypeSig)array : module.CorLibTypes.Void, array));
                    read.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
                    if (shape == "readonly-before") { read.Body.Instructions.Add(Instruction.Create(OpCodes.Ldlen)); read.Body.Instructions.Add(Instruction.Create(OpCodes.Pop)); }
                    read.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); selectedPublisher = read;
                }
                if (shape == "unknown-before" || shape == "unknown-return")
                {
                    var unknown = new MethodDefUser("UnknownPublish", shape == "unknown-return" ? MethodSig.CreateStatic(array) : MethodSig.CreateStatic(module.CorLibTypes.Void, array), dnlib.DotNet.MethodImplAttributes.Native | dnlib.DotNet.MethodImplAttributes.Unmanaged,
                        dnlib.DotNet.MethodAttributes.Public | dnlib.DotNet.MethodAttributes.Static | dnlib.DotNet.MethodAttributes.PinvokeImpl);
                    unknown.ImplMap = new ImplMapUser(new ModuleRefUser(module, "UnprovenNativePublisher"), "Publish", PInvokeAttributes.CallConvCdecl); host.Methods.Add(unknown); selectedPublisher = unknown;
                }
                var entry = Method(host, "BeginProbe", MethodSig.CreateStatic(module.CorLibTypes.Void)); var local = new Local(array); entry.Body.Variables.Add(local); var il = entry.Body.Instructions;
                il.Add(Instruction.Create(OpCodes.Ldc_I4_1)); il.Add(Instruction.Create(OpCodes.Newarr, module.CorLibTypes.Object.TypeDefOrRef)); il.Add(Instruction.Create(OpCodes.Stloc, local));
                Action write = () => { il.Add(Instruction.Create(OpCodes.Ldloc, local)); il.Add(Instruction.Create(OpCodes.Ldc_I4_0)); il.Add(Instruction.Create(OpCodes.Call, host.Methods.Single(value => value.Name == "Run"))); il.Add(Instruction.Create(OpCodes.Stelem_Ref)); };
                if (shape.EndsWith("-after", StringComparison.Ordinal)) write();
                if (shape == "same-before") { il.Add(Instruction.Create(OpCodes.Ldloc, local)); il.Add(Instruction.Create(OpCodes.Stsfld, published)); }
                else if (shape == "nested-before" || shape == "local-link" || shape == "returned-wrapper" || shape == "related-arguments")
                {
                    var outer = new Local(array); entry.Body.Variables.Add(outer);
                    if (shape == "returned-wrapper")
                    {
                        var wrap = Method(host, "WrapEmpty", MethodSig.CreateStatic(array, array));
                        wrap.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_1)); wrap.Body.Instructions.Add(Instruction.Create(OpCodes.Newarr, module.CorLibTypes.Object.TypeDefOrRef)); wrap.Body.Instructions.Add(Instruction.Create(OpCodes.Dup)); wrap.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0)); wrap.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0)); wrap.Body.Instructions.Add(Instruction.Create(OpCodes.Stelem_Ref)); wrap.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                        il.Add(Instruction.Create(OpCodes.Ldloc, local)); il.Add(Instruction.Create(OpCodes.Call, wrap)); il.Add(Instruction.Create(OpCodes.Stloc, outer));
                    }
                    else
                    {
                        il.Add(Instruction.Create(OpCodes.Ldc_I4_1)); il.Add(Instruction.Create(OpCodes.Newarr, module.CorLibTypes.Object.TypeDefOrRef)); il.Add(Instruction.Create(OpCodes.Stloc, outer));
                        var link = Method(host, "LinkEmpty", MethodSig.CreateStatic(module.CorLibTypes.Void, array, array));
                        link.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0)); link.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0)); link.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_1)); link.Body.Instructions.Add(Instruction.Create(OpCodes.Stelem_Ref)); link.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                        il.Add(Instruction.Create(OpCodes.Ldloc, outer)); il.Add(Instruction.Create(OpCodes.Ldloc, local)); il.Add(Instruction.Create(OpCodes.Call, link));
                    }
                    if (shape == "related-arguments")
                    {
                        var twoPhase = Method(host, "PublishThenWrite", MethodSig.CreateStatic(module.CorLibTypes.Void, array, array, new Importer(module).Import(typeof(Type[])).ToTypeSig()));
                        twoPhase.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0)); twoPhase.Body.Instructions.Add(Instruction.Create(OpCodes.Call, publish));
                        twoPhase.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_1)); twoPhase.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0)); twoPhase.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_2)); twoPhase.Body.Instructions.Add(Instruction.Create(OpCodes.Stelem_Ref)); twoPhase.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                        il.Add(Instruction.Create(OpCodes.Ldloc, outer)); il.Add(Instruction.Create(OpCodes.Ldloc, local)); il.Add(Instruction.Create(OpCodes.Call, host.Methods.Single(value => value.Name == "Run"))); il.Add(Instruction.Create(OpCodes.Call, twoPhase));
                    }
                    else if (shape != "local-link") { il.Add(Instruction.Create(OpCodes.Ldloc, outer)); il.Add(Instruction.Create(OpCodes.Call, publish)); }
                }
                else if (shape == "returned-global")
                {
                    var factory = Method(host, "MakePublished", MethodSig.CreateStatic(array));
                    factory.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_1)); factory.Body.Instructions.Add(Instruction.Create(OpCodes.Newarr, module.CorLibTypes.Object.TypeDefOrRef)); factory.Body.Instructions.Add(Instruction.Create(OpCodes.Dup)); factory.Body.Instructions.Add(Instruction.Create(OpCodes.Call, publish)); factory.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                    il.Add(Instruction.Create(OpCodes.Call, factory)); il.Add(Instruction.Create(OpCodes.Stloc, local));
                }
                else if (shape == "receiver-before" || shape == "constructor-before" || shape == "readonly-constructor")
                {
                    var owner = new TypeDefUser("Fixture", "Publisher", module.CorLibTypes.Object.TypeDefOrRef) { Attributes = dnlib.DotNet.TypeAttributes.Public | dnlib.DotNet.TypeAttributes.Sealed }; module.Types.Add(owner);
                    bool receivesArray = shape != "receiver-before";
                    var ctor = new MethodDefUser(".ctor", receivesArray ? MethodSig.CreateInstance(module.CorLibTypes.Void, array) : MethodSig.CreateInstance(module.CorLibTypes.Void), dnlib.DotNet.MethodImplAttributes.IL,
                        dnlib.DotNet.MethodAttributes.Public | dnlib.DotNet.MethodAttributes.SpecialName | dnlib.DotNet.MethodAttributes.RTSpecialName) { Body = new CilBody() }; owner.Methods.Add(ctor);
                    ctor.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0)); ctor.Body.Instructions.Add(Instruction.Create(OpCodes.Call, new Importer(module).Import(typeof(object).GetConstructor(Type.EmptyTypes))));
                    if (shape == "constructor-before") { ctor.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_1)); ctor.Body.Instructions.Add(Instruction.Create(OpCodes.Call, publish)); }
                    ctor.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                    if (receivesArray) il.Add(Instruction.Create(OpCodes.Ldloc, local)); il.Add(Instruction.Create(OpCodes.Newobj, ctor));
                    if (shape == "receiver-before")
                    {
                        var share = Method(owner, "Share", MethodSig.CreateInstance(module.CorLibTypes.Void, array), true);
                        share.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_1)); share.Body.Instructions.Add(Instruction.Create(OpCodes.Call, publish)); share.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                        il.Add(Instruction.Create(OpCodes.Ldloc, local)); il.Add(Instruction.Create(OpCodes.Callvirt, share));
                    }
                    else il.Add(Instruction.Create(OpCodes.Pop));
                }
                else
                {
                    if (selectedPublisher.MethodSig.Params.Count != 0) il.Add(Instruction.Create(OpCodes.Ldloc, local)); il.Add(Instruction.Create(OpCodes.Call, selectedPublisher));
                    if (selectedPublisher.MethodSig.RetType.ElementType != ElementType.Void) il.Add(Instruction.Create(OpCodes.Stloc, local));
                }
                if (!shape.EndsWith("-after", StringComparison.Ordinal) && shape != "related-arguments") write();
                il.Add(Instruction.Create(OpCodes.Ret));
            }
            private static void AddCallbackBroker(ModuleDefUser module)
            {
                var host = module.Types.Single(type => type.Name == "Host"); var callback = new TypeRefUser(module, "Fixture", "SelectedCallback", new AssemblyRefUser("Outside", new Version(1, 0, 0, 0)));
                var method = Method(host, "SelectWithCallback", MethodSig.CreateStatic(module.CorLibTypes.Void, module.CorLibTypes.String, new ClassSig(callback)));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_1)); method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, host.Methods.Single(value => value.Name == "Run")));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, new MemberRefUser(module, "Invoke", MethodSig.CreateInstance(module.CorLibTypes.Void, new Importer(module).Import(typeof(Type[])).ToTypeSig()), callback))); method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            }
            internal static void AddLocalOutputPlumbing(ModuleDefUser module)
            {
                var host = module.Types.Single(type => type.Name == "Host"); var original = host.Methods.Single(value => value.Name == "Run");
                var array = new SZArraySig(module.CorLibTypes.Object);
                var readonlyMethod = Method(host, "ReadOnly", MethodSig.CreateStatic(module.CorLibTypes.Void, new Importer(module).Import(typeof(Type[])).ToTypeSig(), array));
                readonlyMethod.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0)); readonlyMethod.Body.Instructions.Add(Instruction.Create(OpCodes.Ldlen)); readonlyMethod.Body.Instructions.Add(Instruction.Create(OpCodes.Pop)); readonlyMethod.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                var method = Method(host, "LocalMaterialization", MethodSig.CreateStatic(module.CorLibTypes.Void, array)); var il = method.Body.Instructions;
                var local = new Local(array); method.Body.Variables.Add(local);
                il.Add(Instruction.Create(OpCodes.Ldc_I4_1)); il.Add(Instruction.Create(OpCodes.Newarr, module.CorLibTypes.Object.TypeDefOrRef)); il.Add(Instruction.Create(OpCodes.Stloc, local));
                il.Add(Instruction.Create(OpCodes.Ldloc, local)); il.Add(Instruction.Create(OpCodes.Ldc_I4_0)); il.Add(Instruction.Create(OpCodes.Call, original)); il.Add(Instruction.Create(OpCodes.Stelem_Ref));
                il.Add(Instruction.Create(OpCodes.Ldloc, local)); il.Add(Instruction.Create(OpCodes.Ldc_I4_0)); il.Add(Instruction.Create(OpCodes.Ldelem_Ref)); il.Add(Instruction.Create(OpCodes.Castclass, new Importer(module).Import(typeof(Type[]))));
                il.Add(Instruction.Create(OpCodes.Ldarg_0)); il.Add(Instruction.Create(OpCodes.Call, readonlyMethod));
                var addressLocal = new Local(module.CorLibTypes.Object); method.Body.Variables.Add(addressLocal);
                il.Add(Instruction.Create(OpCodes.Ldloca, addressLocal)); il.Add(Instruction.Create(OpCodes.Call, original)); il.Add(Instruction.Create(OpCodes.Stind_Ref)); il.Add(Instruction.Create(OpCodes.Ldloc, addressLocal)); il.Add(Instruction.Create(OpCodes.Pop)); il.Add(Instruction.Create(OpCodes.Ret));
            }
            internal static void AddScalarProjection(ModuleDefUser module)
            {
                var host = module.Types.Single(type => type.Name == "Host"); var importer = new Importer(module);
                var converter = Method(host, "TypeName", MethodSig.CreateStatic(module.CorLibTypes.String, importer.Import(typeof(Type)).ToTypeSig()));
                converter.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0)); converter.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, importer.Import(typeof(Type).GetProperty("FullName").GetGetMethod()))); converter.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                var recursive = Method(host, "ReadOnlyRecursive", MethodSig.CreateStatic(module.CorLibTypes.Boolean, importer.Import(typeof(Type)).ToTypeSig()));
                var complete = Instruction.Create(OpCodes.Ldc_I4_1); recursive.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0)); recursive.Body.Instructions.Add(Instruction.Create(OpCodes.Brfalse, complete));
                recursive.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0)); recursive.Body.Instructions.Add(Instruction.Create(OpCodes.Callvirt, importer.Import(typeof(Type).GetProperty("DeclaringType").GetGetMethod()))); recursive.Body.Instructions.Add(Instruction.Create(OpCodes.Call, recursive)); recursive.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); recursive.Body.Instructions.Add(complete); recursive.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                var report = Method(host, "ReportScalar", MethodSig.CreateStatic(module.CorLibTypes.Void, importer.Import(typeof(Action<string[]>)).ToTypeSig())); var il = report.Body.Instructions;
                il.Add(Instruction.Create(OpCodes.Call, host.Methods.Single(value => value.Name == "Run"))); il.Add(Instruction.Create(OpCodes.Ldc_I4_0)); il.Add(Instruction.Create(OpCodes.Ldelem_Ref)); il.Add(Instruction.Create(OpCodes.Call, recursive)); il.Add(Instruction.Create(OpCodes.Pop));
                il.Add(Instruction.Create(OpCodes.Ldarg_0)); il.Add(Instruction.Create(OpCodes.Call, host.Methods.Single(value => value.Name == "Run"))); il.Add(Instruction.Create(OpCodes.Ldnull)); il.Add(Instruction.Create(OpCodes.Ldftn, converter));
                il.Add(Instruction.Create(OpCodes.Newobj, importer.Import(typeof(Func<Type, string>).GetConstructor(new[] { typeof(object), typeof(IntPtr) }))));
                var select = typeof(Enumerable).GetMethods().Single(value => value.Name == "Select" && value.GetParameters().Length == 2 && value.GetParameters()[1].ParameterType.GetGenericArguments().Length == 2).MakeGenericMethod(typeof(Type), typeof(string));
                il.Add(Instruction.Create(OpCodes.Call, importer.Import(select))); il.Add(Instruction.Create(OpCodes.Call, importer.Import(typeof(Enumerable).GetMethod("ToArray").MakeGenericMethod(typeof(string)))));
                il.Add(Instruction.Create(OpCodes.Callvirt, importer.Import(typeof(Action<string[]>).GetMethod("Invoke")))); il.Add(Instruction.Create(OpCodes.Ret));
            }
            private static void AddMethod(ModuleDefUser module, TypeDef host, string name, bool indirect)
            {
                var importer = new Importer(module); var method = new MethodDefUser(name, MethodSig.CreateStatic(importer.Import(typeof(Type[])).ToTypeSig()),
                    dnlib.DotNet.MethodImplAttributes.IL, dnlib.DotNet.MethodAttributes.Public | dnlib.DotNet.MethodAttributes.Static) { Body = new CilBody() }; host.Methods.Add(method);
                var il = method.Body.Instructions; il.Add(Instruction.Create(OpCodes.Ldstr, "Candidate"));
                il.Add(Instruction.Create(OpCodes.Call, importer.Import(typeof(Assembly).GetMethod("Load", new[] { typeof(string) }))));
                il.Add(Instruction.Create(indirect ? OpCodes.Ldvirtftn : OpCodes.Callvirt, importer.Import(typeof(Assembly).GetMethod("GetTypes", Type.EmptyTypes))));
                if (indirect) { il.Add(Instruction.Create(OpCodes.Pop)); il.Add(Instruction.Create(OpCodes.Ldnull)); }
                il.Add(Instruction.Create(OpCodes.Ret));
            }
            private static ModuleDefUser Module(string name)
            {
                var module = new ModuleDefUser(name + ".dll", Guid.NewGuid(), new AssemblyRefUser(new AssemblyNameInfo(typeof(object).Assembly.FullName))) { Kind = ModuleKind.Dll };
                new AssemblyDefUser(name, new Version(1, 0, 0, 0)).Modules.Add(module); return module;
            }
            public void Dispose() { if (Set != null) Set.Dispose(); if (Directory.Exists(root)) Directory.Delete(root, true); }
        }
    }
}
