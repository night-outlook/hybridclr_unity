using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using NUnit.Framework;
using UnityEditor;

namespace HybridCLR.Editor.AssemblyShadow.Tests
{
    public sealed class ResourceAbiTests
    {
        [Test]
        public void EcmaSerializableFlagsAndFieldExclusionsAreReadFromRealMetadata()
        {
            using (var fixture = new MetadataFixture())
            {
                var dto = fixture.Type("Dto", true);
                var used = fixture.Field(dto, "Value", fixture.Module.CorLibTypes.Int32);
                fixture.Field(fixture.Root, "Dto", new ClassSig(dto));
                var excluded = fixture.Field(fixture.Root, "Excluded", fixture.Module.CorLibTypes.Int32);
                excluded.IsNotSerialized = true;
                var statik = fixture.Field(fixture.Root, "Static", fixture.Module.CorLibTypes.Int32); statik.IsStatic = true;
                var privateField = fixture.Field(fixture.Root, "Private", fixture.Module.CorLibTypes.Int32); privateField.Attributes = FieldAttributes.Private;
                var hidden = fixture.Field(fixture.Root, "Hidden", fixture.Module.CorLibTypes.Int32); hidden.Attributes = FieldAttributes.Private; fixture.Attribute(hidden, "SerializeField");
                var pure = fixture.Type("PureDto", true); fixture.Field(pure, "Unused", fixture.Module.CorLibTypes.Int32);
                var first = fixture.Analyze();
                Assert.IsEmpty(first.unknowns);
                Assert.That(first.types.Any(t => t.type == "Dto"));
                Assert.IsFalse(first.types.Any(t => t.type == "PureDto"));
                CollectionAssert.AreEquivalent(new[] { "Dto", "Hidden" }, first.types.Single(t => t.type == "Root").fields.Select(f => f.name));
                excluded.FieldSig = new FieldSig(fixture.Module.CorLibTypes.String);
                statik.FieldSig = new FieldSig(fixture.Module.CorLibTypes.String);
                privateField.FieldSig = new FieldSig(fixture.Module.CorLibTypes.String);
                fixture.Field(pure, "AnotherUnused", fixture.Module.CorLibTypes.String);
                Assert.AreEqual(ResourceAbiDiffLevel.None, ResourceAbiDiff.Compare(first, fixture.Analyze()).level);
                used.FieldSig = new FieldSig(fixture.Module.CorLibTypes.String);
                Assert.AreEqual(ResourceAbiDiffLevel.ResourceRebuildRequired, ResourceAbiDiff.Compare(first, fixture.Analyze()).level);
            }
        }

        [Test]
        public void ExactUnityEventLeafIsSupportedButArbitraryEngineClassesRemainRejected()
        {
            using (var fixture = new MetadataFixture())
            {
                var unityEvent = new TypeDefUser("UnityEngine.Events", "UnityEvent", fixture.Engine.CorLibTypes.Object.TypeDefOrRef);
                var arbitrary = new TypeDefUser("UnityEngine.Events", "NotAUnityEvent", fixture.Engine.CorLibTypes.Object.TypeDefOrRef);
                fixture.Engine.Types.Add(unityEvent);
                fixture.Engine.Types.Add(arbitrary);
                fixture.Field(fixture.Root, "Persistent", new ClassSig(new TypeRefUser(fixture.Module,
                    unityEvent.Namespace, unityEvent.Name, fixture.Engine.Assembly.ToAssemblyRef())));
                fixture.Field(fixture.Root, "Arbitrary", new ClassSig(new TypeRefUser(fixture.Module,
                    arbitrary.Namespace, arbitrary.Name, fixture.Engine.Assembly.ToAssemblyRef())));
                ResourceAbiDescriptor descriptor = fixture.Analyze();
                ResourceAbiTypeDescriptor root = descriptor.types.Single(type => type.type == "Root");
                Assert.IsFalse(root.fields.Single(field => field.name == "Persistent").unknown);
                Assert.IsTrue(root.fields.Single(field => field.name == "Arbitrary").unknown);
                Assert.That(descriptor.unknowns, Has.Some.Contains("NotAUnityEvent"));
            }
        }

        [Test]
        public void ListChildAndInheritedDtoAreReachableAndTheirChangesAreDetected()
        {
            using (var fixture = new MetadataFixture())
            {
                var parent = fixture.Type("Parent", true);
                fixture.Field(parent, "Inherited", fixture.Module.CorLibTypes.Int32);
                var child = fixture.Type("Child", true); child.BaseType = parent;
                var list = new ClassSig(new TypeRefUser(fixture.Module, "System.Collections.Generic", "List`1", fixture.Module.CorLibTypes.AssemblyRef));
                fixture.Field(fixture.Root, "Values", new GenericInstSig(list, new ClassSig(child)));
                var before = fixture.Analyze();
                Assert.IsEmpty(before.unknowns);
                Assert.That(before.types.Select(t => t.type), Does.Contain("Child"));
                Assert.That(before.types.Select(t => t.type), Does.Contain("Parent"));
                Assert.That(before.types.Single(t => t.type == "Root").referencedTypeKeys, Does.Contain("fixture:Test:Child"));
                Assert.That(before.types.Single(t => t.type == "Child").fields.Select(f => f.name), Does.Contain("Inherited"));
                fixture.Field(parent, "Added", fixture.Module.CorLibTypes.Int32);
                var diff = ResourceAbiDiff.Compare(before, fixture.Analyze());
                Assert.AreEqual(ResourceAbiDiffLevel.ResourceRebuildRequired, diff.level);
                Assert.That(diff.changedTypes, Does.Contain("fixture:Test:Parent"));
                Assert.That(diff.changedTypes, Does.Contain("fixture:Test:Child"));
            }
        }

