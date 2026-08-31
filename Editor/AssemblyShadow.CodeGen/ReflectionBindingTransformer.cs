using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Pdb;
using dnlib.DotNet.Writer;

namespace HybridCLR.AssemblyShadow.CodeGen
{
    public sealed class ReflectionTransformResult
    {
        public byte[] PeData { get; internal set; }
        public byte[] PdbData { get; internal set; }
        public string ConfigurationHash { get; internal set; }
    }

    public sealed class VerifiedReflectionBinding
    {
        public string SiteId, Assembly, TypeName, MethodSignature, ConfigurationHash, OriginalMethodHash;
        public int OperationIndex;
        public string[] AllowedTypes, Providers;
        // Definitions belong to the caller-owned module supplied to Verify.
        public MethodDef OriginalMethod, GuardMethod;
        // Populated only by VerifyLinked; these are full, non-normalized shapes.
        public string LinkedProfileHash, CompiledMethodHash, LinkedMethodHash, CompiledGuardHash, LinkedGuardHash;
        public string Kind, ImageSha256, ProviderAssemblyIdentity, ImagePath;
    }

    public static partial class ReflectionBindingTransformer
    {
        public const string GuardPrefix = "__AssemblyShadowReflectionBinding_";

        private sealed class OriginalSite
        {
            internal ReflectionBindingSite Site;
            internal ReflectionBindingMethodVariant Variant;
            internal MethodDef Method;
        }

        public static ReflectionTransformResult Transform(byte[] pe, byte[] pdb, ReflectionBindingConfiguration configuration)
        {
            BindingChecks.Require(configuration != null, "MissingConfiguration", "Configuration is required.");
            configuration.Validate(); string configHash = configuration.ComputeHash();
            using (var module = Load(pe, pdb))
            {
                RequireUnsigned(module);
                var sites = Sites(module, configuration);
                BindingChecks.Require(sites.Length > 0, "NoConfiguredAssembly", module.Assembly.Name.String);
                BindingChecks.Require(!module.GetTypes().SelectMany(type => type.Methods).Any(method => method.Name.String.StartsWith(GuardPrefix, StringComparison.Ordinal)),
                    "AlreadyProcessed", "Reserved guard methods already exist; reprocessing is prohibited.");
                Guid? originalMvid = module.Mvid;
                string points = SequencePoints(module);
                // Resolve and validate every source site before changing any body.
                var originals = sites.Select(site => ResolveOriginalSite(module, configuration, site)).ToArray();
                foreach (var item in originals)
                {
                    var operation = item.Method.Body.Instructions[item.Variant.operationIndex];
                    var lookup = (IMethod)operation.Operand;
                    var guard = CreateGuard(module, item.Site, configHash, lookup);
                    item.Method.DeclaringType.Methods.Add(guard);
                    // Same static Type(string) signature and call opcode preserve the
                    // original stack, branch target, instruction identity and PDB point.
                    operation.Operand = guard;
                    operation.OpCode = OpCodes.Call;
                }
                Verify(module, configuration);
                if (module.PdbState == null) module.CreatePdbState(PdbFileKind.PortablePDB);
                else module.PdbState.PdbFileKind = PdbFileKind.PortablePDB;
                using (var output = new MemoryStream())
                using (var symbols = new MemoryStream())
                {
                    var options = new ModuleWriterOptions(module)
                    {
                        WritePdb = true, PdbStream = symbols, PdbFileName = module.Assembly.Name.String + ".pdb",
                        PdbFileNameInDebugDirectory = module.Assembly.Name.String + ".pdb",
                        PdbOptions = PdbWriterOptions.Deterministic | PdbWriterOptions.PdbChecksum,
                    };
                    options.PEHeadersOptions.TimeDateStamp = 0;
                    options.MetadataOptions.Flags |= MetadataFlags.PreserveAll;
                    module.Write(output, options);
                    var result = new ReflectionTransformResult { PeData = output.ToArray(), PdbData = symbols.ToArray(), ConfigurationHash = configHash };
                    using (var emitted = Load(result.PeData, result.PdbData))
                    {
                        BindingChecks.Require(emitted.Mvid == originalMvid, "MvidChanged", "Transformation must preserve the input MVID.");
                        BindingChecks.Require(emitted.PdbState != null && emitted.PdbState.PdbFileKind == PdbFileKind.PortablePDB && SequencePoints(emitted) == points,
                            "SequencePointsChanged", "Original instruction sequence points must survive portable-PDB emission.");
                        Verify(emitted, configuration);
                    }
                    BindingChecks.Require(configuration.ComputeHash() == configHash, "ConfigurationChanged", "Configuration changed during transformation.");
                    return result;
                }
            }
        }

