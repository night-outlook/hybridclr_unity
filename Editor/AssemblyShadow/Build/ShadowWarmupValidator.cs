using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;

namespace HybridCLR.Editor.AssemblyShadow
{
    /// <summary>Metadata-only proof over the caller's verified, closed input set.</summary>
    public static class ShadowWarmupValidator
    {
        public static ShadowWarmupPlan ValidateAndClone(ShadowWarmupPlan plan, CompiledAssemblySet inputs, IEnumerable<string> closure)
        {
            Require(inputs != null && closure != null, "WarmupInputs", "Verified compiler inputs and closure are required.");
            var result = Clone(plan);
            string[] names = closure.ToArray();
            Require(names.Length > 0 && names.Length <= 4096 && names.All(name => !string.IsNullOrWhiteSpace(name)) &&
                names.Select(AssemblyIdentityUtil.CanonicalName).Distinct(StringComparer.Ordinal).Count() == names.Length,
                "WarmupClosure", "A nonempty unique verified closure is required.");
            var admitted = new HashSet<string>(names.Select(AssemblyIdentityUtil.CanonicalName), StringComparer.Ordinal);
            foreach (string name in names) inputs.Get(name);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in result.types)
            {
                TypeDef type = Owner(inputs, admitted, entry.assembly, entry.type);
                Require(!type.IsInterface && type.Methods.Any(SupportedMethodBody), "WarmupTypeUnsupported", "PreJitClass requires a type with a managed IL body: " + entry.type);
                Require(seen.Add("type\n" + entry.assembly + "\n" + entry.type), "WarmupDuplicate", "Repeated warmup type.");
            }
            foreach (var entry in result.methods)
            {
                TypeDef type = Owner(inputs, admitted, entry.assembly, entry.declaringType);
                Require(entry.name != ".ctor" && entry.name != ".cctor", "WarmupConstructor", "Constructors belong in the type list, not PreJitMethod.");
                TypeDef[] arguments = entry.genericArguments.Select(value => Argument(inputs, value)).ToArray();
                var candidates = type.Methods.Where(method => method.Name == entry.name && method.IsStatic == entry.isStatic &&
                    method.GenericParameters.Count == entry.genericArity && method.MethodSig != null && method.MethodSig.Params.Count == entry.parameterTypes.Length).ToArray();
                var matches = new List<MethodDef>();
                foreach (MethodDef method in candidates)
                {
                    Require(method.MethodSig.GenParamCount == method.GenericParameters.Count && !method.MethodSig.ExplicitThis &&
                        method.MethodSig.HasThis != method.IsStatic && method.MethodSig.CallingConvention ==
                            (method.GenericParameters.Count == 0 ? (method.IsStatic ? CallingConvention.Default : CallingConvention.HasThis) :
                            (method.IsStatic ? CallingConvention.Generic : CallingConvention.Generic | CallingConvention.HasThis)),
                        "WarmupMethodUnsupported", "Unsupported method calling convention: " + method.FullName);
                    var returned = Signature(inputs, method.MethodSig.RetType, arguments, true, 0);
                    var parameters = method.MethodSig.Params.Select(value => Signature(inputs, value, arguments, false, 0)).ToArray();
                    if (Same(returned, entry.returnType) && parameters.Zip(entry.parameterTypes, Same).All(value => value)) matches.Add(method);
                }
                Require(matches.Count == 1, "WarmupMethodResolution", "Missing or ambiguous exact declared method: " + entry.declaringType + "::" + entry.name);
                MethodDef target = matches[0];
                Require(SupportedMethodBody(target), "WarmupMethodUnsupported", "Warmup target must have a managed IL body: " + target.FullName);
                CheckConstraints(inputs, target, arguments);
                string key = "method\n" + entry.assembly + "\n" + target.FullName + "\n" +
                    string.Join("\n", entry.genericArguments.Select(value => value.type + "|" + value.assembly).ToArray());
                Require(seen.Add(key), "WarmupDuplicate", "Repeated closed warmup method.");
            }
            return result;
        }

        private static ShadowWarmupPlan Clone(ShadowWarmupPlan plan)
        {
            Require(plan != null && plan.types != null && plan.methods != null && plan.types.Length + plan.methods.Length > 0 &&
                plan.types.Length <= 1024 && plan.methods.Length <= 1024, "WarmupPlan", "A bounded nonempty warmup plan with explicit arrays is required.");
            var types = plan.types.Select(entry => {
                Require(entry != null, "WarmupPlan", "Null warmup type."); Text(entry.assembly); Text(entry.type);
                return new ShadowWarmupTypeEntry { assembly = entry.assembly, type = entry.type };
            }).ToArray();
            var methods = plan.methods.Select(entry => {
                Require(entry != null && entry.genericArguments != null && entry.parameterTypes != null &&
                    entry.genericArity >= 0 && entry.genericArity <= 128 && entry.genericArguments.Length == entry.genericArity &&
                    entry.parameterTypes.Length <= 1024, "WarmupPlan", "Invalid method arity/arguments/parameters.");
                Text(entry.assembly); Text(entry.declaringType); Text(entry.name);
                return new ShadowWarmupMethodEntry { assembly = entry.assembly, declaringType = entry.declaringType, name = entry.name,
                    isStatic = entry.isStatic, genericArity = entry.genericArity, genericArguments = entry.genericArguments.Select(CloneIdentity).ToArray(),
                    returnType = CloneIdentity(entry.returnType), parameterTypes = entry.parameterTypes.Select(CloneIdentity).ToArray() };
            }).ToArray();
            return new ShadowWarmupPlan { types = types, methods = methods };
        }

