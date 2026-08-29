using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dnlib.DotNet;
using HybridCLR.Editor.Meta;
using HybridCLR.Editor.MethodBridge;

namespace HybridCLR.Editor.AssemblyShadow
{
    [Serializable]
    public sealed class GenerationAbiEntry { public string key, abi; public int capacity; }

    [Serializable]
    public sealed class ShadowGenerationOutputReceipt
    {
        public int schemaVersion = 1;
        public string kind = "GenerationOutput";
        public string planHash, aotInventoryHash, stage, target, architecture, templateSha256, outputPath, outputSha256, outputHash;
        public bool development;
        public int maxIterations;
        public bool nativePointerDispatchHasMethodInfo;
        public string reversePInvokeGuardPolicy;
        public string[] collectorRoots = new string[0], collectorTypes = new string[0], collectorMethods = new string[0],
            reverseMethods = new string[0], nativeCallSignatures = new string[0], aotTypes = new string[0], aotMethods = new string[0], emittedAssemblyNames = new string[0];
        public GenerationImage[] resolverCatalog = new GenerationImage[0];
        public GenerationAbiEntry[] managedToNative = new GenerationAbiEntry[0], nativeToManaged = new GenerationAbiEntry[0], adjustThunks = new GenerationAbiEntry[0],
            reversePInvoke = new GenerationAbiEntry[0], calli = new GenerationAbiEntry[0], structMappings = new GenerationAbiEntry[0];
    }

    public sealed class ShadowGenerationOutput
    {
        private readonly ShadowGenerationOutputReceipt receipt;
        private readonly VerifiedGenerationPlan plan;
        private readonly VerifiedGenerationAotInputs aot;
        private readonly string receiptSha256;
        public string ReceiptPath { get; private set; }
        public string OutputHash { get { return receipt.outputHash; } }
        public string Stage { get { return receipt.stage; } }
        public string OutputPath { get { return receipt.outputPath; } }
        public string OutputSha256 { get { return receipt.outputSha256; } }
        public bool Development { get { return receipt.development; } }
        public string[] EmittedAssemblyNames { get { return (receipt.emittedAssemblyNames ?? new string[0]).ToArray(); } }
        public ShadowGenerationOutputReceipt Receipt { get { return GenerationIO.Clone(receipt); } }
        internal ShadowGenerationOutput(string path, ShadowGenerationOutputReceipt receipt, VerifiedGenerationPlan plan, VerifiedGenerationAotInputs aot, string receiptSha256)
        {
            // ReadAndVerify owns this freshly deserialized receipt. Keeping that
            // private instance avoids serializing and deserializing very large
            // method inventories a second time; public access remains cloned.
            ReceiptPath = Path.GetFullPath(path); this.receipt = receipt; this.plan = plan; this.aot = aot; this.receiptSha256 = receiptSha256;
        }

