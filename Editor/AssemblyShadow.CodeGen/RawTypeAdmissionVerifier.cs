using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace HybridCLR.AssemblyShadow.CodeGen
{
    // Construction is restricted to the metadata verifier. This is a proof of
    // one instruction in these actual bytes, never a lifecycle or role waiver.
    public sealed class VerifiedRawTypeAdmission
    {
        public string SiteId { get; private set; }
        public string ConsumerAssemblyIdentity { get; private set; }
        public string ConsumerSha256 { get; private set; }
        public string ProviderAssemblyIdentity { get; private set; }
        public string ProviderSha256 { get; private set; }
        public string ProviderInventoryHash { get; private set; }
        public string ConfigurationHash { get; private set; }
        public string MethodSignature { get; private set; }
        public string MethodHash { get; private set; }
        public int OperationIndex { get; private set; }
        public int ReceiverLoadIndex { get; private set; }
        public string ReceiverLiteral { get; private set; }
        public string OperationSignature { get; private set; }
        public string Kind { get; private set; }
        public string TypeName { get; private set; }
        public bool ThrowOnError { get; private set; }
        public bool IgnoreCase { get; private set; }
        public MethodDef Method { get; private set; }
        public string LinkedProfileHash { get; private set; }
        public string CompiledMethodHash { get; private set; }
        public string LinkedMethodHash { get; private set; }
        public string CompiledConsumerSha256 { get; private set; }
        public string LinkedConsumerSha256 { get; private set; }

        internal VerifiedRawTypeAdmission(RawTypeAdmissionSite site, RawTypeAdmissionMethodVariant variant, MethodDef method, int receiverLoadIndex,
            ModuleDefMD provider, string configurationHash, string consumerSha, string providerSha, string inventory,
            VerifiedRawTypeAdmission compiled = null, string linkedProfileHash = null)
        {
            SiteId = site.id; Method = method; ConsumerAssemblyIdentity = method.Module.Assembly.FullName;
            ConsumerSha256 = consumerSha; ProviderAssemblyIdentity = provider.Assembly.FullName; ProviderSha256 = providerSha;
            ProviderInventoryHash = inventory; ConfigurationHash = configurationHash; MethodSignature = method.FullName;
            MethodHash = ReflectionBindingFingerprint.Compute(method); OperationIndex = variant.operationIndex; ReceiverLoadIndex = receiverLoadIndex;
            ReceiverLiteral = provider.Assembly.Name.String; OperationSignature = site.operationSignature;
            Kind = RawTypeAdmissionConfiguration.KindOf(site.operationSignature); TypeName = site.typeName; ThrowOnError = site.throwOnError; IgnoreCase = site.ignoreCase;
            LinkedProfileHash = linkedProfileHash; CompiledMethodHash = compiled == null ? MethodHash : compiled.MethodHash;
            LinkedMethodHash = compiled == null ? null : MethodHash; CompiledConsumerSha256 = compiled == null ? consumerSha : compiled.ConsumerSha256;
            LinkedConsumerSha256 = compiled == null ? null : consumerSha;
        }

        public bool Matches(MethodDef method, int operationIndex)
        {
            return ReferenceEquals(Method, method) && (operationIndex == OperationIndex || operationIndex == ReceiverLoadIndex) &&
                MethodHash == ReflectionBindingFingerprint.Compute(method);
        }
    }

    public static class RawTypeAdmissionVerifier
    {
        public static VerifiedRawTypeAdmission[] Verify(IReadOnlyDictionary<string, ModuleDefMD> modules, RawTypeAdmissionConfiguration configuration)
        { return Verify(modules, configuration, null); }

        public static VerifiedRawTypeAdmission[] Verify(IReadOnlyDictionary<string, ModuleDefMD> modules,
            RawTypeAdmissionConfiguration configuration, string compilerMode)
        { return VerifyCore(modules, configuration, null, null, compilerMode); }

        public static VerifiedRawTypeAdmission[] VerifyLinked(IReadOnlyDictionary<string, ModuleDefMD> compiledModules,
            IReadOnlyDictionary<string, ModuleDefMD> linkedModules, RawTypeAdmissionConfiguration configuration, CapturedReflectionRetargetingProfile profile)
        { return VerifyLinked(compiledModules, linkedModules, configuration, profile, null); }

        public static VerifiedRawTypeAdmission[] VerifyLinked(IReadOnlyDictionary<string, ModuleDefMD> compiledModules,
            IReadOnlyDictionary<string, ModuleDefMD> linkedModules, RawTypeAdmissionConfiguration configuration,
            CapturedReflectionRetargetingProfile profile, string compilerMode)
        {
            var compiled = Verify(compiledModules, configuration, compilerMode);
            BindingChecks.Require(profile != null, "MissingRawAdmissionRetargetingProfile", "Captured per-type linker evidence is required.");
            var result = VerifyCore(linkedModules, configuration, compiled.ToDictionary(value => value.SiteId, StringComparer.Ordinal), profile, compilerMode);
            return result;
        }

        private static VerifiedRawTypeAdmission[] VerifyCore(IReadOnlyDictionary<string, ModuleDefMD> modules, RawTypeAdmissionConfiguration configuration,
            IDictionary<string, VerifiedRawTypeAdmission> compiled, CapturedReflectionRetargetingProfile profile, string compilerMode)
        {
            BindingChecks.Require(configuration != null && modules != null, "MissingRawAdmissionEvidence", "Actual modules and configuration are required.");
            configuration.Validate(); string configurationHash = configuration.ComputeHash();
            var catalog = new Dictionary<string, ModuleDefMD>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in modules)
            {
                var module = pair.Value;
                BindingChecks.Require(module != null && module.Assembly != null && pair.Key.Equals(module.Assembly.Name.String, StringComparison.OrdinalIgnoreCase) &&
                    !catalog.ContainsKey(module.Assembly.Name.String), "AmbiguousRawAdmissionModule", pair.Key);
                catalog.Add(module.Assembly.Name.String, module);
            }
            var results = new List<VerifiedRawTypeAdmission>();
            var covered = new Dictionary<MethodDef, HashSet<int>>();
            var hashes = new Dictionary<ModuleDefMD, string>();
            var inventories = new Dictionary<ModuleDefMD, string>();
            foreach (var site in configuration.sites.OrderBy(value => value.id, StringComparer.Ordinal))
            {
                ModuleDefMD consumer, provider;
                BindingChecks.Require(catalog.TryGetValue(site.consumerAssembly, out consumer), "RawAdmissionConsumerMissing", site.id);
                string providerName = new AssemblyNameInfo(site.providerAssemblyIdentity).Name.String;
                BindingChecks.Require(catalog.TryGetValue(providerName, out provider) && provider.Assembly.FullName == site.providerAssemblyIdentity,
                    "RawAdmissionProviderIdentityMismatch", site.id);
                RequireSingleModule(provider); RequireSingleModule(consumer);
                var matches = consumer.GetTypes().Where(type => type.FullName == site.declaringType).SelectMany(type => type.Methods)
                    .Where(candidate => candidate.FullName == site.methodSignature).ToArray();
                BindingChecks.Require(matches.Length == 1, "RawAdmissionMethodMissing", site.id);
                var method = matches[0];
                BindingChecks.Require(method.IsStatic && method.HasBody && method.GenericParameters.Count == 0 && method.DeclaringType.GenericParameters.Count == 0 &&
                    method.Body.ExceptionHandlers.Count == 0 && method.MethodSig != null && !method.MethodSig.HasThis &&
                    method.MethodSig.GenParamCount == 0 && method.MethodSig.Params.Count <= 1 &&
                    (method.MethodSig.Params.Count == 0 || method.MethodSig.Params[0].ElementType == ElementType.String),
                    "UnsupportedRawAdmissionHelper", site.id + ": expected a static finite literal helper without EH or generics.");
                string methodHash = ReflectionBindingFingerprint.Compute(method);
                RawTypeAdmissionMethodVariant variant;
                if (compiled == null)
                {
                    variant = configuration.MethodVariant(site, compilerMode);
                    BindingChecks.Require(methodHash == variant.methodHash, "RawAdmissionMethodHashMismatch",
                        site.id + ": actual bytes differ from the " + variant.compilerMode + " compiler contract.");
                }
                else
                {
                    var source = compiled[site.id];
                    variant = new RawTypeAdmissionMethodVariant { compilerMode = compilerMode, methodHash = source.CompiledMethodHash, operationIndex = source.OperationIndex };
                    BindingChecks.Require(source.ConsumerAssemblyIdentity == consumer.Assembly.FullName, "RawAdmissionLinkedIdentityMismatch", site.id);
                    BindingChecks.Require(ReflectionBindingFingerprint.Shape(source.Method, source.Method.DeclaringType.FullName, -1, null, profile.LinkedScope) == methodHash,
                        "RawAdmissionLinkedMethodChanged", site.id);
                    ModuleDefMD linkedCore;
                    BindingChecks.Require(catalog.TryGetValue(consumer.CorLibTypes.AssemblyRef.Name.String, out linkedCore) &&
                        profile.RuntimeModules.Any(value => value.AssemblyIdentity == linkedCore.Assembly.FullName &&
                            value.Sha256 == BindingChecks.Sha256(linkedCore.Metadata.PEImage.CreateReader().ToArray())),
                        "RawAdmissionLinkedFrameworkMismatch", site.id + ": framework bytes must match the captured retargeting profile.");
                }
                RequireBytesAgree(consumer, method, hashes, inventories, false);
                RequireBytesAgree(provider, null, hashes, inventories, true);
                HashSet<int> bodyCoverage;
                if (!covered.TryGetValue(method, out bodyCoverage)) { bodyCoverage = new HashSet<int>(); covered.Add(method, bodyCoverage); }
                int loadIndex = CheckChain(method, site, variant.operationIndex, provider, catalog, bodyCoverage);
                results.Add(new VerifiedRawTypeAdmission(site, variant, method, loadIndex, provider, configurationHash, hashes[consumer], hashes[provider], inventories[provider],
                    compiled == null ? null : compiled[site.id], profile == null ? null : profile.ComputeHash()));
            }
            foreach (var pair in covered) CheckWholeHelper(pair.Key, pair.Value, catalog);
            BindingChecks.Require(configuration.ComputeHash() == configurationHash, "RawAdmissionConfigurationChanged", "Configuration changed during verification.");
            return results.ToArray();
        }

        private static void RequireSingleModule(ModuleDefMD module)
        {
            BindingChecks.Require(module.Assembly.Modules.Count == 1 && module.Metadata.TablesStream.FileTable.Rows == 0 && module.ExportedTypes.Count == 0,
                "UnsupportedRawAdmissionModule", module.Assembly.FullName + ": multimodule/exported-type redirection is unsupported.");
        }

        private static void RequireBytesAgree(ModuleDefMD module, MethodDef method, IDictionary<ModuleDefMD, string> hashes,
            IDictionary<ModuleDefMD, string> inventories, bool inventory)
        {
            byte[] bytes = module.Metadata.PEImage.CreateReader().ToArray();
            if (!hashes.ContainsKey(module)) hashes.Add(module, BindingChecks.Sha256(bytes));
            using (var fresh = ModuleDefMD.Load(bytes, new ModuleCreationOptions { TryToLoadPdbFromDisk = false }))
            {
                BindingChecks.Require(fresh.Assembly.FullName == module.Assembly.FullName, "RawAdmissionBytesMismatch", "In-memory assembly identity differs from captured bytes.");
                if (method != null)
                {
                    var originals = fresh.GetTypes().SelectMany(type => type.Methods).Where(value => value.FullName == method.FullName).ToArray();
                    BindingChecks.Require(originals.Length == 1 && ReflectionBindingFingerprint.Compute(originals[0]) == ReflectionBindingFingerprint.Compute(method),
                        "RawAdmissionBytesMismatch", method.FullName);
                }
                if (inventory)
                {
                    string actual = ComputeProviderInventoryHash(module);
                    BindingChecks.Require(actual == ComputeProviderInventoryHash(fresh), "RawAdmissionBytesMismatch", "Type inventory differs from captured bytes.");
                    inventories[module] = actual;
                }
            }
        }

        public static string ComputeProviderInventoryHash(ModuleDefMD module)
        {
            BindingChecks.Require(module != null && module.Assembly != null, "RawAdmissionProviderMissing", "Provider module is required.");
            var types = module.GetTypes().Where(type => !type.IsGlobalModuleType).OrderBy(type => type.FullName, StringComparer.Ordinal).ToArray();
            BindingChecks.Require(types.Select(type => type.FullName).Distinct(StringComparer.Ordinal).Count() == types.Length,
                "AmbiguousRawAdmissionType", module.Assembly.FullName);
            using (var hash = new BindingHash("assembly-shadow-raw-type-inventory:1"))
            {
                hash.Add(module.Assembly.FullName); hash.Add(types.Length);
                foreach (var type in types)
                {
                    hash.Add(type.FullName); hash.Add(type.Namespace.String); hash.Add(type.Name.String);
                    hash.Add(type.DeclaringType == null ? null : type.DeclaringType.FullName); hash.Add(type.GenericParameters.Count);
                    hash.Add((uint)type.Attributes); hash.Add(type.BaseType == null ? null : type.BaseType.FullName);
                }
                return hash.Finish();
            }
        }

        private static int CheckChain(MethodDef method, RawTypeAdmissionSite site, int operationIndex, ModuleDefMD provider,
            IDictionary<string, ModuleDefMD> modules, HashSet<int> covered)
        {
            var il = method.Body.Instructions;
            BindingChecks.Require(operationIndex < il.Count && il[operationIndex].OpCode.Code == Code.Callvirt, "RawAdmissionOperationMismatch", site.id);
            var operation = il[operationIndex].Operand as IMethod;
            BindingChecks.Require(operation != null && operation.FullName == site.operationSignature && operation.MethodSig != null && operation.MethodSig.HasThis &&
                TrustedMethod(operation, method.Module, modules) && TrustedSignature(method.MethodSig.RetType, method.Module, modules) &&
                SameType(method.MethodSig.RetType, operation.MethodSig.RetType), "RawAdmissionOperationMismatch", site.id);
            string kind = RawTypeAdmissionConfiguration.KindOf(site.operationSignature); int cursor = operationIndex;
            if (kind == "Module.GetType")
            {
                int ignore = Previous(il, ref cursor), throws = Previous(il, ref cursor), literal = Previous(il, ref cursor);
                BindingChecks.Require(il[ignore].IsLdcI4() && il[ignore].GetLdcI4Value() == 0 && il[throws].IsLdcI4() && il[throws].GetLdcI4Value() == 1 &&
                    il[literal].OpCode.Code == Code.Ldstr && (string)il[literal].Operand == site.typeName &&
                    provider.GetTypes().Count(type => type.FullName == site.typeName.Replace('+', '/')) == 1, "RawAdmissionTypeMismatch", site.id);
            }
            if (kind.StartsWith("Module.", StringComparison.Ordinal))
            {
                int manifest = Previous(il, ref cursor);
                BindingChecks.Require(il[manifest].OpCode.Code == Code.Callvirt && MethodIs(il[manifest].Operand as IMethod,
                    "System.Reflection.Module System.Reflection.Assembly::get_ManifestModule()", method.Module, modules), "RawAdmissionReceiverMismatch", site.id);
            }
            int load = Previous(il, ref cursor);
            BindingChecks.Require(il[load].OpCode.Code == Code.Call && MethodIs(il[load].Operand as IMethod,
                "System.Reflection.Assembly System.Reflection.Assembly::Load(System.String)", method.Module, modules), "RawAdmissionReceiverMismatch", site.id);
            int start = Previous(il, ref cursor);
            BindingChecks.Require(il[start].OpCode.Code == Code.Ldstr && (string)il[start].Operand == provider.Assembly.Name.String,
                "RawAdmissionReceiverMismatch", site.id + ": receiver must be the exact physical provider simple-name literal.");
            foreach (Instruction instruction in il)
            {
                var target = instruction.Operand as Instruction;
                if (target != null) CheckEntry(il, target, start, operationIndex, site.id);
                var targets = instruction.Operand as IList<Instruction>;
                if (targets != null) foreach (var item in targets) CheckEntry(il, item, start, operationIndex, site.id);
            }
            for (int index = start; index <= operationIndex; index++)
                BindingChecks.Require(covered.Add(index), "OverlappingRawAdmissionChain", site.id);
            return load;
        }

        private static int Previous(IList<Instruction> il, ref int index)
        {
            do { --index; BindingChecks.Require(index >= 0, "RawAdmissionReceiverMismatch", "Incomplete literal receiver chain."); } while (il[index].OpCode.Code == Code.Nop);
            return index;
        }
        private static void CheckEntry(IList<Instruction> il, Instruction target, int start, int end, string id)
        {
            int index = il.IndexOf(target);
            BindingChecks.Require(index >= 0 && (index <= start || index > end), "RawAdmissionBranchBypass", id);
        }
        private static bool MethodIs(IMethod method, string signature, ModuleDef module, IDictionary<string, ModuleDefMD> modules)
        { return method != null && method.FullName == signature && TrustedMethod(method, module, modules); }
        private static bool SameType(TypeSig first, TypeSig second)
        { return first != null && second != null && first.FullName == second.FullName && first.ElementType == second.ElementType && new SigComparer().Equals(first, second); }

        private static bool TrustedMethod(IMethod method, ModuleDef owner, IDictionary<string, ModuleDefMD> modules)
        {
            if (method == null || method is MethodSpec || method.MethodSig == null || method.MethodSig.GenParamCount != 0 ||
                (uint)method.MethodSig.CallingConvention != (method.MethodSig.HasThis ? 32u : 0u) ||
                method.MethodSig.ParamsAfterSentinel != null || !TrustedType(method.DeclaringType, owner, modules)) return false;
            if (!TrustedSignature(method.MethodSig.RetType, owner, modules) || !method.MethodSig.Params.All(type => TrustedSignature(type, owner, modules))) return false;
            ModuleDefMD core = modules[owner.CorLibTypes.AssemblyRef.Name.String];
            var definitions = core.GetTypes().Where(type => type.FullName == method.DeclaringType.FullName).SelectMany(type => type.Methods)
                .Where(value => value.FullName == method.FullName && value.MethodSig.HasThis == method.MethodSig.HasThis).ToArray();
            return definitions.Length == 1;
        }
        private static bool TrustedSignature(TypeSig type, ModuleDef owner, IDictionary<string, ModuleDefMD> modules)
        {
            if (type == null) return false;
            if (type is CorLibTypeSig) return TrustedType(type.ToTypeDefOrRef(), owner, modules);
            if (type is ClassSig || type is ValueTypeSig) return TrustedType(type.ToTypeDefOrRef(), owner, modules);
            if (type is SZArraySig) return TrustedSignature(type.Next, owner, modules);
            var generic = type as GenericInstSig;
            return generic != null && TrustedType(generic.GenericType.TypeDefOrRef, owner, modules) && generic.GenericArguments.All(value => TrustedSignature(value, owner, modules));
        }
        private static bool TrustedType(ITypeDefOrRef type, ModuleDef owner, IDictionary<string, ModuleDefMD> modules)
        {
            if (type == null || type is TypeSpec || type.DefinitionAssembly == null || owner.CorLibTypes.AssemblyRef == null) return false;
            string identity = owner.CorLibTypes.AssemblyRef.FullName;
            if (identity != "mscorlib, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089" &&
                identity != CapturedReflectionRetargetingProfile.RequiredFacadeIdentity) return false;
            ModuleDefMD core;
            return type.DefinitionAssembly.FullName == identity && modules.TryGetValue(owner.CorLibTypes.AssemblyRef.Name.String, out core) &&
                core.Assembly.FullName == identity && core.GetTypes().Count(value => value.FullName == type.FullName) == 1;
        }

        private static void CheckWholeHelper(MethodDef method, HashSet<int> covered, IDictionary<string, ModuleDefMD> modules)
        {
            var il = method.Body.Instructions;
            foreach (Local local in method.Body.Variables)
                BindingChecks.Require(local.Type.ElementType == ElementType.String || local.Type.ElementType == ElementType.I4 || local.Type.ElementType == ElementType.Boolean ||
                    SameType(local.Type, method.MethodSig.RetType), "UnsupportedRawAdmissionHelper", "Receiver aliases and arbitrary locals are unsupported.");
            for (int index = 0; index < il.Count; index++)
            {
                if (covered.Contains(index)) continue;
                var instruction = il[index]; Code code = instruction.OpCode.Code;
                bool allowed = code == Code.Nop || code == Code.Ret || code == Code.Throw || code == Code.Ldstr || code == Code.Ldnull || instruction.IsLdcI4() ||
                    code == Code.Br || code == Code.Br_S || code == Code.Brtrue || code == Code.Brtrue_S || code == Code.Brfalse || code == Code.Brfalse_S ||
                    code == Code.Beq || code == Code.Beq_S || code == Code.Bne_Un || code == Code.Bne_Un_S || code == Code.Ceq || code == Code.Switch;
                if (code == Code.Ldarg || code == Code.Ldarg_S || code == Code.Ldarg_0)
                    allowed = instruction.GetParameterIndex() == 0 && method.MethodSig.Params.Count == 1;
                if (code == Code.Ldloc || code == Code.Ldloc_S || code == Code.Ldloc_0 || code == Code.Ldloc_1 || code == Code.Ldloc_2 || code == Code.Ldloc_3 ||
                    code == Code.Stloc || code == Code.Stloc_S || code == Code.Stloc_0 || code == Code.Stloc_1 || code == Code.Stloc_2 || code == Code.Stloc_3)
                    allowed = instruction.GetLocal(method.Body.Variables) != null;
                var called = instruction.Operand as IMethod;
                if (code == Code.Call)
                    allowed = MethodIs(called, "System.Boolean System.String::op_Equality(System.String,System.String)", method.Module, modules) ||
                        MethodIs(called, "System.Boolean System.String::op_Inequality(System.String,System.String)", method.Module, modules);
                if (code == Code.Newobj)
                    allowed = MethodIs(called, "System.Void System.ArgumentException::.ctor(System.String)", method.Module, modules) ||
                        MethodIs(called, "System.Void System.ArgumentException::.ctor(System.String,System.String)", method.Module, modules) ||
                        MethodIs(called, "System.Void System.ArgumentOutOfRangeException::.ctor(System.String)", method.Module, modules);
                BindingChecks.Require(allowed, "UnsupportedRawAdmissionHelper", method.FullName + " operation " + index + " is outside the finite literal helper grammar: " + instruction);
            }
        }
    }
}