        private static ShadowWarmupTypeIdentity CloneIdentity(ShadowWarmupTypeIdentity value)
        {
            Require(value != null, "WarmupPlan", "Null signature identity."); Text(value.assembly); Text(value.type);
            return new ShadowWarmupTypeIdentity { assembly = value.assembly, type = value.type };
        }

        private static TypeDef Owner(CompiledAssemblySet inputs, HashSet<string> closure, string assembly, string name)
        {
            Require(closure.Contains(AssemblyIdentityUtil.CanonicalName(assembly)), "WarmupClosure", "Warmup owner is outside the replacement closure: " + assembly);
            ModuleDefMD module = inputs.GetModule(assembly);
            Require(module.Assembly.Name.String == assembly, "WarmupIdentity", "Use the exact captured owner assembly name.");
            TypeDef type = Find(module, name);
            Require(ClosedDefinition(type), "WarmupTypeUnsupported", "Warmup declaring types must be nongeneric definitions: " + name);
            return type;
        }

        private static TypeDef Find(ModuleDef module, string name)
        {
            var matches = module.GetTypes().Where(type => !type.IsGlobalModuleType && type.ReflectionFullName == name).ToArray();
            Require(matches.Length == 1, "WarmupTypeResolution", "Missing or ambiguous captured type: " + name);
            return matches[0];
        }

        private static bool ClosedDefinition(TypeDef type)
        {
            for (TypeDef at = type; at != null; at = at.DeclaringType) if (at.GenericParameters.Count != 0) return false;
            return type != null && !type.IsGlobalModuleType;
        }

        private static TypeDef Argument(CompiledAssemblySet inputs, ShadowWarmupTypeIdentity identity)
        {
            var modules = inputs.Modules.Values.Where(module => module.Assembly.FullName == identity.assembly).Distinct().ToArray();
            Require(modules.Length == 1, "WarmupIdentity", "Generic argument requires one exact captured full assembly identity: " + identity.assembly);
            TypeDef type = Find(modules[0], identity.type);
            Require(ClosedDefinition(type) && !new[] { "System.Void", "System.TypedReference", "System.ArgIterator", "System.RuntimeArgumentHandle" }.Contains(type.FullName) &&
                !type.CustomAttributes.Any(attribute => attribute.TypeFullName == "System.Runtime.CompilerServices.IsByRefLikeAttribute"),
                "WarmupGenericArgument", "Only concrete nongeneric non-byref-like TypeDefs are supported as generic arguments.");
            return type;
        }

        private static bool SupportedMethodBody(MethodDef method)
        {
            return method != null && method.HasBody && method.Body.Instructions.Count > 0 && method.IsIL && method.IsManaged &&
                !method.IsAbstract && !method.IsPinvokeImpl && !method.IsInternalCall && !method.IsUnmanagedExport;
        }

        private static ShadowWarmupTypeIdentity Signature(CompiledAssemblySet inputs, TypeSig signature, TypeDef[] arguments, bool allowVoid, int depth)
        {
            Require(signature != null && depth <= 64, "WarmupSignature", "Missing or recursive method signature.");
            var parameter = signature as GenericMVar;
            if (parameter != null)
            {
                Require(parameter.Number < arguments.Length, "WarmupSignature", "Open method generic parameter.");
                return Identity(arguments[parameter.Number]);
            }
            if (signature is ByRefSig || signature is SZArraySig || signature is ArraySig)
            {
                Require(!(signature.Next is ByRefSig), "WarmupSignature", "Nested byref signature.");
                var value = Signature(inputs, signature.Next, arguments, false, depth + 1);
                string suffix = signature is ByRefSig ? "&" : "[]";
                var array = signature as ArraySig;
                if (array != null)
                {
                    Require(array.Rank > 0 && array.Rank <= 32 && array.Sizes.Count == 0 &&
                        (array.LowerBounds.Count == 0 || array.LowerBounds.Count == array.Rank && array.LowerBounds.All(bound => bound == 0)),
                        "WarmupSignature", "Unsupported sized/nonzero-bound array signature.");
                    suffix = array.Rank == 1 ? "[*]" : "[" + new string(',', (int)array.Rank - 1) + "]";
                }
                value.type += suffix; return value;
            }
            var generic = signature as GenericInstSig;
            if (generic != null)
            {
                TypeDef definition = Resolve(inputs, generic.GenericType.TypeDefOrRef);
                Require(generic.GenericArguments.Count > 0 && generic.GenericArguments.Count == definition.GenericParameters.Count,
                    "WarmupSignature", "Invalid constructed signature arity.");
                Require(definition.GenericParameters.All(genericParameter => genericParameter.Flags == GenericParamAttributes.NonVariant &&
                    genericParameter.GenericParamConstraints.Count == 0 && genericParameter.CustomAttributes.Count == 0),
                    "WarmupConstraintUnsupported", "Constructed signature constraints require an explicit supported proof.");
                var values = generic.GenericArguments.Select(component => Signature(inputs, component, arguments, false, depth + 1)).ToArray();
                Require(!values.Any(component => component.type.EndsWith("&", StringComparison.Ordinal)), "WarmupSignature", "Byref generic arguments are unsupported.");
                var value = Identity(definition);
                value.type += "[[" + string.Join("],[", values.Select(item => item.type + ", " + item.assembly).ToArray()) + "]]";
                return value;
            }
            Require(signature is TypeDefOrRefSig || signature is CorLibTypeSig, "WarmupSignature", "Unsupported/open signature: " + signature.FullName);
            Require((allowVoid || signature.ElementType != ElementType.Void) && signature.ElementType != ElementType.TypedByRef,
                "WarmupSignature", "Void is only a return type; typed references are unsupported.");
            TypeDef resolved = Resolve(inputs, signature.ToTypeDefOrRef());
            Require(ClosedDefinition(resolved), "WarmupSignature", "Open generic definition in a method signature.");
            return Identity(resolved);
        }

