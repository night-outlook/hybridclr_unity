using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using NUnit.Framework;

namespace HybridCLR.Editor.AssemblyShadow.Tests
{
    public sealed class SignatureHashTests
    {
        [Test] public void ModifiersArrayBoundsAndFunctionPointersRemainSemantic()
        {
            using (var module = ModuleDefMD.Load(File.ReadAllBytes(typeof(SignatureHashTests).Assembly.Location)))
            {
                var type = module.GetTypes().Single(t => t.Name == "SignatureHashTests");
                var modifier = new TypeRefUser(module, "Fixture", "Modifier", module.CorLibTypes.AssemblyRef);
                var field = new FieldDefUser("FixtureField", new FieldSig(new CModReqdSig(modifier, module.CorLibTypes.Int32)), FieldAttributes.Public);
                type.Fields.Add(field);
                string required = Hash(module);
                field.FieldSig = new FieldSig(new CModOptSig(modifier, module.CorLibTypes.Int32));
                Assert.AreNotEqual(required, Hash(module), "modreq and modopt are not interchangeable.");
                var array = new ArraySig(module.CorLibTypes.Int32, 2);
                array.Sizes.Add(3); array.Sizes.Add(4); array.LowerBounds.Add(0); array.LowerBounds.Add(0);
                field.FieldSig = new FieldSig(array);
                string bounded = Hash(module);
                array.LowerBounds[1] = 1;
                Assert.AreNotEqual(bounded, Hash(module), "Array bounds are metadata semantics.");
                field.FieldSig = new FieldSig(new FnPtrSig(MethodSig.CreateStatic(module.CorLibTypes.Int32, module.CorLibTypes.String)));
                string function = Hash(module);
                field.FieldSig = new FieldSig(new FnPtrSig(MethodSig.CreateStatic(module.CorLibTypes.Int64, module.CorLibTypes.String)));
                Assert.AreNotEqual(function, Hash(module), "Function-pointer return type matters.");
            }
        }

        [Test] public void IlCallSitesAndTypeSpecificationsUseRecursiveAssemblyQualifiedSignatures()
        {
            using (var module = Fixture())
            {
                var method = module.GlobalType.Methods[0];
                var one = new TypeRefUser(module, "Fixture", "Shape", new AssemblyRefUser("Provider.One", new Version(1, 0)));
                var two = new TypeRefUser(module, "Fixture", "Shape", new AssemblyRefUser("Provider.Two", new Version(1, 0)));
                var instruction = new Instruction(OpCodes.Calli, MethodSig.CreateStatic(new ClassSig(one)));
                method.Body.Instructions.Insert(0, instruction);
                string before = Hash(module);
                instruction.Operand = MethodSig.CreateStatic(new ClassSig(two));
                Assert.AreNotEqual(before, Hash(module), "calli must preserve the defining assembly of every type.");
                instruction.OpCode = OpCodes.Ldtoken;
                instruction.Operand = new TypeSpecUser(new CModReqdSig(one, module.CorLibTypes.Int32));
                before = Hash(module);
                instruction.Operand = new TypeSpecUser(new CModOptSig(one, module.CorLibTypes.Int32));
                Assert.AreNotEqual(before, Hash(module), "IL TypeSpec operands must preserve custom modifiers.");
            }
        }

        [Test] public void FloatingPointIlPreservesExactBitsIncludingSignedZeroAndNaNs()
        {
            using (var module = Fixture())
            {
                var instruction = new Instruction(OpCodes.Ldc_R4, 1.0000001f);
                module.GlobalType.Methods[0].Body.Instructions.Insert(0, instruction);
                string before = Hash(module);
                instruction.Operand = 1.0000002f;
                Assert.AreNotEqual(before, Hash(module));
                instruction.OpCode = OpCodes.Ldc_R8;
                instruction.Operand = 1.0000000000000002;
                before = Hash(module);
                instruction.Operand = 1.0000000000000004;
                Assert.AreNotEqual(before, Hash(module));
                instruction.Operand = 0.0;
                before = Hash(module);
                instruction.Operand = BitConverter.Int64BitsToDouble(unchecked((long)0x8000000000000000UL));
                Assert.AreNotEqual(before, Hash(module), "Signed zero is observable.");
                instruction.Operand = BitConverter.Int64BitsToDouble(0x7ff8000000000001L);
                before = Hash(module);
                instruction.Operand = BitConverter.Int64BitsToDouble(0x7ff8000000000002L);
                Assert.AreNotEqual(before, Hash(module), "NaN payloads must not collapse.");
            }
        }

        [Test] public void EmbeddedResourceNameContentAndVisibilityAffectSemantics()
        {
            using (var module = Fixture())
            {
                string empty = Hash(module);
                var resource = new EmbeddedResource("Business.config", new byte[] { 1, 2 }, ManifestResourceAttributes.Public);
                module.Resources.Add(resource);
                string before = Hash(module);
                Assert.AreNotEqual(empty, before);
                module.Resources[0] = new EmbeddedResource("Business.config", new byte[] { 1, 3 }, ManifestResourceAttributes.Public);
                Assert.AreNotEqual(before, Hash(module));
                before = Hash(module);
                module.Resources[0].Name = "Other.config";
                Assert.AreNotEqual(before, Hash(module));
                before = Hash(module);
                module.Resources[0].Attributes = ManifestResourceAttributes.Private;
                Assert.AreNotEqual(before, Hash(module));
            }
        }

