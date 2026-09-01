using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using dnlib.DotNet;

namespace HybridCLR.AssemblyShadow.CodeGen
{
    [DataContract]
    public sealed class RawTypeAdmissionMethodVariant
    {
        [DataMember(IsRequired = true)] public string compilerMode;
        [DataMember(IsRequired = true)] public string methodHash;
        [DataMember(IsRequired = true)] public int operationIndex;
    }

    [DataContract]
    public sealed class RawTypeAdmissionSite
    {
        [DataMember(IsRequired = true)] public string id;
        [DataMember(IsRequired = true)] public string consumerAssembly;
        [DataMember(IsRequired = true)] public string declaringType;
        [DataMember(IsRequired = true)] public string methodSignature;
        [DataMember(EmitDefaultValue = false)] public string methodHash;
        [DataMember(EmitDefaultValue = false)] public int? operationIndex;
        [DataMember(EmitDefaultValue = false)] public RawTypeAdmissionMethodVariant[] compilerVariants;
        [DataMember(IsRequired = true)] public string operationSignature;
        [DataMember(IsRequired = true)] public string providerAssemblyIdentity;
        [DataMember(IsRequired = true)] public string typeName;
        [DataMember(IsRequired = true)] public bool throwOnError;
        [DataMember(IsRequired = true)] public bool ignoreCase;
        [DataMember(IsRequired = true)] public string reason;
    }

    // This domain admits a literal-bound, real API operation. It neither rewrites
    // the result nor proves native lifecycle state. Old guard schemas are separate.
    [DataContract]
    public sealed class RawTypeAdmissionConfiguration
    {
        public const string ProjectRelativePath = "ProjectSettings/AssemblyShadowRawTypeAdmissions.json";
        public const string Policy = "assembly-shadow-raw-type-admission:1";
        public const string PolicyV2 = "assembly-shadow-raw-type-admission:2";
        public const string DevelopmentCompilerMode = "Development";
        public const string ReleaseCompilerMode = "Release";
        [DataMember(IsRequired = true)] public int schemaVersion = 1;
        [DataMember(IsRequired = true)] public string policy = Policy;
        [DataMember(IsRequired = true)] public RawTypeAdmissionSite[] sites = new RawTypeAdmissionSite[0];

        public static RawTypeAdmissionConfiguration Parse(byte[] utf8Json)
        {
            var value = RawTypeAdmissionJson.Parse(utf8Json); value.Validate(); return value;
        }

        public void Validate()
        {
            bool legacy = schemaVersion == 1 && policy == Policy;
            bool compilerBound = schemaVersion == 2 && policy == PolicyV2;
            BindingChecks.Require((legacy || compilerBound) && sites != null && sites.Length > 0 && sites.Length <= 4096,
                "InvalidRawAdmissionConfiguration", "Expected matching raw admission schema/policy 1 or 2 and 1..4096 sites.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var operations = new HashSet<string>(StringComparer.Ordinal);
            var methodProfiles = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var site in sites)
            {
                BindingChecks.Require(site != null && Match(site.id, @"[A-Za-z0-9_.-]{1,128}") &&
                    Match(site.consumerAssembly, @"[A-Za-z_][A-Za-z0-9_.-]*") && !site.consumerAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                    Text(site.declaringType) && Text(site.methodSignature) &&
                    Text(site.operationSignature) && Text(site.providerAssemblyIdentity) && site.typeName != null && Text(site.reason),
                    "InvalidRawAdmissionSite", "A complete raw admission site identity is required.");
                RawTypeAdmissionMethodVariant[] variants;
                if (legacy)
                {
                    BindingChecks.Require(BindingChecks.IsHash(site.methodHash) && site.operationIndex.HasValue && site.operationIndex.Value >= 0 &&
                        (site.compilerVariants == null || site.compilerVariants.Length == 0),
                        "InvalidRawAdmissionMethodVariants", "Schema 1 requires one legacy method hash/index and no compiler variants: " + site.id);
                    variants = new[] { new RawTypeAdmissionMethodVariant { compilerMode = "Legacy", methodHash = site.methodHash, operationIndex = site.operationIndex.Value } };
                }
                else
                {
                    BindingChecks.Require(site.methodHash == null && !site.operationIndex.HasValue && site.compilerVariants != null && site.compilerVariants.Length == 2,
                        "InvalidRawAdmissionMethodVariants", "Schema 2 requires exactly Development and Release compiler variants: " + site.id);
                    var modes = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var variant in site.compilerVariants)
                        BindingChecks.Require(variant != null && (variant.compilerMode == DevelopmentCompilerMode || variant.compilerMode == ReleaseCompilerMode) &&
                            modes.Add(variant.compilerMode) && BindingChecks.IsHash(variant.methodHash) && variant.operationIndex >= 0,
                            "InvalidRawAdmissionMethodVariants", "Compiler variants require unique known modes, exact hashes and non-negative indices: " + site.id);
                    BindingChecks.Require(modes.SetEquals(new[] { DevelopmentCompilerMode, ReleaseCompilerMode }),
                        "InvalidRawAdmissionMethodVariants", "Both compiler modes are required: " + site.id);
                    variants = site.compilerVariants;
                }
                string provider = ReflectionBindingConfiguration.ProviderOf("Admission.Anchor, " + site.providerAssemblyIdentity);
                BindingChecks.Require(site.providerAssemblyIdentity.Split(',').Length == 4 &&
                    new AssemblyNameInfo(site.providerAssemblyIdentity).FullName == site.providerAssemblyIdentity,
                    "InvalidRawAdmissionIdentity", site.id);
                string kind = KindOf(site.operationSignature);
                BindingChecks.Require(kind != null, "UnsupportedRawAdmissionOperation", site.operationSignature);
                if (kind == "Module.GetType")
                {
                    ReflectionBindingConfiguration.ProviderOf(site.typeName + ", " + provider);
                    BindingChecks.Require(site.throwOnError && !site.ignoreCase, "InvalidRawAdmissionFlags", site.id);
                }
                else BindingChecks.Require(site.typeName == "" && !site.throwOnError && !site.ignoreCase, "InvalidRawAdmissionFlags", site.id);
                BindingChecks.Require(ids.Add(site.id), "DuplicateRawAdmissionSite", site.id);
                string method = site.consumerAssembly.ToLowerInvariant() + "\n" + site.declaringType + "\n" + site.methodSignature;
                foreach (var variant in variants)
                    BindingChecks.Require(operations.Add(method + "\n" + variant.compilerMode + "\n" + variant.operationIndex), "DuplicateRawAdmissionOperation", site.id);
                string profile = string.Join("\n", variants.OrderBy(value => value.compilerMode, StringComparer.Ordinal)
                    .Select(value => value.compilerMode + ":" + value.methodHash));
                string prior;
                BindingChecks.Require(!methodProfiles.TryGetValue(method, out prior) || prior == profile, "InconsistentRawAdmissionMethod", site.id);
                methodProfiles[method] = profile;
            }
        }