        private static TypeDef Resolve(CompiledAssemblySet inputs, ITypeDefOrRef reference)
        {
            Require(!(reference is TypeSpec), "WarmupConstraintUnsupported", "Constructed constraints require an explicit supported proof.");
            TypeDef result = inputs.ResolveType(reference);
            Require(result != null, "WarmupTypeResolution", "Signature/constraint is not bound to captured metadata: " + (reference == null ? "<null>" : reference.FullName));
            return result;
        }

        private static ShadowWarmupTypeIdentity Identity(TypeDef type)
        { return new ShadowWarmupTypeIdentity { assembly = type.Module.Assembly.FullName, type = type.ReflectionFullName }; }
        private static bool Same(ShadowWarmupTypeIdentity left, ShadowWarmupTypeIdentity right)
        { return left.assembly == right.assembly && left.type == right.type; }

        private static void CheckConstraints(CompiledAssemblySet inputs, MethodDef method, TypeDef[] arguments)
        {
            var parameters = method.GenericParameters.OrderBy(parameter => parameter.Number).ToArray();
            for (int index = 0; index < parameters.Length; ++index)
            {
                var parameter = parameters[index]; TypeDef argument = arguments[index];
                Require(parameter.Number == index && ((int)parameter.Flags & ~0x1c) == 0 && parameter.CustomAttributes.Count == 0,
                    "WarmupConstraintUnsupported", "Unsupported generic parameter metadata/attributes.");
                bool value = argument.IsValueType;
                Require(!parameter.HasReferenceTypeConstraint || !value, "WarmupGenericConstraint", "Reference-type constraint failed.");
                Require(!parameter.HasNotNullableValueTypeConstraint || value, "WarmupGenericConstraint", "Nonnullable value-type constraint failed.");
                Require(!parameter.HasDefaultConstructorConstraint || value || (!argument.IsAbstract && !argument.IsInterface &&
                    argument.Methods.Any(candidate => candidate.IsInstanceConstructor && candidate.IsPublic && candidate.MethodSig != null && candidate.MethodSig.Params.Count == 0)),
                    "WarmupGenericConstraint", "Public default-constructor constraint failed.");
                foreach (var constraint in parameter.GenericParamConstraints)
                    Require(Assignable(inputs, argument, Resolve(inputs, constraint.Constraint)), "WarmupGenericConstraint", "Declared generic constraint failed.");
            }
        }

        private static bool Assignable(CompiledAssemblySet inputs, TypeDef source, TypeDef target)
        {
            var pending = new Queue<TypeDef>(); var seen = new HashSet<TypeDef>(); pending.Enqueue(source);
            while (pending.Count > 0)
            {
                TypeDef type = pending.Dequeue(); if (!seen.Add(type)) continue;
                Require(seen.Count <= 256, "WarmupConstraintUnsupported", "Oversized constraint inheritance graph.");
                if (ReferenceEquals(type, target)) return true;
                if (type.BaseType != null) pending.Enqueue(Resolve(inputs, type.BaseType));
                foreach (var implemented in type.Interfaces) pending.Enqueue(Resolve(inputs, implemented.Interface));
            }
            return false;
        }

        private static void Text(string value)
        { Require(!string.IsNullOrWhiteSpace(value) && value.Length <= 16384 && value.IndexOfAny(new[] { '\0', '\r', '\n' }) < 0, "WarmupPlan", "Missing/oversized/noncanonical identity text."); }
        private static void Require(bool value, string code, string message) { ShadowHash.Require(value, code, message); }
    }
}
