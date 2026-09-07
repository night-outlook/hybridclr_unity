using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using HybridCLR.AssemblyShadow.CodeGen;

namespace HybridCLR.Editor.AssemblyShadow
{
    internal sealed class VerifiedSerializeReferenceDependency
    {
        internal string Consumer;
        internal string CallSite;
        internal ReflectionDependencyEvidence[] ConcreteTypes;
    }

    /// <summary>
    /// Proves a configured SerializeReference field against the exact compiler
    /// module set. The declaration must enumerate the complete assignable,
    /// serializable, non-UnityEngine.Object concrete type domain.
    /// </summary>
    internal static class SerializeReferenceDependencyVerifier
    {
        internal static VerifiedSerializeReferenceDependency[] Verify(CompiledAssemblySet set, ShadowDependencyConfiguration configuration)
        {
            var declarations = configuration == null ? null : configuration.serializeReferenceDependencies;
            declarations = declarations ?? new DeclaredSerializeReferenceDependency[0];
            ShadowHash.Require(configuration == null || configuration.schemaVersion == 1 || configuration.schemaVersion == 2,
                "DependencySchema", "Unsupported explicit dependency schema.");
            ShadowHash.Require(configuration == null || configuration.schemaVersion >= 2 || declarations.Length == 0,
                "DependencySchema", "SerializeReference declarations require dependency schema 2.");
            if (declarations.Length == 0) return new VerifiedSerializeReferenceDependency[0];

            var result = new List<VerifiedSerializeReferenceDependency>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (DeclaredSerializeReferenceDependency declaration in declarations)
            {
                ShadowHash.Require(declaration != null && !string.IsNullOrWhiteSpace(declaration.consumer) &&
                    !string.IsNullOrWhiteSpace(declaration.callSite) && !string.IsNullOrWhiteSpace(declaration.evidence) &&
                    declaration.concreteTypes != null && declaration.concreteTypes.Length > 0,
                    "InvalidSerializeReferenceDependency", "A consumer, Type::field callsite, nonempty concrete type set and evidence are required.");
                string consumer = AssemblyIdentityUtil.CanonicalName(declaration.consumer);
                AssemblyDescriptor descriptor;
                ShadowHash.Require(set.Assemblies.TryGetValue(consumer, out descriptor) &&
                    (descriptor.classification == AssemblyClassification.Runtime || descriptor.classification == AssemblyClassification.NormalHotUpdate),
                    "InvalidSerializeReferenceDependency", "The consumer is not an actual runtime compiler input: " + declaration.consumer);
                ShadowHash.Require(seen.Add(consumer + "\n" + declaration.callSite), "DuplicateSerializeReferenceDependency",
                    declaration.consumer + " -> " + declaration.callSite);

                int separator = declaration.callSite.LastIndexOf("::", StringComparison.Ordinal);
                ShadowHash.Require(separator > 0 && separator + 2 < declaration.callSite.Length &&
                    declaration.callSite.IndexOf("::", StringComparison.Ordinal) == separator,
                    "InvalidSerializeReferenceDependency", "SerializeReference callsite must be an exact Type::field identity: " + declaration.callSite);
                string typeName = declaration.callSite.Substring(0, separator);
                string fieldName = declaration.callSite.Substring(separator + 2);
                ModuleDefMD module = set.GetModule(consumer);
                TypeDef[] owners = module.GetTypes().Where(type => type.FullName == typeName).ToArray();
                ShadowHash.Require(owners.Length == 1, "SerializeReferenceFieldMissing", declaration.consumer + " -> " + declaration.callSite);
                FieldDef[] fields = owners[0].Fields.Where(field => field.Name.String == fieldName).ToArray();
                ShadowHash.Require(fields.Length == 1 && fields[0].CustomAttributes.Count(attribute =>
                    attribute.TypeFullName == "UnityEngine.SerializeReference" && IsUnityAssembly(attribute.AttributeType)) == 1,
                    "SerializeReferenceFieldMissing", declaration.consumer + " -> " + declaration.callSite);
                TypeSig signature = fields[0].FieldType;
                ShadowHash.Require(signature != null && !(signature is ArraySig) && !(signature is SZArraySig) &&
                    !(signature is GenericInstSig) && !(signature is GenericVar) && !(signature is GenericMVar) &&
                    !(signature is PtrSig) && !(signature is ByRefSig),
                    "SerializeReferenceFieldUnsupported", declaration.callSite + " has an unsupported field shape.");
                bool objectTarget = signature.ElementType == ElementType.Object;
                TypeDef fieldType = objectTarget ? null : set.ResolveType(signature.ToTypeDefOrRef());
                ShadowHash.Require(objectTarget || fieldType != null && !fieldType.IsValueType,
                    "SerializeReferenceFieldUnsupported", declaration.callSite + " has an unresolved or value-type target.");

                string[] actual = set.Modules.Values.Distinct().SelectMany(candidate => candidate.GetTypes())
                    .Where(candidate => IsConcreteSerializable(candidate) && !IsCore(candidate) && !IsUnityAssembly(candidate) &&
                        !DerivesFromUnityObject(set, candidate) && (objectTarget || IsAssignable(set, candidate, fieldType)))
                    .Select(AssemblyQualifiedName).Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray();
                string[] declared = declaration.concreteTypes.ToArray();
                foreach (string value in declared) ReflectionBindingConfiguration.ProviderOf(value);
                ShadowHash.Require(declared.Distinct(StringComparer.Ordinal).Count() == declared.Length &&
                    declared.SequenceEqual(declared.OrderBy(value => value, StringComparer.Ordinal), StringComparer.Ordinal),
                    "InvalidSerializeReferenceDependency", declaration.callSite + " concrete types must be unique and ordinally sorted.");
                ShadowHash.Require(actual.SequenceEqual(declared, StringComparer.Ordinal), "SerializeReferenceTypeDomainMismatch",
                    declaration.callSite + " actual [" + string.Join(", ", actual) + "]; declared [" + string.Join(", ", declared) + "].");
                result.Add(new VerifiedSerializeReferenceDependency
                {
                    Consumer = consumer,
                    CallSite = declaration.callSite,
                    ConcreteTypes = actual.Select(value =>
                    {
                        string provider = ReflectionBindingConfiguration.ProviderOf(value);
                        return new ReflectionDependencyEvidence
                        {
                            callSite = declaration.callSite,
                            target = value,
                            provider = AssemblyIdentityUtil.CanonicalName(provider),
                            typeName = value.Substring(0, value.IndexOf(',')),
                            kind = "SerializeReference",
                        };
                    }).ToArray(),
                });
            }
            return result.ToArray();
        }

