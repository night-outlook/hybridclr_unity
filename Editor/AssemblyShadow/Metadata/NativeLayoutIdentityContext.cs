using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;

namespace HybridCLR.Editor.AssemblyShadow
{
    [Serializable]
    public sealed class NativeLayoutIdentityFile
    {
        public string assemblyIdentity, sha256, mvid;
    }

    [Serializable]
    public sealed class NativeLayoutResolvedType
    {
        public string declaredAssemblyIdentity, declaration, definitionAssemblyIdentity;
        public string declaredModuleSha256, definitionModuleSha256, canonicalKey;
        public string[] forwardingPath;
        public bool runtimeFacadeUsed;
    }

    /// <summary>
    /// Closed-world, metadata-only identity domain. The caller must authenticate
    /// every supplied image; this class is not itself a provenance certificate.
    /// Production constructs it only from reverified linked/compiler receipts.
    /// No ambient dnlib resolver, GAC, Assembly.Load or code execution is used.
    /// All inputs are cloned and all parsed modules remain privately owned.
    /// </summary>
    public sealed class NativeLayoutIdentityContext : IDisposable
    {
        public const string Profile = "CapturedNativeLayoutIdentityV1";
        public const string NetstandardIdentity = "netstandard, Version=2.1.0.0, Culture=neutral, PublicKeyToken=cc7b13ffcd2ddd51";
        private const int MaximumDepth = 128;
        private sealed class Image
        {
            internal ModuleDefMD module;
            internal string hash;
            internal readonly Dictionary<string, TypeDef> definitions = new Dictionary<string, TypeDef>(StringComparer.Ordinal);
            internal readonly Dictionary<string, AssemblyRef> forwards = new Dictionary<string, AssemblyRef>(StringComparer.Ordinal);
        }
        private readonly Dictionary<string, Image> images = new Dictionary<string, Image>(StringComparer.Ordinal);
        private readonly Dictionary<string, NativeLayoutResolvedType> observations = new Dictionary<string, NativeLayoutResolvedType>(StringComparer.Ordinal);
        private Image runtimeFacade;
        private NativeLayoutIdentityContext runtime;
        private bool disposed;
        public string RuntimeFacadeSha256 { get; private set; }
        public string CompilerFacadeSha256 { get; private set; }

        private NativeLayoutIdentityContext() { }

        public static NativeLayoutIdentityContext Load(IEnumerable<byte[]> capturedImages)
        {
            ShadowHash.Require(capturedImages != null, "NativeLayoutResolutionInput", "A complete captured inventory is required.");
            var result = new NativeLayoutIdentityContext();
            try
            {
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var bytes in capturedImages)
                {
                    var image = Read(bytes);
                    try
                    {
                        string identity = image.module.Assembly.FullName;
                        ShadowHash.Require(names.Add(AssemblyIdentityUtil.CanonicalName(image.module.Assembly.Name)) && !result.images.ContainsKey(identity),
                            "NativeLayoutResolutionAmbiguous", identity);
                        result.images.Add(identity, image);
                    }
                    catch { image.module.Dispose(); throw; }
                }
                ShadowHash.Require(result.images.Count != 0, "NativeLayoutResolutionInput", "Empty captured inventory.");
                return result;
            }
            catch { result.Dispose(); throw; }
        }