        [Test]
        public void ManagedReferenceCandidatesFollowInterfaceInheritanceAndRejectUnityObjects()
        {
            using (var fixture = new MetadataFixture())
            {
                var contract = fixture.Type("IContract"); contract.IsInterface = true; contract.IsAbstract = true; contract.BaseType = null;
                var derived = fixture.Type("IDerived"); derived.IsInterface = true; derived.IsAbstract = true; derived.BaseType = null;
                derived.Interfaces.Add(new InterfaceImplUser(contract));
                var candidate = fixture.Type("Candidate", true); candidate.Interfaces.Add(new InterfaceImplUser(derived));
                fixture.Field(candidate, "Value", fixture.Module.CorLibTypes.Int32);
                var invalid = fixture.Type("NonSerializable"); invalid.Interfaces.Add(new InterfaceImplUser(contract));
                fixture.Root.IsSerializable = true; fixture.Root.Interfaces.Add(new InterfaceImplUser(contract));
                var field = fixture.Field(fixture.Root, "Polymorphic", new ClassSig(contract)); fixture.Attribute(field, "SerializeReference");
                var descriptor = fixture.Analyze();
                Assert.IsEmpty(descriptor.unknowns);
                CollectionAssert.AreEqual(new[] { "fixture:Test:Candidate" }, descriptor.types.Single(t => t.type == "Root").serializeReferenceCandidates);
                Assert.IsFalse(descriptor.types.Any(t => t.type == "NonSerializable"));
                fixture.Field(candidate, "NewValue", fixture.Module.CorLibTypes.String);
                Assert.AreEqual(ResourceAbiDiffLevel.ResourceRebuildRequired, ResourceAbiDiff.Compare(descriptor, fixture.Analyze()).level);
            }
        }

        [Test]
        public void UnsupportedContainersObjectDecimalAndUnmarkedStructFailClosed()
        {
            using (var fixture = new MetadataFixture())
            {
                fixture.Field(fixture.Root, "Grid", new ArraySig(fixture.Module.CorLibTypes.Int32, 2));
                fixture.Field(fixture.Root, "Jagged", new SZArraySig(new SZArraySig(fixture.Module.CorLibTypes.Int32)));
                fixture.Field(fixture.Root, "Object", fixture.Module.CorLibTypes.Object);
                fixture.Field(fixture.Root, "Decimal", new ValueTypeSig(new TypeRefUser(fixture.Module, "System", "Decimal", fixture.Module.CorLibTypes.AssemblyRef)));
                var arbitrary = fixture.Type("Arbitrary"); arbitrary.BaseType = new TypeRefUser(fixture.Module, "System", "ValueType", fixture.Module.CorLibTypes.AssemblyRef);
                fixture.Field(fixture.Root, "Arbitrary", new ValueTypeSig(arbitrary));
                var descriptor = fixture.Analyze();
                Assert.AreEqual(5, descriptor.types.Single(t => t.type == "Root").fields.Count(f => f.unknown));
                Assert.AreEqual(ResourceAbiDiffLevel.UnknownRequiresReview, ResourceAbiDiff.Compare(descriptor, descriptor).level);
            }
        }

        [Test]
        public void UnresolvedSameNamedAssemblyTypeAndEmptyManagedCandidatesFailClosed()
        {
            using (var fixture = new MetadataFixture())
            {
                fixture.Type("Dto", true);
                fixture.Field(fixture.Root, "OtherAssemblyDto", new ClassSig(new TypeRefUser(fixture.Module, "Test", "Dto", new AssemblyRefUser("Other"))));
                var contract = fixture.Type("IEmpty"); contract.IsInterface = true; contract.IsAbstract = true; contract.BaseType = null;
                var field = fixture.Field(fixture.Root, "Empty", new ClassSig(contract)); fixture.Attribute(field, "SerializeReference");
                var descriptor = fixture.Analyze();
                var fields = descriptor.types.Single(t => t.type == "Root").fields;
                Assert.IsTrue(fields.Single(f => f.name == "OtherAssemblyDto").unknown);
                Assert.IsTrue(fields.Single(f => f.name == "Empty").unknown);
                Assert.IsFalse(descriptor.types.Any(t => t.type == "Dto"), "A same-named type in another assembly cannot satisfy this reference.");
            }
        }

        [Test]
        public void RequestedUnityEnginePrefixedCandidateIsNotAFrameworkBoundary()
        {
            using (var fixture = new MetadataFixture())
            {
                fixture.Module.Assembly.Name = "UnityEngine.Gameplay";
                var value = fixture.Field(fixture.Root, "Value", fixture.Module.CorLibTypes.Int32);
                var baseline = fixture.Analyze();
                Assert.IsEmpty(baseline.unknowns);
                Assert.AreEqual(1, baseline.types.Length);
                value.FieldSig = new FieldSig(fixture.Module.CorLibTypes.String);
                var current = fixture.Analyze();
                Assert.AreEqual(ResourceAbiDiffLevel.ResourceRebuildRequired, ResourceAbiDiff.Compare(baseline, current).level);
            }
        }

        [Test]
        public void UnityYamlUsesScriptGuidAndFileIdAndManagedReferenceMetadataOnly()
        {
            const string guid = "0123456789abcdef0123456789abcdef";
            string yaml = "%YAML 1.1\n--- !u!114 &1\nMonoBehaviour:\n  m_Script: {fileID: 321, guid: " + guid + ", type: 3}\n" +
                "  text: |\n    m_Script: {fileID: 999, guid: " + guid + ", type: 3}\n      type: {class: Decoy, ns: Test, asm: other}\n" +
                "  references:\n    version: 2\n    RefIds:\n    - rid: 1\n      type: {class: Outer/Child, ns: Test, asm: Exact.Assembly}\n      data:\n        number: 1\n";
            int calls = 0;
            var parsed = UnitySerializedReferenceParser.Parse(yaml, (actualGuid, fileId) => {
                ++calls; Assert.AreEqual(guid, actualGuid); Assert.AreEqual(321, fileId);
                return new ResourceTypeIdentity { assembly = "Script.Assembly", @namespace = "Test", type = "Owner" };
            });
            Assert.IsEmpty(parsed.unknowns);
            Assert.AreEqual(1, calls);
            Assert.AreEqual(1, parsed.managedReferenceTypes.Length);
            Assert.AreEqual("Exact.Assembly", parsed.managedReferenceTypes[0].assembly);
            Assert.AreEqual("Outer/Child", parsed.managedReferenceTypes[0].type);
            var unresolved = UnitySerializedReferenceParser.Parse(yaml, (actualGuid, fileId) => null);
            Assert.IsNotEmpty(unresolved.unknowns);
            var malformed = UnitySerializedReferenceParser.Parse(yaml.Replace("asm: Exact.Assembly", "asm: "), (actualGuid, fileId) => parsed.scriptTypes[0]);
            Assert.IsNotEmpty(malformed.unknowns);
        }

