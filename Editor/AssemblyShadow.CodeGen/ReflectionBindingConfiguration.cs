using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace HybridCLR.AssemblyShadow.CodeGen
{
    public sealed class ReflectionBindingException : Exception
    {
        public string Code { get; private set; }
        public ReflectionBindingException(string code, string message) : base(code + ": " + message) { Code = code; }
    }

    [DataContract]
    public sealed class ReflectionBindingSite
    {
        [DataMember(IsRequired = true)] public string id;
        [DataMember(IsRequired = true)] public string assembly;
        [DataMember(IsRequired = true)] public string typeName;
        [DataMember(IsRequired = true)] public string methodSignature;
        [DataMember(IsRequired = true)] public string originalMethodHash;
        [DataMember(IsRequired = true)] public int operationIndex;
        [DataMember(IsRequired = true)] public string[] allowedTypes;
        [DataMember(IsRequired = true)] public string reason;
        [DataMember(EmitDefaultValue = false)] public string kind;
        [DataMember(EmitDefaultValue = false)] public string imageSha256;
        [DataMember(EmitDefaultValue = false)] public string providerAssemblyIdentity;
        [DataMember(EmitDefaultValue = false)] public string imagePath;
    }

    [DataContract]
    public sealed class ReflectionBindingConfiguration
    {
        public const string ProjectRelativePath = "ProjectSettings/AssemblyShadowReflectionBindings.json";
        [DataMember(IsRequired = true)] public int schemaVersion = 1;
        [DataMember(IsRequired = true)] public int transformerVersion = 1;
        [DataMember(IsRequired = true)] public ReflectionBindingSite[] sites = new ReflectionBindingSite[0];

        public static ReflectionBindingConfiguration Parse(byte[] utf8Json)
        {
            BindingChecks.Require(utf8Json != null && utf8Json.Length > 0 && utf8Json.Length <= 8 * 1024 * 1024, "InvalidConfiguration", "Expected bounded UTF-8 JSON.");
            ReflectionBindingConfiguration result;
            try
            {
                using (var stream = new MemoryStream(utf8Json, false))
                    result = (ReflectionBindingConfiguration)new DataContractJsonSerializer(typeof(ReflectionBindingConfiguration)).ReadObject(stream);
            }
            catch (Exception error) { throw new ReflectionBindingException("InvalidConfiguration", error.Message); }
            BindingChecks.Require(result != null, "InvalidConfiguration", "Configuration was null."); result.Validate(); return result;
        }

        public void Validate()
        {
            BindingChecks.Require(((schemaVersion == 1 && transformerVersion == 1) || (schemaVersion == 2 && transformerVersion == 2)) && sites != null && sites.Length > 0 && sites.Length <= 4096,
                "InvalidConfiguration", "Supported matching schema/transformer versions are 1 and 2, with at least one site.");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            var methods = new HashSet<string>(StringComparer.Ordinal);
            foreach (var site in sites)
            {
                BindingChecks.Require(site != null && IsIdentifier(site.id) && IsAssemblyName(site.assembly) && !string.IsNullOrWhiteSpace(site.typeName) &&
                    !string.IsNullOrWhiteSpace(site.methodSignature) && BindingChecks.IsHash(site.originalMethodHash) && site.operationIndex >= 0 &&
                    site.allowedTypes != null && site.allowedTypes.Length <= 4096 && !string.IsNullOrWhiteSpace(site.reason), "InvalidSite", "Site identity, signature, hash, index, targets and reason are mandatory.");
                BindingChecks.Require(ids.Add(site.id), "DuplicateSite", site.id);
                BindingChecks.Require(methods.Add(site.assembly + "\n" + site.typeName + "\n" + site.methodSignature), "DuplicateMethodSite", "Version 1 permits only one lookup site per method.");
                string kind = KindOf(site);
                BindingChecks.Require((schemaVersion == 1 && kind == "TypeGetType") || (schemaVersion == 2 &&
                    new[] { "TypeGetType", "FiniteAssemblyList", "FiniteAssemblyTypes", "FixedAssemblyBytes" }.Contains(site.kind, StringComparer.Ordinal)),
                    "InvalidAcquisitionKind", site.id);
                if (kind == "FixedAssemblyBytes")
                {
                    BindingChecks.Require(site.allowedTypes.Length == 0 && BindingChecks.IsHash(site.imageSha256) && IsSafeImagePath(site.imagePath), "InvalidFixedImage", site.id);
                    RequireFullAssemblyIdentity(site.providerAssemblyIdentity);
                }
                else BindingChecks.Require(string.IsNullOrEmpty(site.imageSha256) && string.IsNullOrEmpty(site.imagePath) && string.IsNullOrEmpty(site.providerAssemblyIdentity), "UnexpectedFixedImage", site.id);
                var targets = new HashSet<string>(StringComparer.Ordinal);
                foreach (string target in site.allowedTypes)
                {
                    ProviderOf(target);
                    if (kind == "FiniteAssemblyList" || kind == "FiniteAssemblyTypes")
                        RequireFullAssemblyIdentity(target.Substring(target.IndexOf(',') + 2));
                    BindingChecks.Require(targets.Add(target), "DuplicateAllowedType", site.id);
                }
            }
        }

        public string ComputeHash()
        {
            Validate();
            using (var hash = new BindingHash("assembly-shadow-reflection-configuration:" + schemaVersion))
            {
                hash.Add(schemaVersion); hash.Add(transformerVersion); hash.Add(sites.Length);
                foreach (var site in sites.OrderBy(site => site.id, StringComparer.Ordinal))
                {
                    hash.Add(site.id); hash.Add(site.assembly); hash.Add(site.typeName); hash.Add(site.methodSignature); hash.Add(site.originalMethodHash);
                    hash.Add(site.operationIndex); hash.Add(site.reason); hash.Add(site.allowedTypes.Length);
                    foreach (string target in site.allowedTypes.OrderBy(value => value, StringComparer.Ordinal)) hash.Add(target);
                    if (schemaVersion == 2) { hash.Add(site.kind); hash.Add(site.imageSha256); hash.Add(site.providerAssemblyIdentity); hash.Add(site.imagePath); }
                }
                return hash.Finish();
            }
        }

        public bool Targets(string assemblyName) { Validate(); return sites.Any(site => site.assembly == assemblyName); }

        public static string KindOf(ReflectionBindingSite site) { return string.IsNullOrEmpty(site.kind) ? "TypeGetType" : site.kind; }

        public void ValidateImageEvidence(IReadOnlyDictionary<string, byte[]> images)
        {
            Validate();
            foreach (var site in sites.Where(value => KindOf(value) == "FixedAssemblyBytes"))
            {
                byte[] bytes;
                BindingChecks.Require(images != null && images.TryGetValue(site.imagePath, out bytes), "FixedImageEvidenceMissing", site.imagePath);
                bytes = images[site.imagePath];
                BindingChecks.Require(bytes != null && BindingChecks.Sha256(bytes) == site.imageSha256, "FixedImageHashMismatch", site.id);
                using (var module = dnlib.DotNet.ModuleDefMD.Load(bytes, new dnlib.DotNet.ModuleCreationOptions { TryToLoadPdbFromDisk = false }))
                    BindingChecks.Require(module.Assembly != null && module.Assembly.FullName == site.providerAssemblyIdentity, "FixedImageIdentityMismatch", site.id);
            }
        }

        private static bool IsSafeImagePath(string path)
        { return !string.IsNullOrWhiteSpace(path) && path.IndexOf('\\') < 0 && path.IndexOf(':') < 0 && !Path.IsPathRooted(path) &&
            path.Split('/').All(part => part.Length > 0 && part != "." && part != ".."); }

        private static void RequireFullAssemblyIdentity(string identity)
        {
            BindingChecks.Require(!string.IsNullOrWhiteSpace(identity), "InvalidAssemblyIdentity", "A complete assembly identity is required.");
            ProviderOf("Binding.Anchor, " + identity);
            BindingChecks.Require(identity.Split(',').Length == 4 && new dnlib.DotNet.AssemblyNameInfo(identity).FullName == identity,
                "InvalidAssemblyIdentity", identity);
        }

        public static string ProviderOf(string assemblyQualifiedType)
        {
            BindingChecks.Require(!string.IsNullOrEmpty(assemblyQualifiedType) && assemblyQualifiedType.Length <= 4096, "InvalidAllowedType", "An exact assembly-qualified concrete type is required.");
            string[] parts = assemblyQualifiedType.Split(',').Select(part => part.Trim()).ToArray();
            BindingChecks.Require((parts.Length == 2 || parts.Length == 5) && assemblyQualifiedType == string.Join(", ", parts) &&
                Regex.IsMatch(parts[0], @"\A[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*(?:\+[A-Za-z_][A-Za-z0-9_]*)*\z") && IsAssemblyName(parts[1]),
                "InvalidAllowedType", "Generic, pointer, byref, array, unqualified and malformed type targets are unsupported.");
            if (parts.Length == 5)
            {
                BindingChecks.Require(Regex.IsMatch(parts[2], @"\AVersion=[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+\z") &&
                    Regex.IsMatch(parts[3], @"\ACulture=(?:neutral|[A-Za-z0-9]+(?:-[A-Za-z0-9]+)*)\z") &&
                    Regex.IsMatch(parts[4], @"\APublicKeyToken=(?:null|[0-9a-f]{16})\z"), "InvalidAllowedType", "Malformed assembly identity.");
                Version version;
                BindingChecks.Require(Version.TryParse(parts[2].Substring(8), out version), "InvalidAllowedType", "Invalid assembly version.");
            }
            return parts[1];
        }

        private static bool IsIdentifier(string value) { return value != null && Regex.IsMatch(value, @"\A[A-Za-z0-9_.-]{1,128}\z"); }
        private static bool IsAssemblyName(string value) { return value != null && Regex.IsMatch(value, @"\A[A-Za-z_][A-Za-z0-9_.-]*\z") && !value.EndsWith(".dll", StringComparison.OrdinalIgnoreCase); }
    }

    public static class ReflectionBindingDefines
    {
        public const string Prefix = "ASSEMBLY_SHADOW_REFLECTION_BINDINGS_";
        public static string Create(byte[] rawConfiguration) { return Prefix + BindingChecks.Sha256(rawConfiguration); }
        public static bool TryGetEnabledHash(IEnumerable<string> defines, out string rawFileHash)
        {
            string[] values = (defines ?? new string[0]).ToArray(); rawFileHash = null;
            if (values.Contains("UNITY_EDITOR", StringComparer.Ordinal)) return false;
            // Unity may forward the same control through multiple define sources.
            // Only conflicting distinct controls are ambiguous; repetitions carry
            // the same raw-file identity and must be idempotent.
            var matches = values.Where(value => value != null && value.StartsWith(Prefix, StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
            if (matches.Length == 0) return false;
            BindingChecks.Require(matches.Length == 1, "AmbiguousBindingDefine", "Exactly one distinct binding-control define is allowed.");
            rawFileHash = matches[0].Substring(Prefix.Length);
            BindingChecks.Require(BindingChecks.IsHash(rawFileHash), "InvalidBindingDefine", "Binding define must contain the lowercase raw-file SHA256.");
            return true;
        }
    }

    internal static class BindingChecks
    {
        internal static void Require(bool condition, string code, string message) { if (!condition) throw new ReflectionBindingException(code, message); }
        internal static bool IsHash(string value) { return value != null && Regex.IsMatch(value, @"\A[0-9a-f]{64}\z"); }
        internal static string Sha256(byte[] bytes)
        {
            Require(bytes != null, "MissingBytes", "Input bytes are required.");
            using (var sha = SHA256.Create()) return string.Concat(sha.ComputeHash(bytes).Select(value => value.ToString("x2", CultureInfo.InvariantCulture)));
        }
    }

    // Every value is UTF-8 with a signed 32-bit little-endian byte length (-1=null).
    internal sealed class BindingHash : IDisposable
    {
        internal Func<dnlib.DotNet.ITypeDefOrRef, string> TypeScope;
        private readonly MemoryStream stream = new MemoryStream();
        private readonly BinaryWriter writer;
        internal BindingHash(string domain) { writer = new BinaryWriter(stream, Encoding.UTF8, true); Add(domain); }
        internal void Add(object value)
        {
            string text = value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
            if (text == null) { writer.Write(-1); return; }
            byte[] bytes = Encoding.UTF8.GetBytes(text); writer.Write(bytes.Length); writer.Write(bytes);
        }
        internal string Finish() { writer.Flush(); return BindingChecks.Sha256(stream.ToArray()); }
        public void Dispose() { writer.Dispose(); stream.Dispose(); }
    }
}
