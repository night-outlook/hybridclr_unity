using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;

namespace HybridCLR.Editor.AssemblyShadow
{
    [Serializable]
    public sealed class NativeLayoutTypeAdmission
    {
        public string typeKey;
        public string decision;
        public bool metadataChanged;
        public bool prePublicationNativeProofRequired;
        public string[] reasons;
    }

    [Serializable]
    public sealed class NativeLayoutAdmissionReport
    {
        public int schemaVersion = 1;
        public string profile = "NativeLayoutAdmissionV1";
        public string assembly;
        public string baselineDllSha256;
        public string targetDllSha256;
        public bool editorAccepted;
        public bool nativeProofExecuted;
        public bool allocationProofStillRequired = true;
        public bool pureInterpreterExpansionEnabled;
        public NativeLayoutTypeAdmission[] types;

        public void RequireEditorAdmission()
        {
            ShadowHash.Require(editorAccepted, "NativeLayoutIncompatible", string.Join("; ",
                types.Where(t => t.decision == "Rejected").Select(t => t.typeKey + ": " + string.Join(", ", t.reasons)).ToArray()));
        }
    }

    /// <summary>
    /// Metadata-only V1 screen. A positive result is NeedsNativeProof, never a
    /// platform offset or allocation certificate. Resource ABI is independent.
    /// No Assembly.Load, reflection execution, attribute construction or cctor.
    /// </summary>
    public static class NativeLayoutAdmissionValidator
    {
        public static NativeLayoutAdmissionReport Analyze(byte[] baselineBytes, byte[] targetBytes)
        {
            ShadowHash.Require(baselineBytes != null && targetBytes != null, "NativeLayoutInput", "Both actual DLL byte inputs are required.");
            using (var baseline = ModuleDefMD.Load(baselineBytes))
            using (var target = ModuleDefMD.Load(targetBytes))
            {
                ShadowHash.Require(baseline.Assembly != null && target.Assembly != null &&
                    AssemblyIdentityUtil.CanonicalName(baseline.Assembly.Name) == AssemblyIdentityUtil.CanonicalName(target.Assembly.Name),
                    "NativeLayoutInput", "V1 compares the same baseline-backed logical assembly.");
                var before = Index(baseline);
                var after = Index(target);
                var rows = new List<NativeLayoutTypeAdmission>();
                foreach (var pair in after.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    TypeDef oldType;
                    if (!before.TryGetValue(pair.Key, out oldType))
                    {
                        rows.Add(new NativeLayoutTypeAdmission { typeKey = pair.Key, decision = "NoBaselineCounterpart", metadataChanged = true,
                            prePublicationNativeProofRequired = false, reasons = new[] { "Added type in an existing assembly; native capability, resources and allocation guards still apply." } });
                        continue;
                    }
                    rows.Add(Compare(oldType, pair.Value));
                }
                foreach (var pair in before.Where(p => !after.ContainsKey(p.Key)).OrderBy(p => p.Key, StringComparer.Ordinal))
                    rows.Add(new NativeLayoutTypeAdmission { typeKey = pair.Key, decision = "NoActiveCounterpart", metadataChanged = true,
                        prePublicationNativeProofRequired = false, reasons = new[] { "Old type lookup/execution must reject; this is not assembly Remove support or resource compatibility." } });
                return new NativeLayoutAdmissionReport { assembly = target.Assembly.Name, baselineDllSha256 = ShadowHash.Bytes(baselineBytes),
                    targetDllSha256 = ShadowHash.Bytes(targetBytes), editorAccepted = rows.All(r => r.decision != "Rejected"),
                    nativeProofExecuted = false, pureInterpreterExpansionEnabled = false, types = rows.ToArray() };
            }
        }

        private static Dictionary<string, TypeDef> Index(ModuleDef module)
        {
            var result = new Dictionary<string, TypeDef>(StringComparer.Ordinal);
            foreach (var type in module.GetTypes())
            {
                // Validate declaration depth before TypeKey traverses the chain.
                EvolutionSignature.DeclarationKey(type);
                string key = AssemblyIdentityUtil.TypeKey(type);
                ShadowHash.Require(!result.ContainsKey(key), "NativeLayoutInput", "Duplicate logical type definition: " + key);
                result.Add(key, type);
            }
            return result;
        }