        internal static ShadowGenerationOutput Seal(string outputFile, VerifiedGenerationPlan plan, VerifiedGenerationAotInputs aot, ShadowGenerationOutputReceipt receipt)
        {
            plan.VerifyUnchanged(); if (aot != null) aot.VerifyUnchanged(plan);
            receipt.planHash = plan.PlanHash; receipt.aotInventoryHash = aot == null ? null : aot.InventoryHash;
            receipt.target = plan.Target; receipt.architecture = plan.Architecture;
            receipt.outputPath = Path.GetFileName(outputFile); receipt.outputSha256 = ShadowHash.File(outputFile);
            receipt.outputHash = ComputeHash(receipt);
            string path = outputFile + ".generation.json"; ShadowHash.Require(!File.Exists(path), "GenerationExists", path);
            GenerationIO.Write(path, receipt); return ReadAndVerify(path, plan, aot, receipt.outputHash);
        }
        public static ShadowGenerationOutput ReadAndVerify(string receiptPath, VerifiedGenerationPlan plan, VerifiedGenerationAotInputs aot = null, string expectedHash = null)
        {
            plan.VerifyUnchanged(); if (aot != null) aot.VerifyUnchanged(plan);
            string receiptSha256;
            var receipt = GenerationIO.Read<ShadowGenerationOutputReceipt>(receiptPath, out receiptSha256);
            ShadowHash.Require(receipt.schemaVersion == 1 && receipt.kind == "GenerationOutput" &&
                new[] { "Link", "MethodBridge", "AotGenericReference" }.Contains(receipt.stage) && receipt.planHash == plan.PlanHash &&
                receipt.target == plan.Target && receipt.architecture == plan.Architecture &&
                receipt.aotInventoryHash == (aot == null ? null : aot.InventoryHash) && receipt.outputHash == ComputeHash(receipt) &&
                (expectedHash == null || receipt.outputHash == expectedHash), "GenerationOutputReceipt", receiptPath);
            ShadowHash.Require(ShadowHash.File(ShadowHash.SafeChild(Path.GetDirectoryName(Path.GetFullPath(receiptPath)), receipt.outputPath)) == receipt.outputSha256,
                "GenerationOutputMutation", receiptPath);
            using (var resolver = aot == null ? plan.CreateResolver() : aot.CreateResolver(plan))
                ShadowHash.Require(GenerationIO.Equal(resolver.Catalog.ToArray(), receipt.resolverCatalog), "GenerationCatalog", receiptPath);
            ShadowHash.Require(receipt.stage == "Link" ? aot == null && receipt.maxIterations == 0 :
                aot != null && receipt.maxIterations > 0 && receipt.maxIterations <= 20, "GenerationOutputReceipt", "Invalid stage inputs.");
            ShadowHash.Require(!receipt.nativePointerDispatchHasMethodInfo, "GenerationOutputReceipt", "Native-pointer dispatch has no MethodInfo ABI.");
            ShadowHash.Require(receipt.reversePInvokeGuardPolicy == (receipt.stage == "MethodBridge" ? "AbortBeforeExternalDispatch" : null),
                "GenerationOutputReceipt", "External callbacks require nonthrowing fatal guards.");
            foreach (var entries in new[] { receipt.managedToNative, receipt.nativeToManaged, receipt.adjustThunks, receipt.reversePInvoke, receipt.calli, receipt.structMappings })
                ShadowHash.Require(entries != null && entries.All(entry => entry != null && !string.IsNullOrEmpty(entry.key) && !string.IsNullOrEmpty(entry.abi) && entry.capacity > 0) &&
                    entries.Select(entry => entry.key).Distinct(StringComparer.Ordinal).Count() == entries.Length, "GenerationOutputReceipt", "Invalid optimized inventory.");
            return new ShadowGenerationOutput(receiptPath, receipt, plan, aot, receiptSha256);
        }
        public static string ComputeHash(ShadowGenerationOutputReceipt receipt)
        {
            ShadowHash.Require(receipt != null, "GenerationOutputReceipt", "A generation output receipt is required.");
            // outputHash is the only self-excluded field. Serialize the complete
            // DTO once while it is null instead of cloning the potentially tens
            // of megabytes of collector evidence through the serializer first.
            lock (receipt)
            {
                string outputHash = receipt.outputHash;
                try
                {
                    receipt.outputHash = null;
                    return ShadowHash.Text("assembly-shadow-generation-output:1\n" + Convert.ToBase64String(GenerationIO.Bytes(receipt)));
                }
                finally { receipt.outputHash = outputHash; }
            }
        }