        // This maps a verified compiler *reference definition* through the actual
        // captured runtime facade. Merely sharing a framework name is insufficient.
        // The production factory also requires VerifiedTargetFrameworkReferences.
        public static NativeLayoutIdentityContext LoadCompiler(IEnumerable<byte[]> capturedImages,
            NativeLayoutIdentityContext linked, byte[] capturedRuntimeFacade, string expectedRuntimeFacadeSha256,
            string verifiedCompilerFacadeSha256)
        {
            ShadowHash.Require(linked != null && !linked.disposed, "NativeLayoutResolutionInput", "Live linked identity inventory required.");
            var result = Load(capturedImages);
            try
            {
                ShadowHash.Require(capturedRuntimeFacade != null && ShadowHash.Bytes(capturedRuntimeFacade) == expectedRuntimeFacadeSha256,
                    "NativeLayoutResolutionFacadeHash", "Runtime facade changed.");
                result.runtimeFacade = Read(capturedRuntimeFacade);
                Image compilerFacade;
                ShadowHash.Require(result.runtimeFacade.module.Assembly.FullName == NetstandardIdentity &&
                    result.images.TryGetValue(NetstandardIdentity, out compilerFacade) && compilerFacade.hash == verifiedCompilerFacadeSha256,
                    "NativeLayoutResolutionFacadeIdentity", "The exact captured compiler and runtime facade identities/hashes are required.");
                ShadowHash.Require(result.runtimeFacade.forwards.Count > 0 && result.runtimeFacade.definitions.Values.All(t => t.IsGlobalModuleType),
                    "NativeLayoutResolutionFacadeShape", "The runtime mapping must be an actual forwarding facade, not a same-named definition library.");
                result.RuntimeFacadeSha256 = expectedRuntimeFacadeSha256;
                result.CompilerFacadeSha256 = verifiedCompilerFacadeSha256;
                result.runtime = linked;
                return result;
            }
            catch { result.Dispose(); throw; }
        }

        public NativeLayoutIdentityFile[] Files
        {
            get
            {
                CheckLive();
                return images.Values.OrderBy(i => i.module.Assembly.FullName, StringComparer.Ordinal).Select(i => new NativeLayoutIdentityFile
                { assemblyIdentity = i.module.Assembly.FullName, sha256 = i.hash, mvid = i.module.Mvid.HasValue ? i.module.Mvid.Value.ToString("D") : "" }).ToArray();
            }
        }