        public static VerifiedReflectionBinding[] Verify(ModuleDefMD module, ReflectionBindingConfiguration configuration)
        {
            BindingChecks.Require(module != null && configuration != null, "MissingConfiguration", "Module and configuration are required.");
            RequireUnsigned(module); configuration.Validate(); string configHash = configuration.ComputeHash();
            var sites = Sites(module, configuration); var verified = new List<VerifiedReflectionBinding>();
            var expectedGuards = new HashSet<MethodDef>();
            foreach (var site in sites)
            {
                var original = FindMethod(module, site);
                var variants = configuration.MethodVariants(site);
                var guardedIndices = variants.Select(value => value.operationIndex).Distinct().Where(index =>
                {
                    if (index >= original.Body.Instructions.Count) return false;
                    var candidate = original.Body.Instructions[index]; var target = candidate.Operand as MethodDef;
                    return candidate.OpCode.Code == Code.Call && target != null && target.DeclaringType == original.DeclaringType && target.Name == GuardName(site, configHash);
                }).ToArray();
                BindingChecks.Require(guardedIndices.Length == 1, "MissingGuardedSite", site.id);
                int operationIndex = guardedIndices[0];
                var operation = original.Body.Instructions[operationIndex]; var guard = (MethodDef)operation.Operand;
                BindingChecks.Require(expectedGuards.Add(guard), "AmbiguousGuard", site.id);
                BindingChecks.Require(original.Body.Instructions.All(instruction => !IsSiteAcquisition(instruction.Operand as IMethod, site)), "AdditionalLookup", site.id);
                BindingChecks.Require(guard.MethodSig != null && guard.MethodSig.Params.Count == 1, "GuardTemplateMismatch", site.id);
                // The guard signature carries the original framework Type and String
                // scopes even in deny-all mode, where no lookup MemberRef remains.
                var lookup = OriginalAcquisition(module, site, guard);
                var expected = CreateGuard(module, site, configHash, lookup);
                BindingChecks.Require(guard.HasBody && ReflectionBindingFingerprint.Shape(guard, original.DeclaringType.FullName, -1, null) ==
                    ReflectionBindingFingerprint.Shape(expected, original.DeclaringType.FullName, -1, null), "GuardTemplateMismatch", site.id);
                string restoredMethodHash = ReflectionBindingFingerprint.Shape(original, original.DeclaringType.FullName,
                    operationIndex, lookup, null, OriginalCode(site));
                var matchingVariants = variants.Where(value => value.operationIndex == operationIndex &&
                    restoredMethodHash == value.originalMethodHash).ToArray();
                BindingChecks.Require(matchingVariants.Length == 1, "OriginalMethodChanged", "Virtual restoration of the original lookup did not match exactly one compiler variant: " + site.id);
                var variant = matchingVariants[0];
                foreach (var method in module.GetTypes().SelectMany(type => type.Methods).Where(method => method.HasBody))
                    foreach (var instruction in method.Body.Instructions)
                    {
                        var target = instruction.Operand as IMethod;
                        if (target == null || target.Name != guard.Name) continue;
                        BindingChecks.Require(method == original && instruction == operation && target == guard,
                            "AdditionalGuardReference", "A site guard may only be called by its one verified operation: " + site.id);
                    }
                var allowed = site.allowedTypes.OrderBy(value => value, StringComparer.Ordinal).ToArray();
                verified.Add(new VerifiedReflectionBinding { SiteId = site.id, Assembly = site.assembly, TypeName = site.typeName,
                    MethodSignature = site.methodSignature, OperationIndex = operationIndex, OriginalMethodHash = variant.originalMethodHash,
                    OriginalMethod = original, GuardMethod = guard,
                    ConfigurationHash = configHash, AllowedTypes = allowed, Providers = Providers(site), Kind = ReflectionBindingConfiguration.KindOf(site),
                    ImageSha256 = site.imageSha256, ProviderAssemblyIdentity = site.providerAssemblyIdentity, ImagePath = site.imagePath });
            }
            foreach (var method in module.GetTypes().SelectMany(type => type.Methods))
                BindingChecks.Require(!method.Name.String.StartsWith(GuardPrefix, StringComparison.Ordinal) || expectedGuards.Contains(method),
                    "UnexpectedGuard", "Unverified reserved guard: " + method.FullName);
            return verified.ToArray();
        }