        public static void RequireCoverage(ShadowGenerationOutput selected, params ShadowGenerationOutput[] required)
        {
            ShadowHash.Require(selected != null && selected.receipt.stage == "MethodBridge", "GenerationCoverage", "Selected MethodBridge output required.");
            selected.VerifyFile();
            foreach (var input in required ?? new ShadowGenerationOutput[0])
            {
                ShadowHash.Require(input != null && input.receipt.stage == "MethodBridge" && input.receipt.target == selected.receipt.target &&
                    input.receipt.architecture == selected.receipt.architecture && input.receipt.development == selected.receipt.development &&
                    input.receipt.templateSha256 == selected.receipt.templateSha256, "GenerationCoverage", "Incompatible generator ABI context.");
                input.VerifyFile();
                RequireEntries(selected.receipt.managedToNative, input.receipt.managedToNative, "M2N");
                RequireEntries(selected.receipt.nativeToManaged, input.receipt.nativeToManaged, "N2M");
                RequireEntries(selected.receipt.adjustThunks, input.receipt.adjustThunks, "AdjustThunk");
                RequireEntries(selected.receipt.reversePInvoke, input.receipt.reversePInvoke, "ReversePInvoke");
                RequireEntries(selected.receipt.calli, input.receipt.calli, "Calli");
                RequireStructMappings(selected.receipt.structMappings, input.receipt.structMappings);
            }
        }
        private void VerifyFile()
        {
            plan.VerifyUnchanged(); if (aot != null) aot.VerifyUnchanged(plan);
            ShadowHash.Require(ShadowHash.File(ReceiptPath) == receiptSha256 &&
                ShadowHash.File(ShadowHash.SafeChild(Path.GetDirectoryName(ReceiptPath), receipt.outputPath)) == receipt.outputSha256,
                "GenerationOutputMutation", ReceiptPath);
        }
        private static void RequireEntries(GenerationAbiEntry[] selected, GenerationAbiEntry[] required, string kind)
        {
            ShadowHash.Require(selected != null && required != null, "GenerationCoverage", kind);
            var capacities = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var candidate in selected)
            {
                if (candidate == null || string.IsNullOrEmpty(candidate.abi) || candidate.capacity <= 0) continue;
                int capacity;
                if (!capacities.TryGetValue(candidate.abi, out capacity) || capacity < candidate.capacity)
                    capacities[candidate.abi] = candidate.capacity;
            }
            foreach (var entry in required)
            {
                int capacity;
                ShadowHash.Require(entry != null && entry.capacity > 0 && !string.IsNullOrEmpty(entry.abi) &&
                    capacities.TryGetValue(entry.abi, out capacity) && capacity >= entry.capacity,
                    "GenerationCoverage", kind + " missing structural ABI/capacity: " + (entry == null ? "null" : entry.abi));
            }
        }
        private static void RequireStructMappings(GenerationAbiEntry[] selected, GenerationAbiEntry[] required)
        {
            ShadowHash.Require(selected != null && required != null, "GenerationCoverage", "Missing struct inventory.");
            var mappings = new HashSet<Tuple<string, string>>(selected.Where(candidate => candidate != null)
                .Select(candidate => Tuple.Create(candidate.key, candidate.abi)));
            foreach (var entry in required)
                ShadowHash.Require(entry != null && mappings.Contains(Tuple.Create(entry.key, entry.abi)),
                    "GenerationCoverage", "Missing runtime struct signature mapping: " + (entry == null ? "null" : entry.key));
        }
        internal static GenerationAbiEntry[] Entries(IReadOnlyList<GeneratedBridgeSignature> entries)
        { return entries.Select(entry => new GenerationAbiEntry { key = entry.Key, abi = entry.Abi, capacity = entry.Capacity }).ToArray(); }
        internal static void NewOutput(string path)
        { ShadowHash.Require(!File.Exists(path) && !File.Exists(path + ".generation.json"), "GenerationExists", path); Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))); }
    }

    internal static class GenerationSignatures
    {
        internal static string Type(TypeSig type)
        {
            if (type == null) return "null";
            var writer = new CanonicalSignatureWriter(); writer.Token(type.ElementType.ToString()); writer.Token(type.FullName);
            var direct = type as TypeDefOrRefSig;
            if (direct != null) writer.Token(direct.TypeDefOrRef.DefinitionAssembly == null ? "" : direct.TypeDefOrRef.DefinitionAssembly.FullName);
            var generic = type as GenericInstSig;
            if (generic != null) { writer.Token(Type(generic.GenericType)); foreach (var argument in generic.GenericArguments) writer.Token(Type(argument)); }
            var modifier = type as ModifierSig;
            if (modifier != null) writer.Token(modifier.Modifier.AssemblyQualifiedName);
            var array = type as ArraySig;
            if (array != null) { writer.Token(array.Rank.ToString()); writer.Token(string.Join(",", array.Sizes)); writer.Token(string.Join(",", array.LowerBounds)); }
            var function = type as FnPtrSig;
            if (function != null)
            {
                var signature = function.Signature as MethodSig;
                ShadowHash.Require(signature != null, "GenerationSignature", "Unsupported function-pointer signature.");
                writer.Token(Signature(signature));
            }
            if (type.Next != null) writer.Token(Type(type.Next));
            return writer.ToString();
        }
        internal static string Method(GenericMethod method)
        {
            var writer = new CanonicalSignatureWriter(); writer.Token(method.Method.Module.Assembly.FullName);
            writer.Token(method.Method.DeclaringType.FullName); writer.Token(method.Method.Name); writer.Token(Signature(method.Method.MethodSig));
            foreach (var type in method.KlassInst ?? new List<TypeSig>()) writer.Token("class:" + Type(type));
            foreach (var type in method.MethodInst ?? new List<TypeSig>()) writer.Token("method:" + Type(type));
            return writer.ToString();
        }
        internal static string Signature(MethodSig signature)
        {
            var writer = new CanonicalSignatureWriter(); writer.Token(signature.CallingConvention.ToString()); writer.Token(signature.GenParamCount.ToString());
            writer.Token(Type(signature.RetType)); foreach (var type in signature.Params) writer.Token(Type(type));
            writer.Token("sentinel"); foreach (var type in signature.ParamsAfterSentinel ?? new List<TypeSig>()) writer.Token(Type(type));
            return writer.ToString();
        }
        internal static string[] Sorted(IEnumerable<string> values) { return values.Distinct(StringComparer.Ordinal).OrderBy(value => value, StringComparer.Ordinal).ToArray(); }
    }
}
