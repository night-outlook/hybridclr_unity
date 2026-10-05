using System;
using System.Linq;
using dnlib.DotNet;

namespace HybridCLR.Editor.AssemblyShadow
{
    /// <summary>Logical CLI shapes. Tokens, RVAs, method slots and addresses never enter this encoding.</summary>
    public static class EvolutionSignature
    {
        private const int MaximumDepth = 128;

        public static string Type(ITypeDefOrRef type) { return Type(type, 0, null); }

        public static string Type(ITypeDefOrRef type, Func<ITypeDefOrRef, string> identity) { return Type(type, 0, identity); }

        private static string Type(ITypeDefOrRef type, int depth, Func<ITypeDefOrRef, string> identity)
        {
            if (type == null) return "<none>";
            ShadowHash.Require(depth < MaximumDepth, "EvolutionSignatureUnsupported", "Recursive type specification.");
            var spec = type as TypeSpec;
            if (spec != null) return Signature(spec.TypeSig, depth + 1, identity);
            var seen = new System.Collections.Generic.HashSet<ITypeDefOrRef>();
            for (var at = type; at != null; at = at.DeclaringType)
                ShadowHash.Require(seen.Add(at) && seen.Count < MaximumDepth, "EvolutionSignatureUnsupported", "Recursive declaration chain.");
            return identity == null ? AssemblyIdentityUtil.TypeKey(type) : identity(type);
        }

        public static string DeclarationKey(TypeDef type)
        {
            ShadowHash.Require(type != null, "EvolutionSignatureUnsupported", "Missing declaration.");
            var chain = new System.Collections.Generic.List<TypeDef>();
            for (var at = type; at != null; at = at.DeclaringType)
            {
                ShadowHash.Require(chain.Count < MaximumDepth && !chain.Contains(at), "EvolutionSignatureUnsupported", "Recursive declaration chain.");
                chain.Add(at);
            }
            chain.Reverse();
            var writer = new CanonicalSignatureWriter();
            writer.Token(AssemblyIdentityUtil.CanonicalName(type.Module.Assembly.Name));
            foreach (var at in chain)
            {
                writer.Token(at.Namespace.String); writer.Token(at.Name.String);
                writer.Token(at.GenericParameters.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            return writer.ToString();
        }

        public static string Signature(TypeSig type) { return Signature(type, 0, null); }

        public static string Signature(TypeSig type, Func<ITypeDefOrRef, string> identity) { return Signature(type, 0, identity); }

        private static string Signature(TypeSig type, int depth, Func<ITypeDefOrRef, string> identity)
        {
            ShadowHash.Require(type != null && depth < MaximumDepth, "EvolutionSignatureUnsupported", "Missing or recursively excessive type signature.");
            var writer = new CanonicalSignatureWriter();
            writer.Token(type.ElementType.ToString());
            // Primitive element codes have intrinsic CLI identities, independent
            // of the compiler's mscorlib/System.Runtime facade reference.
            var primitive = type as CorLibTypeSig;
            var generic = type as GenericInstSig;
            var variable = type as GenericSig;
            var modifier = type as ModifierSig;
            var array = type as ArraySig;
            var function = type as FnPtrSig;
            var definition = type as TypeDefOrRefSig;
            if (primitive != null) { }
            else if (generic != null)
            {
                writer.Token(Signature(generic.GenericType, depth + 1, identity));
                writer.Token(generic.GenericArguments.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                foreach (var argument in generic.GenericArguments) writer.Token(Signature(argument, depth + 1, identity));
            }
            else if (variable != null) writer.Token(variable.Number.ToString(System.Globalization.CultureInfo.InvariantCulture));
            else if (modifier != null)
            {
                ShadowHash.Require(modifier.Modifier != null, "EvolutionSignatureUnsupported", "Missing custom modifier identity.");
                writer.Token(Type(modifier.Modifier, depth + 1, identity)); writer.Token(Signature(modifier.Next, depth + 1, identity));
            }
            else if (array != null)
            {
                writer.Token(array.Rank.ToString(System.Globalization.CultureInfo.InvariantCulture));
                writer.Token(string.Join(",", array.Sizes.Select(n => n.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()));
                writer.Token(string.Join(",", array.LowerBounds.Select(n => n.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray()));
                writer.Token(Signature(array.Next, depth + 1, identity));
            }
            else if (function != null)
            {
                var method = function.Signature as MethodSig;
                ShadowHash.Require(method != null, "EvolutionSignatureUnsupported", "Function pointer has no method signature.");
                writer.Token(Method(method, depth + 1, identity));
            }
            else if (definition != null)
            {
                ShadowHash.Require(definition.TypeDefOrRef != null, "EvolutionSignatureUnsupported", "Missing type identity.");
                writer.Token(Type(definition.TypeDefOrRef, depth + 1, identity));
            }
            else if (type.Next != null) writer.Token(Signature(type.Next, depth + 1, identity));
            else throw new ShadowBuildException("EvolutionSignatureUnsupported", type.ElementType.ToString());
            return writer.ToString();
        }

        public static string Method(MethodSig signature) { return Method(signature, 0, null); }

        private static string Method(MethodSig signature, int depth, Func<ITypeDefOrRef, string> identity)
        {
            ShadowHash.Require(signature != null && depth < MaximumDepth, "EvolutionSignatureUnsupported", "Missing or recursively excessive method signature.");
            var writer = new CanonicalSignatureWriter();
            writer.Token(signature.CallingConvention.ToString());
            writer.Token(signature.GenParamCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
            writer.Token(Signature(signature.RetType, depth + 1, identity));
            writer.Token(signature.Params.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (var parameter in signature.Params) writer.Token(Signature(parameter, depth + 1, identity));
            var optional = signature.ParamsAfterSentinel;
            writer.Token(optional == null ? "0" : optional.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (optional != null) foreach (var parameter in optional) writer.Token(Signature(parameter, depth + 1, identity));
            return writer.ToString();
        }

        public static string Constraints(System.Collections.Generic.IEnumerable<GenericParam> parameters)
        { return Constraints(parameters, null); }

        public static string Constraints(System.Collections.Generic.IEnumerable<GenericParam> parameters, Func<ITypeDefOrRef, string> identity)
        {
            var writer = new CanonicalSignatureWriter();
            var values = parameters.OrderBy(p => p.Number).ToArray();
            writer.Token(values.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            for (int i = 0; i < values.Length; ++i)
            {
                var parameter = values[i];
                ShadowHash.Require(parameter.Number == i, "EvolutionSignatureUnsupported", "Generic parameter numbering must be contiguous and local to its owner.");
                writer.Token(parameter.Flags.ToString());
                var constraints = parameter.GenericParamConstraints.Select(c => Type(c.Constraint, identity)).OrderBy(s => s, StringComparer.Ordinal).ToArray();
                writer.Token(constraints.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
                foreach (var constraint in constraints) writer.Token(constraint);
            }
            return writer.ToString();
        }
    }
}