        public string ComputeHash()
        {
            Validate();
            using (var hash = new BindingHash(policy))
            {
                hash.Add(schemaVersion); hash.Add(policy); hash.Add(sites.Length);
                foreach (var site in sites.OrderBy(value => value.id, StringComparer.Ordinal))
                {
                    hash.Add(site.id); hash.Add(site.consumerAssembly); hash.Add(site.declaringType); hash.Add(site.methodSignature);
                    if (schemaVersion == 1) { hash.Add(site.methodHash); hash.Add(site.operationIndex.Value); }
                    else
                    {
                        var variants = site.compilerVariants.OrderBy(value => value.compilerMode, StringComparer.Ordinal).ToArray();
                        hash.Add(variants.Length);
                        foreach (var variant in variants) { hash.Add(variant.compilerMode); hash.Add(variant.methodHash); hash.Add(variant.operationIndex); }
                    }
                    hash.Add(site.operationSignature); hash.Add(site.providerAssemblyIdentity);
                    hash.Add(site.typeName); hash.Add(site.throwOnError); hash.Add(site.ignoreCase); hash.Add(site.reason);
                }
                return hash.Finish();
            }
        }

        public RawTypeAdmissionMethodVariant MethodVariant(RawTypeAdmissionSite site, string compilerMode)
        {
            Validate();
            BindingChecks.Require(site != null && sites.Contains(site), "InvalidRawAdmissionSite", "The site must belong to this configuration.");
            if (schemaVersion == 1)
                return new RawTypeAdmissionMethodVariant { compilerMode = "Legacy", methodHash = site.methodHash, operationIndex = site.operationIndex.Value };
            BindingChecks.Require(compilerMode == DevelopmentCompilerMode || compilerMode == ReleaseCompilerMode,
                "RawAdmissionCompilerModeMissing", "Schema 2 requires an explicit Development or Release compiler mode.");
            return site.compilerVariants.Single(value => value.compilerMode == compilerMode);
        }

        public static string CompilerMode(bool developmentBuild)
        { return developmentBuild ? DevelopmentCompilerMode : ReleaseCompilerMode; }

        public static string KindOf(string signature)
        {
            switch (signature)
            {
                case "System.Type[] System.Reflection.Assembly::GetTypes()": return "Assembly.GetTypes";
                case "System.Collections.Generic.IEnumerable`1<System.Reflection.TypeInfo> System.Reflection.Assembly::get_DefinedTypes()": return "Assembly.get_DefinedTypes";
                case "System.Collections.Generic.IEnumerable`1<System.Type> System.Reflection.Assembly::get_ExportedTypes()": return "Assembly.get_ExportedTypes";
                case "System.Type[] System.Reflection.Module::GetTypes()": return "Module.GetTypes";
                case "System.Type System.Reflection.Module::GetType(System.String,System.Boolean,System.Boolean)": return "Module.GetType";
                default: return null;
            }
        }

        private static bool Text(string value) { return !string.IsNullOrWhiteSpace(value) && value.Length <= 16384 && value.All(c => !char.IsControl(c)); }
        private static bool Match(string value, string pattern) { return value != null && Regex.IsMatch(value, "\\A(?:" + pattern + ")\\z"); }
    }

    public static class RawTypeAdmissionDefines
    {
        public const string Prefix = "ASSEMBLY_SHADOW_RAW_TYPE_ADMISSION_";
        public static string Create(byte[] rawConfiguration) { return Prefix + BindingChecks.Sha256(rawConfiguration); }
        public static bool TryGetEnabledHash(IEnumerable<string> defines, out string rawFileHash)
        {
            string[] values = (defines ?? new string[0]).ToArray(); rawFileHash = null;
            // This is an evidence domain, not an ILPP opt-out. A present control
            // must be validated even if UNITY_EDITOR is also present.
            string[] controls = values.Where(value => value != null && value.StartsWith(Prefix, StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
            if (controls.Length == 0) return false;
            BindingChecks.Require(controls.Length == 1, "AmbiguousRawAdmissionDefine", "Expected one distinct raw admission control.");
            rawFileHash = controls[0].Substring(Prefix.Length);
            BindingChecks.Require(BindingChecks.IsHash(rawFileHash), "InvalidRawAdmissionDefine", "Expected lowercase raw-file SHA256.");
            return true;
        }
    }
}