        private static bool IsConcreteSerializable(TypeDef type)
        {
            return type != null && type.IsSerializable && !type.IsAbstract && !type.IsInterface && !type.IsValueType &&
                !type.HasGenericParameters && !type.IsGlobalModuleType;
        }

        private static bool IsAssignable(CompiledAssemblySet set, TypeDef source, TypeDef target)
        {
            var pending = new Queue<TypeDef>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            pending.Enqueue(source);
            while (pending.Count > 0)
            {
                TypeDef current = pending.Dequeue();
                string key = AssemblyIdentityUtil.TypeKey(current);
                if (!seen.Add(key)) continue;
                ShadowHash.Require(seen.Count <= 1024, "SerializeReferenceTypeGraphUnsupported", "Managed-reference inheritance graph is oversized.");
                if (key == AssemblyIdentityUtil.TypeKey(target)) return true;
                foreach (ITypeDefOrRef relation in current.Interfaces.Select(item => item.Interface)
                    .Concat(current.BaseType == null ? Enumerable.Empty<ITypeDefOrRef>() : new[] { current.BaseType }))
                {
                    if (IsTerminal(relation)) continue;
                    TypeDef resolved = set.ResolveType(relation);
                    ShadowHash.Require(resolved != null, "SerializeReferenceTypeGraphUnresolved", key + " -> " + AssemblyIdentityUtil.TypeKey(relation));
                    pending.Enqueue(resolved);
                }
            }
            return false;
        }

        private static bool DerivesFromUnityObject(CompiledAssemblySet set, TypeDef type)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            TypeDef current = type;
            while (current != null && seen.Add(AssemblyIdentityUtil.TypeKey(current)))
            {
                if (current.FullName == "UnityEngine.Object" && IsUnityAssembly(current)) return true;
                ITypeDefOrRef parent = current.BaseType;
                if (parent == null || IsTerminal(parent)) return false;
                current = set.ResolveType(parent);
                ShadowHash.Require(current != null, "SerializeReferenceTypeGraphUnresolved", AssemblyIdentityUtil.TypeKey(type) + " -> " + AssemblyIdentityUtil.TypeKey(parent));
            }
            ShadowHash.Require(current == null, "SerializeReferenceTypeGraphUnsupported", "Managed-reference base graph contains a cycle.");
            return false;
        }

        private static bool IsTerminal(ITypeDefOrRef type)
        {
            return IsCore(type) && (type.FullName == "System.Object" || type.FullName == "System.ValueType" || type.FullName == "System.Enum");
        }

        private static bool IsCore(ITypeDefOrRef type)
        {
            string assembly = Assembly(type);
            return assembly == "mscorlib" || assembly == "netstandard" || assembly == "system.runtime" || assembly == "system.private.corelib";
        }

        private static bool IsUnityAssembly(ITypeDefOrRef type)
        {
            string assembly = Assembly(type);
            return assembly == "unityengine" || assembly.StartsWith("unityengine.", StringComparison.Ordinal);
        }

        private static string Assembly(ITypeDefOrRef type)
        {
            return type == null || type.DefinitionAssembly == null ? string.Empty : AssemblyIdentityUtil.CanonicalName(type.DefinitionAssembly.Name);
        }

        private static string AssemblyQualifiedName(TypeDef type)
        {
            return type.FullName.Replace('/', '+') + ", " + type.Module.Assembly.Name.String;
        }
    }
}