        [Test]
        public void CallbackBodyChangesRequireReviewButUnchangedCallbacksDoNotBlockCodeOnly()
        {
            using (var fixture = new MetadataFixture())
            {
                fixture.Root.Interfaces.Add(new InterfaceImplUser(new TypeRefUser(fixture.Module, "UnityEngine", "ISerializationCallbackReceiver", fixture.Engine.Assembly.ToAssemblyRef())));
                var beforeMethod = fixture.Callback("OnBeforeSerialize"); fixture.Callback("OnAfterDeserialize");
                var baseline = fixture.Analyze();
                Assert.IsEmpty(baseline.unknowns);
                Assert.AreEqual(ResourceAbiDiffLevel.None, ResourceAbiDiff.Compare(baseline, fixture.Analyze()).level);
                beforeMethod.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Nop));
                Assert.AreEqual(ResourceAbiDiffLevel.UnknownRequiresReview, ResourceAbiDiff.Compare(baseline, fixture.Analyze()).level);
            }
        }

        [Test]
        public void CallbackHashPreservesAdjacentFloatAndDoubleOperandBits()
        {
            using (var fixture = new MetadataFixture())
            {
                fixture.CallbackContract();
                var single = fixture.Field(fixture.Root, "Single", fixture.Module.CorLibTypes.Single);
                var twice = fixture.Field(fixture.Root, "Double", fixture.Module.CorLibTypes.Double);
                var callback = fixture.Callback("OnAfterDeserialize");
                var singleValue = Instruction.Create(OpCodes.Ldc_R4, 1.0f);
                var doubleValue = Instruction.Create(OpCodes.Ldc_R8, 1.0d);
                callback.Body.Instructions.Clear();
                callback.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
                callback.Body.Instructions.Add(singleValue);
                callback.Body.Instructions.Add(Instruction.Create(OpCodes.Stfld, single));
                callback.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
                callback.Body.Instructions.Add(doubleValue);
                callback.Body.Instructions.Add(Instruction.Create(OpCodes.Stfld, twice));
                callback.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                var baseline = fixture.Analyze();
                singleValue.Operand = BitConverter.ToSingle(BitConverter.GetBytes(0x3f800001), 0);
                var changedSingle = fixture.Analyze();
                Assert.AreNotEqual(CallbackHash(baseline), CallbackHash(changedSingle), "Adjacent float constants must not round to the same callback hash.");
                Assert.AreEqual(ResourceAbiDiffLevel.UnknownRequiresReview, ResourceAbiDiff.Compare(baseline, changedSingle).level);
                singleValue.Operand = 1.0f;
                doubleValue.Operand = BitConverter.Int64BitsToDouble(0x3ff0000000000001L);
                var changedDouble = fixture.Analyze();
                Assert.AreNotEqual(CallbackHash(baseline), CallbackHash(changedDouble), "Adjacent double constants must not round to the same callback hash.");
                Assert.AreEqual(ResourceAbiDiffLevel.UnknownRequiresReview, ResourceAbiDiff.Compare(baseline, changedDouble).level);
            }
        }

        [Test]
        public void CallbackStaticFieldReadIncludesHelperTypeInitializer()
        {
            using (var fixture = new MetadataFixture())
            {
                fixture.CallbackContract();
                var value = fixture.Field(fixture.Root, "Value", fixture.Module.CorLibTypes.Int32);
                var helper = fixture.Type("Helper");
                var defaultValue = fixture.Field(helper, "DefaultValue", fixture.Module.CorLibTypes.Int32);
                defaultValue.IsStatic = true;
                var initializer = fixture.StaticInitializer(helper);
                var constant = Instruction.Create(OpCodes.Ldc_I4, 1);
                initializer.Body.Instructions.Clear();
                initializer.Body.Instructions.Add(constant);
                initializer.Body.Instructions.Add(Instruction.Create(OpCodes.Stsfld, defaultValue));
                initializer.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                var callback = fixture.Callback("OnAfterDeserialize");
                callback.Body.Instructions.Clear();
                callback.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
                callback.Body.Instructions.Add(Instruction.Create(OpCodes.Ldsfld, defaultValue));
                callback.Body.Instructions.Add(Instruction.Create(OpCodes.Stfld, value));
                callback.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                var baseline = fixture.Analyze();
                constant.Operand = 2;
                var changed = fixture.Analyze();
                Assert.AreNotEqual(CallbackHash(baseline), CallbackHash(changed), "A callback observes helper initialization even without a direct call to .cctor.");
                Assert.AreEqual(ResourceAbiDiffLevel.UnknownRequiresReview, ResourceAbiDiff.Compare(baseline, changed).level);
                Assert.That(changed.unknowns.Any(s => s.Contains("mutable static")), "Other writes to mutable static state cannot be proved from the callback call graph.");
            }
        }

        [Test]
        public void CallbackPrivateInstanceConstructorStateRequiresReview()
        { AssertUnprovenInstanceConstructorState(FieldAttributes.Private, false); }

        [Test]
        public void CallbackReadonlyInstanceConstructorStateRequiresReview()
        { AssertUnprovenInstanceConstructorState(FieldAttributes.Public | FieldAttributes.InitOnly, false); }

        [Test]
        public void CallbackInheritedInstanceConstructorStateRequiresReview()
        { AssertUnprovenInstanceConstructorState(FieldAttributes.Family, true); }

        [Test]
        public void CallbackNonSerializedInheritedInstanceStateRequiresReview()
        { AssertUnprovenInstanceConstructorState(FieldAttributes.Public | FieldAttributes.NotSerialized, true); }

        private static void AssertUnprovenInstanceConstructorState(FieldAttributes attributes, bool inherited)
        {
            using (var fixture = new MetadataFixture())
            {
                fixture.CallbackContract();
                var owner = fixture.Root;
                if (inherited)
                {
                    owner = fixture.Type("Parent");
                    owner.BaseType = fixture.Root.BaseType;
                    fixture.Root.BaseType = owner;
                }
                var state = fixture.Field(owner, "DefaultValue", fixture.Module.CorLibTypes.Int32);
                state.Attributes = attributes;
                var value = fixture.Field(fixture.Root, "Value", fixture.Module.CorLibTypes.Int32);
                var constructor = new MethodDefUser(".ctor", MethodSig.CreateInstance(fixture.Module.CorLibTypes.Void), MethodImplAttributes.IL,
                    MethodAttributes.Public | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName);
                constructor.Body = new CilBody();
                var initialValue = Instruction.Create(OpCodes.Ldc_I4, 1);
                constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
                constructor.Body.Instructions.Add(initialValue);
                constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Stfld, state));
                constructor.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                owner.Methods.Add(constructor);
                var callback = fixture.Callback("OnAfterDeserialize");
                callback.Body.Instructions.Clear();
                callback.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
                callback.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
                callback.Body.Instructions.Add(Instruction.Create(OpCodes.Ldfld, state));
                callback.Body.Instructions.Add(Instruction.Create(OpCodes.Stfld, value));
                callback.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                var baseline = fixture.Analyze();
                string assemblyBefore = AssemblySemanticHasher.Compute(fixture.Module).semanticHash;
                initialValue.Operand = 2;
                var changed = fixture.Analyze();
                Assert.AreNotEqual(assemblyBefore, AssemblySemanticHasher.Compute(fixture.Module).semanticHash);
                Assert.AreEqual(ResourceAbiDiffLevel.UnknownRequiresReview, ResourceAbiDiff.Compare(baseline, changed).level,
                    "Instance constructors and other writers are not part of the callback call graph.");
                Assert.That(baseline.unknowns.Any(s => s.Contains("unproven instance state")));
                Assert.That(changed.unknowns.Any(s => s.Contains("unproven instance state")));
            }
        }

        [Test]
        public void CallbackCanReadItsExplicitlySerializedPrivateAndInheritedInputs()
        {
            using (var fixture = new MetadataFixture())
            {
                fixture.CallbackContract();
                var parent = fixture.Type("Parent"); parent.BaseType = fixture.Root.BaseType; fixture.Root.BaseType = parent;
                var inherited = fixture.Field(parent, "InheritedInput", fixture.Module.CorLibTypes.Int32);
                inherited.Attributes = FieldAttributes.Family; fixture.Attribute(inherited, "SerializeField");
                var input = fixture.Field(fixture.Root, "PrivateInput", fixture.Module.CorLibTypes.Int32);
                input.Attributes = FieldAttributes.Private; fixture.Attribute(input, "SerializeField");
                var output = fixture.Field(fixture.Root, "Value", fixture.Module.CorLibTypes.Int32);
                var callback = fixture.Callback("OnAfterDeserialize");
                callback.Body.Instructions.Clear();
                callback.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
                callback.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
                callback.Body.Instructions.Add(Instruction.Create(OpCodes.Ldfld, input));
                callback.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
                callback.Body.Instructions.Add(Instruction.Create(OpCodes.Ldfld, inherited));
                callback.Body.Instructions.Add(Instruction.Create(OpCodes.Add));
                callback.Body.Instructions.Add(Instruction.Create(OpCodes.Stfld, output));
                callback.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                var baseline = fixture.Analyze();
                Assert.IsEmpty(baseline.unknowns);
                Assert.AreEqual(ResourceAbiDiffLevel.None, ResourceAbiDiff.Compare(baseline, fixture.Analyze()).level);
            }
        }

        [Test]
        public void ReadonlyStaticInitializationCanRemainStableButModuleChangesCannot()
        {
            using (var fixture = new MetadataFixture())
            {
                fixture.CallbackContract();
                var helper = fixture.Type("Helper");
                var value = fixture.Field(helper, "Value", fixture.Module.CorLibTypes.Int32);
                value.Attributes = FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.InitOnly;
                var initialize = fixture.StaticInitializer(helper);
                initialize.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Ldc_I4, 7));
                initialize.Body.Instructions.Insert(1, Instruction.Create(OpCodes.Stsfld, value));
                var moduleInitialize = fixture.StaticInitializer(fixture.Module.GlobalType);
                var callback = fixture.Callback("OnAfterDeserialize");
                callback.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Ldsfld, value));
                callback.Body.Instructions.Insert(1, Instruction.Create(OpCodes.Pop));
                var baseline = fixture.Analyze();
                Assert.IsEmpty(baseline.unknowns);
                Assert.AreEqual(ResourceAbiDiffLevel.None, ResourceAbiDiff.Compare(baseline, fixture.Analyze()).level);
                moduleInitialize.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Nop));
                var changedModule = fixture.Analyze();
                Assert.AreNotEqual(CallbackHash(baseline), CallbackHash(changedModule));
                Assert.AreEqual(ResourceAbiDiffLevel.UnknownRequiresReview, ResourceAbiDiff.Compare(baseline, changedModule).level);
                moduleInitialize.Body.Instructions.RemoveAt(0);
                helper.IsBeforeFieldInit = !helper.IsBeforeFieldInit;
                Assert.AreNotEqual(CallbackHash(baseline), CallbackHash(fixture.Analyze()), "Type initialization timing is observable to callbacks.");
            }
        }

        [Test]
        public void CallbackMemberFieldSignatureKeepsReferencedAssemblyIdentity()
        {
            using (var fixture = new MetadataFixture())
            {
                fixture.CallbackContract();
                var helper = fixture.Type("Helper");
                var firstType = new ClassSig(new TypeRefUser(fixture.Module, "Same", "Payload", new AssemblyRefUser("First")));
                var secondType = new ClassSig(new TypeRefUser(fixture.Module, "Same", "Payload", new AssemblyRefUser("Second")));
                var field = fixture.Field(helper, "Value", firstType);
                field.Attributes = FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.InitOnly;
                var reference = new MemberRefUser(fixture.Module, "Value", new FieldSig(firstType), helper);
                var callback = fixture.Callback("OnAfterDeserialize");
                callback.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Ldsfld, reference));
                callback.Body.Instructions.Insert(1, Instruction.Create(OpCodes.Pop));
                var baseline = fixture.Analyze();
                string display = reference.FullName;
                field.FieldSig = new FieldSig(secondType); reference.FieldSig = new FieldSig(secondType);
                Assert.AreEqual(display, reference.FullName, "The display name deliberately hides this assembly difference.");
                var changed = fixture.Analyze();
                Assert.AreNotEqual(CallbackHash(baseline), CallbackHash(changed));
                Assert.AreEqual(ResourceAbiDiffLevel.UnknownRequiresReview, ResourceAbiDiff.Compare(baseline, changed).level);
            }
        }

        [Test]
        public void CallbackMethodSpecKeepsGenericArgumentAssemblyIdentity()
        {
            using (var fixture = new MetadataFixture())
            {
                fixture.CallbackContract();
                var helper = fixture.Type("Helper");
                var signature = MethodSig.CreateStatic(fixture.Module.CorLibTypes.Void);
                signature.CallingConvention |= CallingConvention.Generic;
                signature.GenParamCount = 1;
                var generic = new MethodDefUser("Accept", signature, MethodImplAttributes.IL, MethodAttributes.Public | MethodAttributes.Static);
                generic.GenericParameters.Add(new GenericParamUser(0, GenericParamAttributes.NonVariant, "T"));
                generic.Body = new CilBody(); generic.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); helper.Methods.Add(generic);
                var argument = new ClassSig(new TypeRefUser(fixture.Module, "Same", "Payload", new AssemblyRefUser("First")));
                var specification = new MethodSpecUser(generic, new GenericInstMethodSig(argument));
                var callback = fixture.Callback("OnAfterDeserialize");
                callback.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Call, specification));
                var baseline = fixture.Analyze();
                specification.GenericInstMethodSig.GenericArguments[0] = new ClassSig(new TypeRefUser(fixture.Module, "Same", "Payload", new AssemblyRefUser("Second")));
                var changed = fixture.Analyze();
                Assert.AreNotEqual(CallbackHash(baseline), CallbackHash(changed));
                Assert.That(changed.unknowns.Any(s => s.Contains("generic execution")));
            }
        }

        [Test]
        public void CallbackUnresolvedVirtualAndIndirectDispatchRemainUnknown()
        {
            using (var fixture = new MetadataFixture())
            {
                fixture.CallbackContract();
                var callback = fixture.Callback("OnAfterDeserialize");
                var missing = new MemberRefUser(fixture.Module, "Invoke", MethodSig.CreateStatic(fixture.Module.CorLibTypes.Void),
                    new TypeRefUser(fixture.Module, "Absent", "Helper", new AssemblyRefUser("Missing")));
                callback.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Call, missing));
                Assert.That(fixture.Analyze().unknowns.Any(s => s.Contains("method dependency unresolved")));
                callback.Body.Instructions.RemoveAt(0);
                var helper = fixture.Type("Helper");
                var virtualMethod = new MethodDefUser("Invoke", MethodSig.CreateInstance(fixture.Module.CorLibTypes.Void), MethodImplAttributes.IL,
                    MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.NewSlot);
                virtualMethod.Body = new CilBody(); virtualMethod.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); helper.Methods.Add(virtualMethod);
                callback.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Ldnull));
                callback.Body.Instructions.Insert(1, Instruction.Create(OpCodes.Callvirt, virtualMethod));
                Assert.That(fixture.Analyze().unknowns.Any(s => s.Contains("virtual dispatch")));
                callback.Body.Instructions.Clear();
                callback.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0));
                callback.Body.Instructions.Add(Instruction.Create(OpCodes.Conv_I));
                callback.Body.Instructions.Add(Instruction.Create(OpCodes.Calli, MethodSig.CreateStatic(fixture.Module.CorLibTypes.Void)));
                callback.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                Assert.That(fixture.Analyze().unknowns.Any(s => s.Contains("indirect or delegate dispatch")));
            }
        }

        private static string CallbackHash(ResourceAbiDescriptor descriptor)
        { return descriptor.types.Single(t => t.type == "Root").callbackSemanticHash; }

        [Test]
        public void SharedMethodAndFieldWriterExtractionPreservesAssemblyHashBytes()
        {
            using (var fixture = new MetadataFixture())
            {
                var field = fixture.Field(fixture.Root, "Value", fixture.Module.CorLibTypes.Double);
                fixture.Attribute(field, "SerializeField");
                var constant = fixture.Field(fixture.Root, "Constant", fixture.Module.CorLibTypes.Int32);
                constant.Attributes = FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault;
                constant.Constant = new ConstantUser(7);
                var callback = fixture.Callback("OnAfterDeserialize");
                callback.Body.Instructions.Clear();
                callback.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
                callback.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_R8, BitConverter.Int64BitsToDouble(0x3ff0000000000001L)));
                callback.Body.Instructions.Add(Instruction.Create(OpCodes.Stfld, field));
                callback.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                // Captured before extracting the shared method/field writers. This locks the
                // existing assembly-hash schema bytes, not the callback-specific hash version.
                Assert.AreEqual("c3e1b8b1c4c8ddbba1e8dfcc55ed420b3ba0aada311e50ae06c93089384892f0", AssemblySemanticHasher.Compute(fixture.Module).semanticHash);
            }
        }

        [Test]
        public void RealIndexGraphMapsDtoBaseAndManagedCandidateToPrefabAndSceneOnly()
        {
            var root = IndexType("assemblya", "VersionedPrefabComponent", "assemblya:Test:Dto", "assemblya:Test:Base");
            root.serializeReferenceCandidates = new[] { "assemblya:Test:Managed" };
            var descriptor = new ResourceAbiDescriptor(new[] { root, IndexType("assemblya", "Dto"), IndexType("assemblya", "Base"), IndexType("assemblya", "Managed"), IndexType("assemblya", "VersionedData") }, new string[0]);
            var reader = new FakeAssetReader();
            reader.Assets.Add("prefab.prefab", Asset("prefab-guid", "assemblya", "VersionedPrefabComponent"));
            reader.Assets.Add("scene.unity", new ResourceAssetReferences { guid = "scene-guid" });
            reader.Assets.Add("data.asset", Asset("data-guid", "assemblya", "VersionedData"));
            reader.Dependencies.Add("scene.unity", new[] { "prefab.prefab", "VersionedPrefabComponent.cs" });
            var builds = new[] { Build("versioned-prefab", "prefab.prefab"), Build("business-scene", "scene.unity"), Build("versioned-data", "data.asset") };
            var index = AssetScriptReferenceIndexer.Build(builds, descriptor, null, reader);
            Assert.IsFalse(index.hasUnknown, string.Join(";", index.unknowns));
            foreach (string name in new[] { "VersionedPrefabComponent", "Dto", "Base", "Managed" })
            {
                var diff = new ResourceAbiDiff { level = ResourceAbiDiffLevel.ResourceRebuildRequired, changedTypes = new[] { "assemblya:Test:" + name } };
                CollectionAssert.AreEqual(new[] { "business-scene", "versioned-prefab" }, BundleImpactAnalyzer.GetAffectedBundles(diff, index));
                CollectionAssert.AreEqual(new[] { "prefab-guid" }, index.entries.Single(e => e.typeKey == "assemblya:Test:" + name).assetGuids);
            }
            Assert.IsFalse(index.entries.SelectMany(e => e.assetPaths).Any(p => p.EndsWith(".cs", StringComparison.Ordinal)));
        }

        [Test]
        public void IndexUsesExactAssemblyAndCaseAndUnreadableResourcesFailClosed()
        {
            var descriptor = new ResourceAbiDescriptor(new[] { IndexType("a", "Same"), IndexType("b", "Same") }, new string[0]);
            var reader = new FakeAssetReader(); reader.Assets.Add("asset.asset", Asset("guid", "b", "Same"));
            var builds = new[] { Build("one", "asset.asset") };
            var index = AssetScriptReferenceIndexer.Build(builds, descriptor, null, reader);
            CollectionAssert.AreEqual(new[] { "b:Test:Same" }, index.entries.Select(e => e.typeKey));
            reader.Assets["asset.asset"] = Asset("guid", "b", "same");
            Assert.IsTrue(AssetScriptReferenceIndexer.Build(builds, descriptor, null, reader).hasUnknown);
            reader.Assets.Clear();
            index = AssetScriptReferenceIndexer.Build(builds, descriptor, null, reader);
            Assert.IsTrue(index.hasUnknown);
            CollectionAssert.AreEqual(new[] { "one" }, BundleImpactAnalyzer.GetAffectedBundles(new ResourceAbiDiff { level = ResourceAbiDiffLevel.None }, index));
        }

        [Test, Category("UnityAssetDatabase")]
        public void FrozenDemoResourcesUseActualScriptGuidsAndOnlyAffectedBundles()
        {
            const string prefab = "Assets/AssemblyShadowDemo/ResourcesSource/VersionedPrefab.prefab";
            const string scene = "Assets/AssemblyShadowDemo/Scenes/Business.unity";
            const string data = "Assets/AssemblyShadowDemo/ResourcesSource/VersionedData.asset";
            if (!File.Exists(prefab) || !File.Exists(scene) || !File.Exists(data)) Assert.Ignore("The frozen M01 demo assets are not present in this project.");
            var component = new ResourceAbiTypeDescriptor {
                typeKey = "assemblya.implementation.internal:AssemblyA.Implementation.Internal:VersionedPrefabComponent",
                assembly = "AssemblyA.Implementation.Internal", @namespace = "AssemblyA.Implementation.Internal", type = "VersionedPrefabComponent"
            };
            var dataType = new ResourceAbiTypeDescriptor {
                typeKey = "assemblya.implementation.internal:AssemblyA.Implementation.Internal:VersionedScriptableObject",
                assembly = "AssemblyA.Implementation.Internal", @namespace = "AssemblyA.Implementation.Internal", type = "VersionedScriptableObject"
            };
            var descriptor = new ResourceAbiDescriptor(new[] { component, dataType }, new string[0]);
            var index = AssetScriptReferenceIndexer.Build(new[] { Build("versioned-prefab.bundle", prefab), Build("business-scene.bundle", scene), Build("versioned-data.bundle", data) }, descriptor);
            Assert.IsFalse(index.hasUnknown, string.Join(";", index.unknowns));
            var impact = BundleImpactAnalyzer.GetAffectedBundles(new ResourceAbiDiff { level = ResourceAbiDiffLevel.ResourceRebuildRequired, changedTypes = new[] { component.typeKey } }, index);
            CollectionAssert.AreEqual(new[] { "business-scene.bundle", "versioned-prefab.bundle" }, impact);
            var entry = index.entries.Single(e => e.typeKey == component.typeKey);
            CollectionAssert.AreEquivalent(new[] { AssetDatabase.AssetPathToGUID(prefab), AssetDatabase.AssetPathToGUID(scene) }, entry.assetGuids);
            Assert.IsFalse(entry.assetPaths.Any(p => p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)));
        }

        private static AssetBundleBuild Build(string bundle, string path) { return new AssetBundleBuild { assetBundleName = bundle, assetNames = new[] { path } }; }
        private static ResourceAbiTypeDescriptor IndexType(string assembly, string name, params string[] references)
        { return new ResourceAbiTypeDescriptor { typeKey = assembly + ":Test:" + name, assembly = assembly, @namespace = "Test", type = name, referencedTypeKeys = references }; }
        private static ResourceAssetReferences Asset(string guid, string assembly, string name)
        { return new ResourceAssetReferences { guid = guid, scriptTypes = new[] { new ResourceTypeIdentity { assembly = assembly, @namespace = "Test", type = name } } }; }
        private sealed class FakeAssetReader : IResourceAssetReader
        {
            public readonly Dictionary<string, ResourceAssetReferences> Assets = new Dictionary<string, ResourceAssetReferences>();
            public readonly Dictionary<string, string[]> Dependencies = new Dictionary<string, string[]>();
            public string[] GetDependencies(string path) { string[] result; return Dependencies.TryGetValue(path, out result) ? result : new string[0]; }
            public ResourceAssetReferences Read(string path) { return Assets[path]; }
        }

        private sealed class MetadataFixture : IDisposable
        {
            public readonly ModuleDefUser Module = CreateModule("Fixture");
            public readonly ModuleDefUser Engine = CreateModule("UnityEngine.CoreModule");
            public readonly TypeDef Root;
            private readonly string directory = Path.Combine(Path.GetTempPath(), "ResourceAbiTests-" + System.Guid.NewGuid().ToString("N"));
            public MetadataFixture()
            {
                var obj = new TypeDefUser("UnityEngine", "Object", Engine.CorLibTypes.Object.TypeDefOrRef); Engine.Types.Add(obj);
                var mono = new TypeDefUser("UnityEngine", "MonoBehaviour", obj); Engine.Types.Add(mono);
                Root = Type("Root"); Root.BaseType = new TypeRefUser(Module, "UnityEngine", "MonoBehaviour", Engine.Assembly.ToAssemblyRef());
                Directory.CreateDirectory(Path.Combine(directory, "Snapshot"));
                Directory.CreateDirectory(Path.Combine(directory, "References"));
            }
            public TypeDef Type(string name, bool serializable = false)
            {
                var type = new TypeDefUser("Test", name, Module.CorLibTypes.Object.TypeDefOrRef) { Attributes = TypeAttributes.Public };
                type.IsSerializable = serializable; Module.Types.Add(type); return type;
            }
            public FieldDef Field(TypeDef type, string name, TypeSig signature)
            { var field = new FieldDefUser(name, new FieldSig(signature), FieldAttributes.Public); type.Fields.Add(field); return field; }
            public void Attribute(FieldDef field, string name)
            {
                var type = new TypeRefUser(Module, "UnityEngine", name, Engine.Assembly.ToAssemblyRef());
                field.CustomAttributes.Add(new CustomAttribute(new MemberRefUser(Module, ".ctor", MethodSig.CreateInstance(Module.CorLibTypes.Void), type)));
            }
            public MethodDef Callback(string name)
            {
                var method = new MethodDefUser(name, MethodSig.CreateInstance(Module.CorLibTypes.Void), MethodImplAttributes.IL | MethodImplAttributes.Managed, MethodAttributes.Public);
                method.Body = new CilBody(); method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); Root.Methods.Add(method); return method;
            }
            public void CallbackContract()
            { Root.Interfaces.Add(new InterfaceImplUser(new TypeRefUser(Module, "UnityEngine", "ISerializationCallbackReceiver", Engine.Assembly.ToAssemblyRef()))); }
            public MethodDef StaticInitializer(TypeDef type)
            {
                var method = new MethodDefUser(".cctor", MethodSig.CreateStatic(Module.CorLibTypes.Void), MethodImplAttributes.IL | MethodImplAttributes.Managed,
                    MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName);
                method.Body = new CilBody(); method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); type.Methods.Add(method); return method;
            }
            public ResourceAbiDescriptor Analyze()
            {
                string snapshot = Path.Combine(directory, "Snapshot"), references = Path.Combine(directory, "References");
                Module.Write(Path.Combine(snapshot, Module.Assembly.Name + ".dll")); Engine.Write(Path.Combine(references, "UnityEngine.CoreModule.dll"));
                using (var set = DnlibAssemblyLoader.Load(snapshot, new[] { references }, null, false))
                    return UnitySerializedTypeAnalyzer.Analyze(set, new[] { Module.Assembly.Name.String });
            }
            private static ModuleDefUser CreateModule(string name)
            { var module = new ModuleDefUser(name + ".dll") { Kind = ModuleKind.Dll }; new AssemblyDefUser(name, new Version(1, 0, 0, 0)).Modules.Add(module); return module; }
            public void Dispose() { Module.Dispose(); Engine.Dispose(); Directory.Delete(directory, true); }
        }

        [Test]
        public void HashIsDeterministicAndIgnoresOrdering()
        {
            var first = Descriptor(Field("base", "int", "scalar", "SerializeField"));
            var second = Descriptor(Field("base", "int", "scalar", "SerializeField"));
            Assert.AreEqual(ResourceAbiHasher.Compute(first), ResourceAbiHasher.Compute(second));
        }

        [Test]
        public void SerializedFieldChangesRequireResourceRebuild()
        {
            var baseline = Descriptor(Field("value", "int", "scalar", "SerializeField"));
            var current = Descriptor(Field("value", "string", "scalar", "SerializeField"));
            var diff = ResourceAbiDiff.Compare(baseline, current);
            Assert.AreEqual(ResourceAbiDiffLevel.ResourceRebuildRequired, diff.level);
            Assert.Contains("asm::Demo", diff.changedTypes);
        }

        [Test]
        public void NonSerializedAndPrivateWithoutSerializeFieldAreNotAbiEntries()
        {
            var baseline = Descriptor(Field("value", "int", "scalar", "SerializeField"));
            var current = Descriptor(Field("value", "int", "scalar", "SerializeField"));
            var diff = ResourceAbiDiff.Compare(baseline, current);
            Assert.AreEqual(ResourceAbiDiffLevel.None, diff.level);
        }

        [Test]
        public void FormerNameArrayAndManagedReferenceChangesAreDetected()
        {
            var field = Field("values", "List<asm::DemoValue>", "list", "SerializeField");
            field.formerNames = new[] { "oldValues" };
            var baseline = Descriptor(field);
            var changed = Field("values", "List<asm::OtherValue>", "list", "SerializeField");
            changed.formerNames = new[] { "oldValues" };
            var current = Descriptor(changed);
            var diff = ResourceAbiDiff.Compare(baseline, current);
            Assert.AreEqual(ResourceAbiDiffLevel.ResourceRebuildRequired, diff.level);
        }

        [Test]
        public void InheritedNestedArrayAndListFieldsArePartOfTheContract()
        {
            var baseField = Field("baseValue", "int", "scalar", "SerializeField");
            baseField.declaringType = "asm:Demo:Base";
            var nested = Field("nested", "asm:Demo:Outer/Inner", "scalar", "public");
            var array = Field("items", "asm:DemoValue", "array", "public");
            var list = Field("list", "List<asm:DemoValue>", "list", "SerializeField");
            var descriptor = Descriptor(baseField, nested, array, list);
            Assert.AreEqual("asm:Demo:Base", descriptor.types[0].fields[0].declaringType);
            Assert.AreEqual("array", descriptor.types[0].fields[2].shape);
            Assert.AreEqual("list", descriptor.types[0].fields[3].shape);
        }

        [Test]
        public void SerializeReferenceCandidateAndUnknownAreFailClosed()
        {
            var field = Field("polymorphic", "asm:Demo:Base", "scalar", "SerializeReference");
            field.managedReferenceMode = "SerializeReference";
            var baseline = Descriptor(field);
            baseline.types[0].serializeReferenceCandidates = new[] { "asm:Demo:Concrete" };
            var current = Descriptor(field);
            current.types[0].serializeReferenceCandidates = new[] { "asm:Demo:Concrete", "asm:Demo:NewConcrete" };
            var diff = ResourceAbiDiff.Compare(baseline, current);
            Assert.AreEqual(ResourceAbiDiffLevel.ResourceRebuildRequired, diff.level);

            current.unknowns = new[] { "asm:Demo:polymorphic:unsupported" };
            diff = ResourceAbiDiff.Compare(baseline, current);
            Assert.AreEqual(ResourceAbiDiffLevel.UnknownRequiresReview, diff.level);
            Assert.IsTrue(diff.hasUnknown);
        }

        [Test]
        public void ResourceTypeRemovalRequiresRebuild()
        {
            var baseline = Descriptor(Field("value", "int", "scalar", "public"));
            var current = new ResourceAbiDescriptor(new ResourceAbiTypeDescriptor[0], new string[0]);
            var diff = ResourceAbiDiff.Compare(baseline, current);
            Assert.AreEqual(ResourceAbiDiffLevel.ResourceRebuildRequired, diff.level);
            Assert.Contains("asm::Demo", diff.changedTypes);
        }

        [Test]
        public void UnknownTypesFailClosedToAllIndexedBundles()
        {
            var diff = new ResourceAbiDiff { level = ResourceAbiDiffLevel.UnknownRequiresReview, hasUnknown = true };
            var index = new ResourceScriptIndex { bundles = new[] { "prefab", "scene", "unrelated" }, hasUnknown = false };
            CollectionAssert.AreEqual(new[] { "prefab", "scene", "unrelated" }, BundleImpactAnalyzer.GetAffectedBundles(diff, index));
        }

        [Test]
        public void ChangedTypeMapsToOnlyReferencingBundles()
        {
            var diff = new ResourceAbiDiff { level = ResourceAbiDiffLevel.ResourceRebuildRequired, changedTypes = new[] { "asm::Demo" } };
            var index = new ResourceScriptIndex
            {
                bundles = new[] { "prefab", "scene", "unrelated" },
                entries = new[]
                {
                    new ResourceScriptIndexEntry { typeKey = "asm::Demo", bundleNames = new[] { "prefab", "scene" } },
                    new ResourceScriptIndexEntry { typeKey = "asm::Other", bundleNames = new[] { "unrelated" } },
                },
            };
            CollectionAssert.AreEqual(new[] { "prefab", "scene" }, BundleImpactAnalyzer.GetAffectedBundles(diff, index));
        }

        private static ResourceAbiDescriptor Descriptor(params ResourceAbiFieldDescriptor[] fields)
        {
            return new ResourceAbiDescriptor(new[]
            {
                new ResourceAbiTypeDescriptor
                {
                    typeKey = "asm::Demo", assembly = "asm", @namespace = "Demo", type = "Demo",
                    baseChain = "asm::UnityEngine.Object", fields = fields,
                },
            }, new string[0]);
        }

        private static ResourceAbiFieldDescriptor Field(string name, string type, string shape, string flags)
        {
            return new ResourceAbiFieldDescriptor
            {
                declaringType = "asm::Demo", name = name, type = type, shape = shape, flags = flags,
                formerNames = new string[0], managedReferenceMode = string.Empty,
            };
        }
    }
}
