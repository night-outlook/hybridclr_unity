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
                Directory.CreateDirectory(directory);
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
            public ResourceAbiDescriptor Analyze()
            {
                Module.Write(Path.Combine(directory, "Fixture.dll")); Engine.Write(Path.Combine(directory, "UnityEngine.CoreModule.dll"));
                using (var set = DnlibAssemblyLoader.Load(directory, null, null, false)) return UnitySerializedTypeAnalyzer.Analyze(set, new[] { "Fixture" });
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