        public NativeLayoutResolvedType[] Resolutions
        {
            get
            {
                CheckLive();
                return observations.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new NativeLayoutResolvedType
                {
                    declaredAssemblyIdentity = p.Value.declaredAssemblyIdentity, declaration = p.Value.declaration,
                    definitionAssemblyIdentity = p.Value.definitionAssemblyIdentity, declaredModuleSha256 = p.Value.declaredModuleSha256,
                    definitionModuleSha256 = p.Value.definitionModuleSha256, canonicalKey = p.Value.canonicalKey,
                    forwardingPath = (string[])p.Value.forwardingPath.Clone(), runtimeFacadeUsed = p.Value.runtimeFacadeUsed,
                }).ToArray();
            }
        }

        internal ModuleDefMD Input(byte[] bytes)
        {
            CheckLive();
            string hash = ShadowHash.Bytes(bytes);
            var matches = images.Values.Where(i => i.hash == hash).ToArray();
            ShadowHash.Require(matches.Length == 1, "NativeLayoutResolutionInput", "Compared bytes must belong to exactly this captured inventory.");
            return matches[0].module;
        }

        internal string TypeKey(ITypeDefOrRef type)
        {
            CheckLive();
            ShadowHash.Require(type != null && !(type is TypeSpec), "NativeLayoutResolutionInput", "A named metadata type is required.");
            // These types originate in our private comparison modules. Never
            // follow a caller-owned resolver or silently accept a netmodule scope.
            ShadowHash.Require(images.Values.Any(i => object.ReferenceEquals(i.module, type.Module)), "NativeLayoutResolutionOwner", type.FullName);
            var chain = Chain(type);
            IAssembly scope;
            var definition = type as TypeDef;
            if (definition != null) scope = definition.Module.Assembly;
            else
            {
                var outer = chain[0] as TypeRef;
                ShadowHash.Require(outer != null, "NativeLayoutResolutionScope", type.FullName);
                var assembly = outer.ResolutionScope as AssemblyRef;
                var module = outer.ResolutionScope as ModuleDef;
                ShadowHash.Require(assembly != null || (module != null && object.ReferenceEquals(module, type.Module)),
                    "NativeLayoutResolutionScope", "Unsupported or foreign module scope: " + type.FullName);
                scope = assembly != null ? (IAssembly)assembly : module.Assembly;
            }
            string declaration = Declaration(chain[0].Namespace, chain.Select(t => t.Name.String));
            var path = new List<string>();
            Image origin = null;
            ShadowHash.Require(scope != null && images.TryGetValue(scope.FullName, out origin), "NativeLayoutResolutionMissingAssembly", scope == null ? type.FullName : scope.FullName);
            var resolved = Resolve(scope, declaration, new HashSet<string>(StringComparer.Ordinal), path);
            Image destination = images[resolved.Module.Assembly.FullName];
            bool mapped = runtimeFacade != null && resolved.Module.Assembly.FullName == NetstandardIdentity;
            if (mapped)
            {
                AssemblyRef forward;
                ShadowHash.Require(runtimeFacade.forwards.TryGetValue(declaration, out forward), "NativeLayoutResolutionMissingForwarder", type.FullName);
                path.Add(NetstandardIdentity + " | runtime-facade=" + RuntimeFacadeSha256);
                var runtimeDefinition = runtime.Resolve(forward, declaration, new HashSet<string>(StringComparer.Ordinal), path);
                ShadowHash.Require(resolved.GenericParameters.Count == runtimeDefinition.GenericParameters.Count &&
                    resolved.IsInterface == runtimeDefinition.IsInterface && resolved.IsValueType == runtimeDefinition.IsValueType && resolved.IsEnum == runtimeDefinition.IsEnum,
                    "NativeLayoutResolutionDefinitionShape", "Facade destination kind/arity differs: " + type.FullName);
                resolved = runtimeDefinition;
                destination = runtime.images[resolved.Module.Assembly.FullName];
            }
            string key = DeclarationIdentity(resolved.Module.Assembly.FullName, declaration);
            string observationKey = DeclarationIdentity(scope.FullName, declaration);
            observations[observationKey] = new NativeLayoutResolvedType
            {
                declaredAssemblyIdentity = scope.FullName, declaration = declaration, definitionAssemblyIdentity = resolved.Module.Assembly.FullName,
                declaredModuleSha256 = origin.hash, definitionModuleSha256 = destination.hash, canonicalKey = key,
                forwardingPath = path.ToArray(), runtimeFacadeUsed = mapped,
            };
            return key;
        }

        private TypeDef Resolve(IAssembly requested, string declaration, HashSet<string> visited, List<string> path)
        {
            CheckLive();
            Image image = null;
            ShadowHash.Require(requested != null && images.TryGetValue(requested.FullName, out image), "NativeLayoutResolutionMissingAssembly", requested == null ? declaration : requested.FullName);
            ShadowHash.Require(AssemblyNameComparer.CompareAll.Equals(requested, image.module.Assembly), "NativeLayoutResolutionAssemblyIdentity", requested.FullName);
            string key = DeclarationIdentity(requested.FullName, declaration);
            ShadowHash.Require(visited.Count < MaximumDepth && visited.Add(key), "NativeLayoutResolutionCycle", key);
            path.Add(image.module.Assembly.FullName + " | sha256=" + image.hash);
            TypeDef found;
            if (image.definitions.TryGetValue(declaration, out found)) return found;
            AssemblyRef forward;
            ShadowHash.Require(image.forwards.TryGetValue(declaration, out forward), "NativeLayoutResolutionMissingType", key);
            return Resolve(forward, declaration, visited, path);
        }

        private static Image Read(byte[] supplied)
        {
            ShadowHash.Require(supplied != null && supplied.Length > 0, "NativeLayoutResolutionInput", "Empty image.");
            byte[] bytes = (byte[])supplied.Clone();
            var image = new Image { hash = ShadowHash.Bytes(bytes), module = ModuleDefMD.Load(bytes, new ModuleCreationOptions { TryToLoadPdbFromDisk = false }) };
            try
            {
                ShadowHash.Require(image.module.Assembly != null, "NativeLayoutResolutionInput", "Netmodules are not supported.");
                foreach (var type in image.module.GetTypes())
                {
                    var chain = Chain(type);
                    string declaration = Declaration(chain[0].Namespace, chain.Select(t => t.Name.String));
                    ShadowHash.Require(!image.definitions.ContainsKey(declaration), "NativeLayoutResolutionAmbiguous", declaration);
                    image.definitions.Add(declaration, type);
                }
                var exports = new HashSet<ExportedType>(image.module.ExportedTypes);
                ShadowHash.Require(exports.Count == image.module.ExportedTypes.Count, "NativeLayoutResolutionAmbiguous", "Duplicate export row.");
                foreach (var export in image.module.ExportedTypes)
                {
                    var names = new List<string>(); string ns; AssemblyRef destination;
                    Export(export, exports, new HashSet<ExportedType>(), names, out ns, out destination);
                    string declaration = Declaration(ns, names);
                    ShadowHash.Require(!image.definitions.ContainsKey(declaration) && !image.forwards.ContainsKey(declaration),
                        "NativeLayoutResolutionAmbiguous", declaration);
                    image.forwards.Add(declaration, destination);
                }
                return image;
            }
            catch { image.module.Dispose(); throw; }
        }

        private static void Export(ExportedType value, HashSet<ExportedType> exports, HashSet<ExportedType> visited,
            List<string> names, out string ns, out AssemblyRef destination)
        {
            ShadowHash.Require(value != null && exports.Contains(value) && visited.Count < MaximumDepth && visited.Add(value),
                "NativeLayoutResolutionForwarder", "Invalid nested export chain.");
            var parent = value.Implementation as ExportedType;
            if (parent != null)
            {
                ShadowHash.Require(string.IsNullOrEmpty(value.TypeNamespace.String), "NativeLayoutResolutionForwarder", "Nested export namespace.");
                Export(parent, exports, visited, names, out ns, out destination);
            }
            else
            {
                destination = value.Implementation as AssemblyRef;
                ShadowHash.Require(value.IsForwarder && destination != null, "NativeLayoutResolutionForwarder", "Explicit assembly forwarder required.");
                ns = value.TypeNamespace.String;
            }
            ShadowHash.Require(!string.IsNullOrEmpty(value.TypeName.String), "NativeLayoutResolutionForwarder", "Missing export name.");
            names.Add(value.TypeName.String);
        }

        private static ITypeDefOrRef[] Chain(ITypeDefOrRef type)
        {
            var result = new List<ITypeDefOrRef>();
            for (var at = type; at != null; at = at.DeclaringType)
            {
                ShadowHash.Require(result.Count < MaximumDepth && !result.Contains(at), "NativeLayoutResolutionDeclaration", "Recursive declaration.");
                result.Add(at);
            }
            result.Reverse();
            ShadowHash.Require(result.Count > 0, "NativeLayoutResolutionDeclaration", "Missing declaration.");
            return result.ToArray();
        }

        private static string Declaration(string ns, IEnumerable<string> names)
        {
            var writer = new CanonicalSignatureWriter(); writer.Token(ns ?? "");
            var values = names.ToArray(); writer.Token(values.Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
            foreach (var value in values) { ShadowHash.Require(!string.IsNullOrEmpty(value), "NativeLayoutResolutionDeclaration", "Missing name."); writer.Token(value); }
            return writer.ToString();
        }
        private static string DeclarationIdentity(string assembly, string declaration)
        { var writer = new CanonicalSignatureWriter(); writer.Token(assembly); writer.Token(declaration); return writer.ToString(); }
        private void CheckLive() { ShadowHash.Require(!disposed, "NativeLayoutResolutionDisposed", "Identity context has ended."); }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach (var image in images.Values) image.module.Dispose();
            images.Clear(); observations.Clear();
            if (runtimeFacade != null) runtimeFacade.module.Dispose();
            runtimeFacade = null; runtime = null;
        }
    }
}