        [Test] public void FieldMemberReferenceDoesNotLoseItsFieldSignatureThroughIMethod()
        {
            using (var module = Fixture())
            {
                var owner = new TypeRefUser(module, "Fixture", "Other", module.CorLibTypes.AssemblyRef);
                var field = new MemberRefUser(module, "Value", new FieldSig(module.CorLibTypes.Int32), owner);
                module.GlobalType.Methods[0].Body.Instructions.Insert(0, new Instruction(OpCodes.Ldsfld, field));
                string before = Hash(module);
                field.Signature = new FieldSig(module.CorLibTypes.Int64);
                Assert.AreNotEqual(before, Hash(module));
            }
        }

        [Test] public void AttributeNullStringArrayAndBoxedValueKindsRemainDistinct()
        {
            using (var module = Fixture())
            {
                var owner = new TypeRefUser(module, "Fixture", "TestAttribute", module.CorLibTypes.AssemblyRef);
                var constructor = new MemberRefUser(module, ".ctor", MethodSig.CreateInstance(module.CorLibTypes.Void, module.CorLibTypes.String), owner);
                var attribute = new CustomAttribute(constructor);
                attribute.ConstructorArguments.Add(new CAArgument(module.CorLibTypes.String, null));
                module.CustomAttributes.Add(attribute);
                string before = Hash(module);
                attribute.ConstructorArguments[0] = new CAArgument(module.CorLibTypes.String, new UTF8String("null"));
                Assert.AreNotEqual(before, Hash(module));
                var arrayType = new SZArraySig(module.CorLibTypes.String);
                attribute.ConstructorArguments[0] = new CAArgument(arrayType, new List<CAArgument> { new CAArgument(module.CorLibTypes.String, "a,b"), new CAArgument(module.CorLibTypes.String, "c") });
                before = Hash(module);
                attribute.ConstructorArguments[0] = new CAArgument(arrayType, new List<CAArgument> { new CAArgument(module.CorLibTypes.String, "a"), new CAArgument(module.CorLibTypes.String, "b,c") });
                Assert.AreNotEqual(before, Hash(module));
                attribute.ConstructorArguments[0] = new CAArgument(module.CorLibTypes.Object, new CAArgument(module.CorLibTypes.Int32, 1));
                before = Hash(module);
                attribute.ConstructorArguments[0] = new CAArgument(module.CorLibTypes.Object, new CAArgument(module.CorLibTypes.Int64, 1L));
                Assert.AreNotEqual(before, Hash(module));
            }
        }

        [Test] public void TypeForwardingDestinationAndPropertyConstantsAreSemantic()
        {
            using (var module = Fixture())
            {
                var forwarder = new ExportedTypeUser(module, 0, "Fixture", "Forwarded", TypeAttributes.Public | TypeAttributes.Forwarder,
                    new AssemblyRefUser("Provider.One", new Version(1, 0)));
                module.ExportedTypes.Add(forwarder);
                string before = Hash(module);
                forwarder.Implementation = new AssemblyRefUser("Provider.Two", new Version(1, 0));
                Assert.AreNotEqual(before, Hash(module));
                var property = new PropertyDefUser("Answer", PropertySig.CreateStatic(module.CorLibTypes.Int32), PropertyAttributes.HasDefault) { Constant = new ConstantUser(1) };
                module.GlobalType.Properties.Add(property);
                before = Hash(module);
                property.Constant = new ConstantUser(2);
                Assert.AreNotEqual(before, Hash(module));
            }
        }

        [Test] public void AssemblyReferenceRetargetingAndIdentityHashRemainDistinct()
        {
            var reference = new AssemblyRefUser("Provider", new Version(1, 0));
            string before = AssemblyIdentityUtil.AssemblyReferenceKey(reference);
            reference.Attributes = AssemblyAttributes.Retargetable;
            Assert.AreNotEqual(before, AssemblyIdentityUtil.AssemblyReferenceKey(reference));
            before = AssemblyIdentityUtil.AssemblyReferenceKey(reference);
            reference.Hash = new byte[] { 1, 2, 3 };
            Assert.AreNotEqual(before, AssemblyIdentityUtil.AssemblyReferenceKey(reference));
        }

        [Test] public void UnsignedPublicKeysNormalizeMissingAndEmptyBytes()
        {
            using (var module = Fixture())
            {
                string unsigned = Hash(module);
                module.Assembly.PublicKey = new PublicKey(new byte[0]);
                Assert.AreEqual(unsigned, Hash(module), "Missing and empty unsigned keys have the same metadata meaning.");
                module.Assembly.PublicKey = new PublicKey(new byte[] { 1, 2, 3 });
                Assert.AreNotEqual(unsigned, Hash(module), "Nonempty public-key bytes remain part of assembly identity.");
            }
            var reference = new AssemblyRefUser("Provider", new Version(1, 0));
            string unsignedReference = AssemblyIdentityUtil.AssemblyReferenceKey(reference);
            reference.PublicKeyOrToken = new PublicKeyToken(new byte[0]);
            Assert.AreEqual(unsignedReference, AssemblyIdentityUtil.AssemblyReferenceKey(reference));
            reference.PublicKeyOrToken = new PublicKeyToken(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            Assert.AreNotEqual(unsignedReference, AssemblyIdentityUtil.AssemblyReferenceKey(reference));
        }

        private static ModuleDefUser Fixture()
        {
            var module = new ModuleDefUser("Fixture.dll");
            new AssemblyDefUser("Fixture", new Version(1, 0)).Modules.Add(module);
            var method = new MethodDefUser("Probe", MethodSig.CreateStatic(module.CorLibTypes.Void), MethodImplAttributes.IL, MethodAttributes.Public | MethodAttributes.Static) { Body = new CilBody() };
            method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
            module.GlobalType.Methods.Add(method);
            return module;
        }

        private static string Hash(ModuleDef module) { return AssemblySemanticHasher.Compute(module).semanticHash; }
    }
}