        private static ReflectionBindingSite[] Sites(ModuleDefMD module, ReflectionBindingConfiguration configuration)
        {
            BindingChecks.Require(module.Assembly != null, "NotAssembly", "A managed assembly is required.");
            return configuration.sites.Where(site => site.assembly == module.Assembly.Name.String).OrderBy(site => site.id, StringComparer.Ordinal).ToArray();
        }

        private static MethodDef FindMethod(ModuleDefMD module, ReflectionBindingSite site)
        {
            var types = module.GetTypes().Where(type => type.FullName == site.typeName).ToArray();
            BindingChecks.Require(types.Length == 1, "MissingConfiguredType", site.id);
            var methods = types[0].Methods.Where(method => ReflectionBindingFingerprint.MethodSignature(method) == site.methodSignature).ToArray();
            BindingChecks.Require(methods.Length == 1 && methods[0].HasBody, "MissingConfiguredMethod", site.id);
            BindingChecks.Require(!methods[0].HasGenericParameters && !types[0].HasGenericParameters, "UnsupportedConfiguredMethod", "Generic methods/declaring types are unsupported in version 1.");
            return methods[0];
        }

        private static OriginalSite ResolveOriginalSite(ModuleDefMD module, ReflectionBindingConfiguration configuration, ReflectionBindingSite site)
        {
            var method = FindMethod(module, site);
            string methodHash = ReflectionBindingFingerprint.Compute(method);
            var variants = configuration.MethodVariants(site);
            var matches = variants.Where(value => value.originalMethodHash == methodHash).ToArray();
            if (matches.Length != 1)
            {
                bool wrongLookup = variants.Select(value => value.operationIndex).Distinct().Where(index => index < method.Body.Instructions.Count)
                    .Select(index => method.Body.Instructions[index]).Any(candidate => candidate.OpCode.Code == OriginalCode(site) &&
                        IsSiteAcquisition(candidate.Operand as IMethod, site) && !IsExactAcquisition(candidate.Operand as IMethod, method.Module, site));
                BindingChecks.Require(!wrongLookup, "WrongLookupOverload", site.id);
            }
            BindingChecks.Require(matches.Length == 1, "OriginalMethodChanged", "Compiled method does not match exactly one approved compiler variant: " + site.id);
            var variant = matches[0];
            BindingChecks.Require(variant.operationIndex < method.Body.Instructions.Count, "MissingLookup", site.id);
            var operation = method.Body.Instructions[variant.operationIndex];
            BindingChecks.Require(operation.OpCode.Code == OriginalCode(site) && IsExactAcquisition(operation.Operand as IMethod, method.Module, site), "WrongLookupOverload", site.id);
            BindingChecks.Require(method.Body.Instructions.Count(instruction => IsSiteAcquisition(instruction.Operand as IMethod, site)) == 1, "AdditionalLookup", site.id);
            if (variant.operationIndex > 0)
                BindingChecks.Require(method.Body.Instructions[variant.operationIndex - 1].OpCode.OpCodeType != OpCodeType.Prefix, "UnsupportedLookupPrefix", site.id);
            return new OriginalSite { Site = site, Variant = variant, Method = method };
        }

        private static bool IsAnyTypeLookup(IMethod method) { return method != null && method.Name == "GetType" && method.DeclaringType.FullName == "System.Type"; }

        private static bool IsExactTypeLookup(IMethod method, ModuleDef module)
        {
            if (!IsAnyTypeLookup(method) || method is MethodSpec || method.MethodSig == null) return false;
            var signature = method.MethodSig;
            return signature.CallingConvention == CallingConvention.Default && signature.GenParamCount == 0 && signature.Params.Count == 1 &&
                signature.ParamsAfterSentinel == null && signature.Params[0].ElementType == ElementType.String && signature.RetType.FullName == "System.Type" &&
                method.DeclaringType is TypeRef && method.DeclaringType.DefinitionAssembly != null &&
                method.DeclaringType.DefinitionAssembly.FullName == module.CorLibTypes.AssemblyRef.FullName &&
                signature.RetType.ToTypeDefOrRef() != null && signature.RetType.ToTypeDefOrRef().DefinitionAssembly.FullName == method.DeclaringType.DefinitionAssembly.FullName;
        }

        private static string GuardName(ReflectionBindingSite site, string hash)
        { return GuardPrefix + hash + "_" + BindingChecks.Sha256(System.Text.Encoding.UTF8.GetBytes(site.id)); }

