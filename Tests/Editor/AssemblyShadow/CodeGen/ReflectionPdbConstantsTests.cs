using System;
using System.IO;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Pdb;
using dnlib.DotNet.Writer;
using HybridCLR.AssemblyShadow.CodeGen;
using NUnit.Framework;

namespace HybridCLR.Editor.AssemblyShadow.Tests
{
    public sealed class ReflectionPdbConstantsTests
    {
        private string root, library;
        [SetUp] public void SetUp()
        {
            root = Path.Combine(Path.GetTempPath(), "H1PdbTests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root); library = Path.Combine(root, "PdbEnumLibrary.dll");
            WriteLibrary(library, false);
        }
        [TearDown] public void TearDown() { if (Directory.Exists(root)) Directory.Delete(root, true); }

        [Test] public void ExplicitCompilerReferencesPreserveExternalEnumAndOtherConstants()
        {
            var input = CreateFixture();
            var output = ReflectionBindingTransformer.Transform(input.Pe, input.Pdb, input.Configuration, new[] { library });
            using (var references = new CapturedCompilerReferences(new[] { library }))
            using (var before = Load(input.Pe, input.Pdb, references.Context))
            using (var after = Load(output.PeData, output.PdbData, references.Context))
            {
                Assert.IsNotNull(before.PdbState); Assert.IsNotNull(after.PdbState);
                Assert.AreEqual(PdbFileKind.PortablePDB, before.PdbState.PdbFileKind);
                Assert.AreEqual(PdbFileKind.PortablePDB, after.PdbState.PdbFileKind);
                Assert.AreEqual(ReflectionPdbConstants.Snapshot(before), ReflectionPdbConstants.Snapshot(after));
                Assert.AreEqual(before.Mvid, after.Mvid);
            }
        }
        [Test] public void DnlibAuthoredFixtureDoesNotRequireAFalseCounterexample()
        {
            var input = CreateFixture();
            // dnlib-authored symbols do not reproduce every Roslyn local-constant
            // encoding. Actual compiler captures exercise unresolved/A/B replay.
            var output = ReflectionBindingTransformer.Transform(input.Pe, input.Pdb, input.Configuration);
            using (var before = Load(input.Pe, input.Pdb, null))
            using (var after = Load(output.PeData, output.PdbData, null))
            {
                Assert.AreEqual(ReflectionPdbConstants.Snapshot(before), ReflectionPdbConstants.Snapshot(after));
                Assert.AreEqual(before.Mvid, after.Mvid);
            }
        }
        [Test] public void RouteBIsExplicitAndRetainsEquivalentConstants()
        {
            var input = CreateFixture();
            var a = ReflectionBindingTransformer.Transform(input.Pe, input.Pdb, input.Configuration, new[] { library });
            var b = ReflectionBindingTransformer.Transform(input.Pe, input.Pdb, input.Configuration, new[] { library }, true);
            CollectionAssert.AreEqual(a.PeData, b.PeData); CollectionAssert.AreEqual(a.PdbData, b.PdbData);
        }
        [Test] public void IdenticalInputsAreDeterministic()
        {
            var input = CreateFixture();
            var a = ReflectionBindingTransformer.Transform(input.Pe, input.Pdb, input.Configuration, new[] { library });
            var b = ReflectionBindingTransformer.Transform(input.Pe, input.Pdb, input.Configuration, new[] { library });
            CollectionAssert.AreEqual(a.PeData, b.PeData); CollectionAssert.AreEqual(a.PdbData, b.PdbData);
        }
        [Test] public void ResolverDoesNotSubstituteAnotherAssemblyVersion()
        {
            using (var references = new CapturedCompilerReferences(new[] { library }))
                Assert.IsNull(references.Resolve(new AssemblyRefUser("PdbEnumLibrary", new Version(2, 0, 0, 0)), null));
        }
        [Test] public void ConflictingDuplicateReferenceIdentityFails()
        {
            string other = Path.Combine(root, "other.dll"); WriteLibrary(other, true);
            Assert.AreEqual("AmbiguousCompilerReference", Assert.Throws<ReflectionBindingException>(
                () => { using (new CapturedCompilerReferences(new[] { library, other })) { } }).Code);
        }
        [Test] public void ByteIdenticalReferenceAliasesAreAccepted()
        {
            string other = Path.Combine(root, "same.dll"); File.Copy(library, other);
            using (var references = new CapturedCompilerReferences(new[] { library, other }))
                Assert.IsNotNull(references.Resolve(new AssemblyRefUser("PdbEnumLibrary", new Version(1, 0, 0, 0)), null));
        }
        [Test] public void RelativeCompilerReferencesAreRejected()
        {
            Assert.AreEqual("InvalidCompilerReference", Assert.Throws<ReflectionBindingException>(
                () => { using (new CapturedCompilerReferences(new[] { "relative.dll" })) { } }).Code);
        }
        [Test] public void DisposedResolverCannotBeReused()
        {
            var references = new CapturedCompilerReferences(new[] { library }); references.Dispose();
            Assert.Throws<ObjectDisposedException>(() => references.Resolve(new AssemblyRefUser("PdbEnumLibrary"), null));
        }
        [Test] public void ClassWithNumericValueIsNotRewrittenToNull()
        {
            using (var module = TestModule())
            {
                var type = new TypeDefUser("Fixture", "NotEnum", module.CorLibTypes.Object.TypeDefOrRef); module.Types.Add(type);
                var constant = AttachConstant(module, new ClassSig(type), 1);
                Assert.AreEqual("UnprovenPdbEnumConstant", Assert.Throws<ReflectionBindingException>(() => ReflectionPdbConstants.NormalizeResolvedEnums(module)).Code);
                Assert.AreEqual(1, constant.Value); Assert.IsInstanceOf<ClassSig>(constant.Type);
            }
        }
        [Test] public void EnumUnderlyingValueTypeMustMatchExactly()
        {
            using (var module = TestModule())
            {
                var type = AddEnum(module); var constant = AttachConstant(module, new ClassSig(type), (uint)1);
                Assert.AreEqual("PdbEnumUnderlyingTypeMismatch", Assert.Throws<ReflectionBindingException>(() => ReflectionPdbConstants.NormalizeResolvedEnums(module)).Code);
                Assert.AreEqual((uint)1, constant.Value);
            }
        }
        [Test] public void ResolvedClassSigEnumCanBeNormalizedWithoutChangingValue()
        {
            using (var module = TestModule())
            {
                var type = AddEnum(module); var constant = AttachConstant(module, new ClassSig(type), 1);
                string before = ReflectionPdbConstants.Snapshot(module);
                Assert.AreEqual(1, ReflectionPdbConstants.NormalizeResolvedEnums(module));
                Assert.IsInstanceOf<ValueTypeSig>(constant.Type); Assert.AreEqual(1, constant.Value);
                Assert.AreEqual(before, ReflectionPdbConstants.Snapshot(module));
            }
        }
        [Test] public void NullClassConstantRemainsUntouched()
        {
            using (var module = TestModule())
            {
                var constant = AttachConstant(module, new ClassSig(module.CorLibTypes.Object.TypeDefOrRef), null);
                Assert.AreEqual(0, ReflectionPdbConstants.NormalizeResolvedEnums(module)); Assert.IsNull(constant.Value);
            }
        }
        [Test] public void ConstantValueMutationChangesSnapshot()
        {
            using (var module = TestModule())
            {
                var constant = AttachConstant(module, module.CorLibTypes.Int32, 1);
                string before = ReflectionPdbConstants.Snapshot(module); constant.Value = 2;
                Assert.AreNotEqual(before, ReflectionPdbConstants.Snapshot(module));
            }
        }

        private sealed class Fixture { internal byte[] Pe, Pdb; internal ReflectionBindingConfiguration Configuration; }
        private Fixture CreateFixture()
        {
            var result = new Fixture();
            using (var references = new CapturedCompilerReferences(new[] { library }))
            using (var module = TestModule())
            {
                module.Context = references.Context;
                var host = new TypeDefUser("Fixture", "Host", module.CorLibTypes.Object.TypeDefOrRef); module.Types.Add(host);
                var type = new TypeRefUser(module, "System", "Type", module.CorLibTypes.AssemblyRef);
                var lookup = new MemberRefUser(module, "GetType", MethodSig.CreateStatic(new ClassSig(type), module.CorLibTypes.String), type);
                var method = new MethodDefUser("Run", MethodSig.CreateStatic(new ClassSig(type), module.CorLibTypes.String),
                    MethodImplAttributes.IL, MethodAttributes.Public | MethodAttributes.Static) { Body = new CilBody() };
                host.Methods.Add(method);
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0)); method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, lookup));
                method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                module.CreatePdbState(PdbFileKind.PortablePDB);
                method.Body.PdbMethod = new PdbMethod { Scope = new PdbScope { Start = method.Body.Instructions[0] } };
                var enumReference = new TypeRefUser(module, "Fixture", "ExternalEnum", new AssemblyRefUser("PdbEnumLibrary", new Version(1, 0, 0, 0)));
                method.Body.PdbMethod.Scope.Constants.Add(new PdbConstant("mode", new ValueTypeSig(enumReference), 1));
                method.Body.PdbMethod.Scope.Constants.Add(new PdbConstant("message", module.CorLibTypes.String, "sample"));
                method.Body.PdbMethod.Scope.Constants.Add(new PdbConstant("nullObject", module.CorLibTypes.Object, null));
                using (var pe = new MemoryStream()) using (var pdb = new MemoryStream())
                {
                    var options = new ModuleWriterOptions(module) { WritePdb = true, PdbStream = pdb,
                        PdbFileName = "PdbConsumer.pdb", PdbOptions = PdbWriterOptions.Deterministic };
                    options.PEHeadersOptions.TimeDateStamp = 0; module.Write(pe, options);
                    result.Pe = pe.ToArray(); result.Pdb = pdb.ToArray();
                }
            }
            using (var module = ModuleDefMD.Load(result.Pe))
            {
                var method = module.GetTypes().SelectMany(t => t.Methods).Single();
                result.Configuration = new ReflectionBindingConfiguration { sites = new[] {
                    new ReflectionBindingSite { id = "unit.pdb", assembly = "PdbConsumer", typeName = "Fixture.Host",
                        methodSignature = ReflectionBindingFingerprint.MethodSignature(method), originalMethodHash = ReflectionBindingFingerprint.Compute(method),
                        operationIndex = 1, allowedTypes = new string[0], reason = "Portable PDB enum regression" } } };
            }
            return result;
        }
        private static ModuleDefUser TestModule()
        {
            var module = new ModuleDefUser("PdbConsumer.dll", new Guid("8ba6bb1a-b169-43ba-887a-19b6345d83ec")) { Kind = ModuleKind.Dll };
            new AssemblyDefUser("PdbConsumer", new Version(1, 0, 0, 0)).Modules.Add(module); return module;
        }
        private static TypeDef AddEnum(ModuleDef module)
        {
            var type = new TypeDefUser("Fixture", "ExternalEnum", new TypeRefUser(module, "System", "Enum", module.CorLibTypes.AssemblyRef))
                { Attributes = TypeAttributes.Public | TypeAttributes.Sealed };
            type.Fields.Add(new FieldDefUser("value__", new FieldSig(module.CorLibTypes.Int32), FieldAttributes.Public | FieldAttributes.SpecialName | FieldAttributes.RTSpecialName));
            module.Types.Add(type); return type;
        }
        private static PdbConstant AttachConstant(ModuleDef module, TypeSig type, object value)
        {
            var host = new TypeDefUser("Fixture", "Host", module.CorLibTypes.Object.TypeDefOrRef); module.Types.Add(host);
            var method = new MethodDefUser("Run", MethodSig.CreateStatic(module.CorLibTypes.Void), MethodImplAttributes.IL, MethodAttributes.Static) { Body = new CilBody() };
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret)); host.Methods.Add(method); module.CreatePdbState(PdbFileKind.PortablePDB);
            method.Body.PdbMethod = new PdbMethod { Scope = new PdbScope { Start = method.Body.Instructions[0] } };
            var constant = new PdbConstant("value", type, value); method.Body.PdbMethod.Scope.Constants.Add(constant); return constant;
        }
        private static void WriteLibrary(string path, bool different)
        {
            using (var module = new ModuleDefUser("PdbEnumLibrary.dll") { Kind = ModuleKind.Dll })
            {
                new AssemblyDefUser("PdbEnumLibrary", new Version(1, 0, 0, 0)).Modules.Add(module); AddEnum(module);
                if (different) module.Types.Add(new TypeDefUser("Fixture", "Extra", module.CorLibTypes.Object.TypeDefOrRef));
                module.Write(path);
            }
        }
        private static ModuleDefMD Load(byte[] pe, byte[] pdb, ModuleContext context)
        { return ModuleDefMD.Load(pe, new ModuleCreationOptions { Context = context, PdbFileOrData = pdb, TryToLoadPdbFromDisk = false }); }
    }
}
