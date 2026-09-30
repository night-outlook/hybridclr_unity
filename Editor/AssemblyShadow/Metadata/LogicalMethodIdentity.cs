using System;
using System.Linq;
using dnlib.DotNet;

namespace HybridCLR.Editor.AssemblyShadow
{
    public static class LogicalMethodIdentity
    {
        public const int SchemaVersion = 1;

        public static string Key(MethodDef method)
        {
            ShadowHash.Require(method != null && method.DeclaringType != null && method.MethodSig != null,
                "LogicalMethodIdentity", "A declaring type and complete definition signature are required.");
            ShadowHash.Require(method.IsStatic != method.MethodSig.HasThis, "LogicalMethodIdentity", "Static flag and signature calling convention disagree.");
            ShadowHash.Require(method.GenericParameters.Count == method.MethodSig.GenParamCount, "LogicalMethodIdentity", "Generic arity and signature disagree.");
            var writer = new CanonicalSignatureWriter();
            writer.Token("LogicalMethodKeyV1");
            writer.Token(EvolutionSignature.DeclarationKey(method.DeclaringType));
            writer.Token(method.Name.String);
            writer.Token(EvolutionSignature.Method(method.MethodSig));
            return writer.ToString();
        }

        public static string[] CompatibilityDifferences(MethodDef baseline, MethodDef target)
        {
            ShadowHash.Require(baseline != null && target != null, "MethodCompatibility", "Both physical definitions are required.");
            var reasons = new System.Collections.Generic.List<string>();
            if (Key(baseline) != Key(target)) reasons.Add("LogicalSignature");
            // Visibility, dispatch, static/instance and invocation contract are
            // checked separately from lookup. An equal key is never approval.
            if (baseline.Attributes != target.Attributes) reasons.Add("MethodAttributes");
            const MethodImplAttributes optimizationHints = MethodImplAttributes.NoInlining | MethodImplAttributes.NoOptimization | MethodImplAttributes.AggressiveInlining | (MethodImplAttributes)0x0200;
            if ((baseline.ImplAttributes & ~optimizationHints) != (target.ImplAttributes & ~optimizationHints)) reasons.Add("ImplementationContract");
            if (EvolutionSignature.Constraints(baseline.GenericParameters) != EvolutionSignature.Constraints(target.GenericParameters)) reasons.Add("MethodGenericConstraints");
            if (EvolutionSignature.Constraints(baseline.DeclaringType.GenericParameters) != EvolutionSignature.Constraints(target.DeclaringType.GenericParameters)) reasons.Add("DeclaringTypeGenericConstraints");
            var before = ParameterAttributes(baseline);
            var after = ParameterAttributes(target);
            if (!before.SequenceEqual(after)) reasons.Add("ParameterAttributes");
            return reasons.ToArray();
        }

        private static ushort[] ParameterAttributes(MethodDef method)
        {
            var result = new ushort[method.MethodSig.Params.Count + 1];
            var seen = new System.Collections.Generic.HashSet<int>();
            foreach (var parameter in method.ParamDefs)
            {
                int index = parameter.Sequence;
                ShadowHash.Require(index < result.Length && seen.Add(index), "MethodCompatibility", "Invalid or duplicate parameter row.");
                result[index] = (ushort)parameter.Attributes;
            }
            return result;
        }

        public static MethodDef ResolveCompatible(MethodDef baseline, TypeDef target)
        {
            ShadowHash.Require(target != null, "LogicalMethodIdentity", "An active declaring type is required.");
            string key = Key(baseline);
            var candidates = target.Methods.Where(method => Key(method) == key).ToArray();
            ShadowHash.Require(candidates.Length == 1, candidates.Length == 0 ? "LogicalMethodNotFound" : "LogicalMethodAmbiguous", key);
            string[] differences = CompatibilityDifferences(baseline, candidates[0]);
            ShadowHash.Require(differences.Length == 0, "MethodCompatibility", string.Join(", ", differences));
            return candidates[0];
        }
    }
}