        private static NativeLayoutTypeAdmission Compare(TypeDef baseline, TypeDef target)
        {
            var rejected = new List<string>();
            var proof = new List<string>();
            if (Kind(baseline) != Kind(target)) rejected.Add("TypeKindChanged");
            if (EvolutionSignature.DeclarationKey(baseline) != EvolutionSignature.DeclarationKey(target)) rejected.Add("DeclarationArityChanged");
            if (IsByRefLike(baseline) != IsByRefLike(target)) rejected.Add("ByRefLikeChanged");
            if (EvolutionSignature.Type(baseline.BaseType) != EvolutionSignature.Type(target.BaseType)) rejected.Add("ParentChanged");
            if (!Interfaces(baseline).SequenceEqual(Interfaces(target))) rejected.Add("InterfacesChanged");
            if (EvolutionSignature.Constraints(baseline.GenericParameters) != EvolutionSignature.Constraints(target.GenericParameters)) rejected.Add("GenericConstraintsChanged");
            if ((baseline.Attributes & TypeAttributes.LayoutMask) != (target.Attributes & TypeAttributes.LayoutMask)) rejected.Add("LayoutKindChanged");
            if (baseline.PackingSize != target.PackingSize) proof.Add("EffectiveNativePackingRequired");
            if (baseline.ClassSize != target.ClassSize) proof.Add("NativeInstanceSizeRequired");

            var oldFields = baseline.Fields.Where(f => !f.IsStatic).ToArray();
            var newFields = target.Fields.Where(f => !f.IsStatic).ToArray();
            if (newFields.Length < oldFields.Length) rejected.Add("InstanceFieldRemoved");
            for (int i = 0; i < Math.Min(oldFields.Length, newFields.Length); ++i)
            {
                var oldField = oldFields[i]; var newField = newFields[i];
                if (oldField.Name != newField.Name || EvolutionSignature.Signature(oldField.FieldType) != EvolutionSignature.Signature(newField.FieldType))
                    rejected.Add("InstanceFieldOrderOrSignatureChanged:" + oldField.Name);
                if (oldField.FieldOffset != newField.FieldOffset)
                {
                    if (oldField.FieldOffset.HasValue && newField.FieldOffset.HasValue) rejected.Add("ExplicitFieldOffsetChanged:" + oldField.Name);
                    else proof.Add("EffectiveNativeFieldOffsetRequired:" + oldField.Name);
                }
            }
            if (newFields.Length > oldFields.Length)
            {
                if (baseline.IsValueType || target.IsValueType) rejected.Add("ValueTypeGrowth");
                if (baseline.GenericParameters.Count != 0 || target.GenericParameters.Count != 0) rejected.Add("GenericDefinitionStructuralChange");
                for (int i = oldFields.Length; i < newFields.Length; ++i)
                {
                    var field = newFields[i];
                    if (!field.IsPrivate || !PrivatePrimitive(field.FieldType)) rejected.Add("OnlyPrivatePrimitiveAppend:" + field.Name);
                }
                proof.Add("AppendMustStartAfterBaselineInstanceSizeAndFitTargetInstanceSize");
            }
            bool changed = rejected.Count != 0 || proof.Count != 0;
            if (proof.Count == 0) proof.Add("ActualPlatformOffsetsAndAllocationCertificateRemainNativeOwned");
            return new NativeLayoutTypeAdmission { typeKey = AssemblyIdentityUtil.TypeKey(target), metadataChanged = changed,
                decision = rejected.Count == 0 ? "NeedsNativeProof" : "Rejected", prePublicationNativeProofRequired = changed,
                reasons = (rejected.Count == 0 ? proof : rejected).ToArray() };
        }

        private static int Kind(TypeDef type)
        {
            if (type.IsInterface) return 3;
            if (type.IsEnum) return 2;
            if (type.IsValueType) return 1;
            string parent = type.BaseType == null ? "" : type.BaseType.FullName;
            return parent == "System.MulticastDelegate" || parent == "System.Delegate" ? 4 : 0;
        }
        private static bool IsByRefLike(TypeDef type)
        { return type.CustomAttributes.Any(a => a.AttributeType != null && a.AttributeType.FullName == "System.Runtime.CompilerServices.IsByRefLikeAttribute"); }
        private static string[] Interfaces(TypeDef type)
        { return type.Interfaces.Select(i => EvolutionSignature.Type(i.Interface)).OrderBy(s => s, StringComparer.Ordinal).ToArray(); }
        private static bool PrivatePrimitive(TypeSig type)
        {
            switch (type.ElementType)
            {
                case ElementType.Boolean: case ElementType.Char:
                case ElementType.I1: case ElementType.U1: case ElementType.I2: case ElementType.U2:
                case ElementType.I4: case ElementType.U4: case ElementType.I8: case ElementType.U8:
                case ElementType.R4: case ElementType.R8: case ElementType.I: case ElementType.U: return true;
                default: return false;
            }
        }
    }
}