        private static MethodDef CreateGuard(ModuleDef module, ReflectionBindingSite site, string hash, IMethod lookup)
        {
            if (ReflectionBindingConfiguration.KindOf(site) != "TypeGetType") return CreateAcquisitionGuard(module, site, hash, lookup);
            var guard = new MethodDefUser(GuardName(site, hash), MethodSig.CreateStatic(lookup.MethodSig.RetType, lookup.MethodSig.Params[0]),
                MethodImplAttributes.IL | MethodImplAttributes.Managed, MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.HideBySig)
            { Body = new CilBody { InitLocals = true, MaxStack = 3 } };
            var stringType = module.CorLibTypes.String.TypeDefOrRef;
            var comparison = new TypeRefUser(module, "System", "StringComparison", module.CorLibTypes.AssemblyRef);
            var equals = new MemberRefUser(module, "Equals", MethodSig.CreateStatic(module.CorLibTypes.Boolean, module.CorLibTypes.String,
                module.CorLibTypes.String, new ValueTypeSig(comparison)), stringType);
            var exception = new TypeRefUser(module, "System", "InvalidOperationException", module.CorLibTypes.AssemblyRef);
            var constructor = new MemberRefUser(module, ".ctor", MethodSig.CreateInstance(module.CorLibTypes.Void, module.CorLibTypes.String), exception);
            var instructions = guard.Body.Instructions;
            foreach (string allowed in site.allowedTypes.OrderBy(value => value, StringComparer.Ordinal))
            {
                var next = Instruction.Create(OpCodes.Nop);
                instructions.Add(Instruction.Create(OpCodes.Ldarg_0)); instructions.Add(Instruction.Create(OpCodes.Ldstr, allowed));
                instructions.Add(Instruction.CreateLdcI4(4)); // StringComparison.Ordinal
                instructions.Add(Instruction.Create(OpCodes.Call, equals)); instructions.Add(Instruction.Create(OpCodes.Brfalse, next));
                instructions.Add(Instruction.Create(OpCodes.Ldstr, allowed)); instructions.Add(Instruction.Create(OpCodes.Call, lookup));
                instructions.Add(Instruction.Create(OpCodes.Ret)); instructions.Add(next);
            }
            instructions.Add(Instruction.Create(OpCodes.Ldstr, "AssemblyShadow reflection denied; configuration=" + hash + "; site=" + site.id));
            instructions.Add(Instruction.Create(OpCodes.Newobj, constructor)); instructions.Add(Instruction.Create(OpCodes.Throw));
            return guard;
        }

        private static ModuleDefMD Load(byte[] pe, byte[] pdb)
        {
            BindingChecks.Require(pe != null && pe.Length > 0, "MissingAssembly", "PE bytes are required.");
            var module = ModuleDefMD.Load(pe, new ModuleCreationOptions { TryToLoadPdbFromDisk = false, PdbFileOrData = pdb != null && pdb.Length != 0 ? pdb : null });
            if (pdb != null && pdb.Length != 0 && module.PdbState == null)
            { module.Dispose(); throw new ReflectionBindingException("InvalidPdb", "Supplied symbols could not be read."); }
            return module;
        }

        private static void RequireUnsigned(ModuleDef module)
        {
            BindingChecks.Require(module.Assembly != null && !module.IsStrongNameSigned && (module.Assembly.PublicKey == null || module.Assembly.PublicKey.Data.Length == 0),
                "StrongNameUnsupported", "Signed/delay-signed assemblies require an explicit signing implementation.");
        }

        private static string SequencePoints(ModuleDef module)
        {
            using (var hash = new BindingHash("original-sequence-points:1"))
            {
                foreach (var method in module.GetTypes().SelectMany(type => type.Methods).Where(method => method.HasBody && !method.Name.String.StartsWith(GuardPrefix, StringComparison.Ordinal)).OrderBy(method => method.FullName, StringComparer.Ordinal))
                    for (int index = 0; index < method.Body.Instructions.Count; index++)
                    {
                        var point = method.Body.Instructions[index].SequencePoint; if (point == null) continue;
                        hash.Add(method.FullName); hash.Add(index); hash.Add(point.Document.Url); hash.Add(point.StartLine); hash.Add(point.StartColumn);
                        hash.Add(point.EndLine); hash.Add(point.EndColumn); hash.Add(point.Document.CheckSumAlgorithmId); hash.Add(BitConverter.ToString(point.Document.CheckSum ?? new byte[0]));
                    }
                return hash.Finish();
            }
        }
    }
}
