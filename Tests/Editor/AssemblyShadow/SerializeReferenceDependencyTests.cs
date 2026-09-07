using System;
using System.IO;
using dnlib.DotNet;
using NUnit.Framework;

namespace HybridCLR.Editor.AssemblyShadow.Tests
{
    public sealed class SerializeReferenceDependencyTests
    {
        [Test]
        public void ExactSameAssemblyTypeDomainApprovesOnlyItsField()
        {
            string root = Path.Combine(Path.GetTempPath(), "AssemblyShadowSerializeReference-" + Guid.NewGuid().ToString("N"));
            string assemblies = Path.Combine(root, "Assemblies");
            string references = Path.Combine(root, "References");
            Directory.CreateDirectory(assemblies);
            Directory.CreateDirectory(references);
            try
            {
                WriteFixture(Path.Combine(assemblies, "Consumer.dll"));
                File.Copy(typeof(object).Assembly.Location, Path.Combine(references, Path.GetFileName(typeof(object).Assembly.Location)));
                WriteUnityReference(Path.Combine(references, "UnityEngine.CoreModule.dll"));
                var capability = new AssemblyCapability
                {
                    name = "Consumer",
                    classification = AssemblyClassification.Runtime,
                    isShadowCapable = true,
                    capabilityDeclared = true,
                };
                using (CompiledAssemblySet set = DnlibAssemblyLoader.Load(assemblies, new[] { references }, new[] { capability }))
                {
                    ShadowPolicyConfiguration policy = Policy("Fixture.Owner::node",
                        "Fixture.NodeA, Consumer", "Fixture.NodeB, Consumer");
                    ShadowPolicyValidationResult valid = ShadowAssemblyPolicyValidator.ValidateCompiled(set, policy, DateTime.UtcNow);
                    Assert.That(valid.IsValid, Is.True, valid.ToString());

                    policy.dependencies.serializeReferenceDependencies[0].concreteTypes = new[] { "Fixture.NodeA, Consumer" };
                    StringAssert.Contains("SerializeReferenceTypeDomainMismatch",
                        ShadowAssemblyPolicyValidator.ValidateCompiled(set, policy, DateTime.UtcNow).ToString());

                    policy = Policy("Fixture.Owner::other", "Fixture.NodeA, Consumer", "Fixture.NodeB, Consumer");
                    string wrongField = ShadowAssemblyPolicyValidator.ValidateCompiled(set, policy, DateTime.UtcNow).ToString();
                    StringAssert.Contains("SerializeReferenceFieldMissing", wrongField);
                    StringAssert.Contains("UnknownReflectionDependency", wrongField);
                }
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [Test]
        public void SameAssemblyDeclarationIsNotAFalseGraphEdge()
        {
            var descriptor = new AssemblyDescriptor
            {
                name = "Consumer",
                classification = AssemblyClassification.Runtime,
                isShadowCapable = true,
                capabilityDeclared = true,
                references = new string[0],
            };
            var configuration = Policy("Fixture.Owner::node", "Fixture.NodeA, Consumer").dependencies;
            Assert.That(new AssemblyReferenceGraph(new[] { descriptor }, configuration).Edges, Is.Empty);
        }

        private static ShadowPolicyConfiguration Policy(string callSite, params string[] concreteTypes)
        {
            return new ShadowPolicyConfiguration
            {
                assemblies = new[]
                {
                    new AssemblyCapability
                    {
                        name = "Consumer",
                        classification = AssemblyClassification.Runtime,
                        isShadowCapable = true,
                        capabilityDeclared = true,
                    },
                },
                dependencies = new ShadowDependencyConfiguration
                {
                    schemaVersion = 2,
                    serializeReferenceDependencies = new[]
                    {
                        new DeclaredSerializeReferenceDependency
                        {
                            consumer = "Consumer",
                            callSite = callSite,
                            concreteTypes = concreteTypes,
                            evidence = "Exact compiler-derived fixture domain",
                        },
                    },
                },
            };
        }

        private static void WriteFixture(string path)
        {
            using (var module = new ModuleDefUser("Consumer.dll", Guid.NewGuid(),
                new AssemblyRefUser(new AssemblyNameInfo(typeof(object).Assembly.FullName))) { Kind = ModuleKind.Dll })
            {
                var assembly = new AssemblyDefUser("Consumer", new Version(1, 0, 0, 0));
                assembly.Modules.Add(module);
                var contract = new TypeDefUser("Fixture", "INode", null)
                {
                    Attributes = dnlib.DotNet.TypeAttributes.Public | dnlib.DotNet.TypeAttributes.Interface | dnlib.DotNet.TypeAttributes.Abstract,
                };
                var first = SerializableType(module, contract, "NodeA");
                var second = SerializableType(module, contract, "NodeB");
                var owner = new TypeDefUser("Fixture", "Owner", module.CorLibTypes.Object.TypeDefOrRef)
                {
                    Attributes = dnlib.DotNet.TypeAttributes.Public | dnlib.DotNet.TypeAttributes.Class,
                };
                var field = new FieldDefUser("node", new FieldSig(new ClassSig(contract)), dnlib.DotNet.FieldAttributes.Private);
                var serializeReference = new TypeRefUser(module, "UnityEngine", "SerializeReference", new AssemblyRefUser("UnityEngine.CoreModule"));
                field.CustomAttributes.Add(new CustomAttribute(new MemberRefUser(module, ".ctor",
                    MethodSig.CreateInstance(module.CorLibTypes.Void), serializeReference)));
                owner.Fields.Add(field);
                module.Types.Add(contract);
                module.Types.Add(first);
                module.Types.Add(second);
                module.Types.Add(owner);
                module.Write(path);
            }
        }

        private static TypeDef SerializableType(ModuleDef module, TypeDef contract, string name)
        {
            var type = new TypeDefUser("Fixture", name, module.CorLibTypes.Object.TypeDefOrRef)
            {
                Attributes = dnlib.DotNet.TypeAttributes.Public | dnlib.DotNet.TypeAttributes.Class | dnlib.DotNet.TypeAttributes.Serializable,
            };
            type.Interfaces.Add(new InterfaceImplUser(contract));
            return type;
        }

        private static void WriteUnityReference(string path)
        {
            using (var module = new ModuleDefUser("UnityEngine.CoreModule.dll") { Kind = ModuleKind.Dll })
            {
                var assembly = new AssemblyDefUser("UnityEngine.CoreModule", new Version(0, 0, 0, 0));
                assembly.Modules.Add(module);
                module.Write(path);
            }
        }
    }
}
