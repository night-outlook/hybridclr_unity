using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dnlib.DotNet;

namespace HybridCLR.Editor.AssemblyShadow
{
    public static class DnlibAssemblyLoader
    {
        public static CompiledAssemblySet Load(string snapshotDirectory,
            IEnumerable<string> referenceDirectories,
            IEnumerable<AssemblyCapability> capabilities,
            bool rejectUnresolvedReferences = true,
            VerifiedTargetFrameworkReferences targetFrameworkReferences = null)
        {
            ShadowHash.Require(Directory.Exists(snapshotDirectory), "SnapshotMissing", "Snapshot directory does not exist: " + snapshotDirectory);
            var snapshotPaths = FindDlls(snapshotDirectory);
            ShadowHash.Require(snapshotPaths.Count > 0, "SnapshotEmpty", "Snapshot contains no DLLs: " + snapshotDirectory);

            var capabilityMap = new Dictionary<string, AssemblyCapability>(StringComparer.OrdinalIgnoreCase);
            foreach (AssemblyCapability capability in capabilities ?? Enumerable.Empty<AssemblyCapability>())
            {
                ShadowHash.Require(capability != null && !string.IsNullOrWhiteSpace(capability.name), "InvalidCapability", "Assembly capability requires a name.");
                string key = AssemblyIdentityUtil.CanonicalName(capability.name);
                ShadowHash.Require(!capabilityMap.ContainsKey(key), "DuplicateCapability", capability.name);
                capabilityMap.Add(key, capability);
            }

            var referencePaths = new List<string>();
            foreach (string directory in referenceDirectories ?? Enumerable.Empty<string>())
            {
                ShadowHash.Require(!string.IsNullOrWhiteSpace(directory), "ReferenceDirectoryMissing", "Reference directory is empty.");
                ShadowHash.Require(Directory.Exists(directory), "ReferenceDirectoryMissing", "Reference directory does not exist: " + directory);
                referencePaths.AddRange(FindDlls(directory));
            }

            var pathByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var snapshotNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in snapshotPaths)
            {
                string name = AssemblyNameFromPath(path);
                ShadowHash.Require(string.Equals(name, AssemblyIdentityUtil.CanonicalName(Path.GetFileNameWithoutExtension(path)), StringComparison.OrdinalIgnoreCase),
                    "AssemblyNameMismatch", "File name and assembly identity differ: " + path + " declares " + name);
                ShadowHash.Require(snapshotNames.Add(name), "DuplicateAssembly", "Duplicate snapshot assembly simple name '" + name + "': " + path);
                pathByName.Add(name, path);
            }

            var referenceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in referencePaths)
            {
                string name = AssemblyNameFromPath(path);
                ShadowHash.Require(string.Equals(name, AssemblyIdentityUtil.CanonicalName(Path.GetFileNameWithoutExtension(path)), StringComparison.OrdinalIgnoreCase),
                    "AssemblyNameMismatch", "File name and assembly identity differ: " + path + " declares " + name);
                ShadowHash.Require(referenceNames.Add(name), "DuplicateAssembly", "Duplicate reference assembly simple name '" + name + "': " + path);
                // Explicit policy: a snapshot copy wins over a reference-root copy. Duplicate
                // names within either category remain errors, so this cannot be ambiguous.
                if (!pathByName.ContainsKey(name))
                    pathByName.Add(name, path);
            }

            var dictionaryResolver = new DictionaryAssemblyResolver(targetFrameworkReferences);
            var typeResolver = new Resolver(dictionaryResolver) { ProjectWinMDRefs = false };
            ModuleContext context = new ModuleContext(dictionaryResolver, typeResolver);

            var sourceBindings = new List<CompiledAssemblySource>();
            var moduleByName = new Dictionary<string, ModuleDefMD>(StringComparer.OrdinalIgnoreCase);
            var modulePathByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                // Load every supplied root into one explicit resolver cache. This makes resolution
                // deterministic while still allowing an assembly to be loaded only once.
                foreach (KeyValuePair<string, string> pair in pathByName.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                {
                    byte[] bytes = File.ReadAllBytes(pair.Value);
                    ModuleDefMD module = ModuleDefMD.Load(bytes, context);
                    module.EnableTypeDefFindCache = true;
                    string actualName = AssemblyName(module, pair.Value);
                    ShadowHash.Require(string.Equals(actualName, pair.Key, StringComparison.OrdinalIgnoreCase),
                        "AssemblyNameMismatch", "File name and assembly identity differ: " + pair.Value + " declares " + actualName);
                    ShadowHash.Require(!moduleByName.ContainsKey(actualName), "DuplicateAssembly", "Duplicate assembly simple name '" + actualName + "': " + pair.Value);
                    moduleByName.Add(actualName, module);
                    modulePathByName.Add(actualName, pair.Value);
                    string inputHash = ShadowHash.Bytes(bytes);
                    dictionaryResolver.Add(module, !snapshotNames.Contains(actualName), inputHash);
                    sourceBindings.Add(new CompiledAssemblySource(actualName, Path.GetFullPath(pair.Value), inputHash, !snapshotNames.Contains(actualName)));
                }

                // Compiler reference facades can advertise platform-specific APIs which
                // the target profile does not supply. Only reference-root, metadata-only
                // forwarding assemblies qualify; a primary assembly never does.
                var facades = new HashSet<string>(moduleByName.Where(pair =>
                    !snapshotNames.Contains(pair.Key) && IsPureReferenceFacade(pair.Value))
                    .Select(pair => pair.Key), StringComparer.OrdinalIgnoreCase);
                var unresolved = new List<string>();
                var deferred = new List<string>();
                foreach (KeyValuePair<string, ModuleDefMD> pair in moduleByName.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                {
                    foreach (AssemblyRef reference in pair.Value.GetAssemblyRefs())
                    {
                        string referenceName = AssemblyIdentityUtil.CanonicalName(reference.Name);
                        ModuleDefMD supplied;
                        if (moduleByName.TryGetValue(referenceName, out supplied) && !dictionaryResolver.IsCompatible(reference, supplied.Assembly))
                            throw new ShadowBuildException("AssemblyIdentityMismatch", modulePathByName[pair.Key] + " -> requested " + reference.FullName +
                                "; supplied " + supplied.Assembly.FullName + " at " + modulePathByName[referenceName]);
                        AssemblyDef resolved = null;
                        try { resolved = dictionaryResolver.Resolve(reference, pair.Value); }
                        catch (Exception) { }
                        if (resolved == null || !moduleByName.ContainsKey(referenceName))
                        {
                            string referenceKey = AssemblyIdentityUtil.AssemblyReferenceKey(reference);
                            if (rejectUnresolvedReferences && facades.Contains(pair.Key) && IsForwardingOnlyReference(pair.Value, referenceName))
                                deferred.Add(pair.Key + " -> " + referenceKey);
                            else
                                unresolved.Add(modulePathByName[pair.Key] + " -> " + referenceKey);
                        }
                    }
                }
                if (rejectUnresolvedReferences && unresolved.Count > 0)
                    throw new ShadowBuildException("UnresolvedAssemblyReference", string.Join("; ", unresolved.OrderBy(x => x, StringComparer.Ordinal).ToArray()));

                // AssemblyRef presence alone cannot prove that a forwarded type exists.
                // Validate actual snapshot TypeRefs, including forwarding chains, with
                // the same closed resolver used by the returned set. No GAC/profile or
                // Editor-domain lookup is permitted to fill an absent target.
                if (rejectUnresolvedReferences)
                {
                    var unresolvedTypes = new List<string>();
                    foreach (string name in snapshotNames.OrderBy(n => n, StringComparer.Ordinal))
                    {
                        // Production callers derive this role from verified successful
                        // Player filter evidence. Archived removed DLLs remain hashed,
                        // but are not runtime consumers. NormalHotUpdate is deliberately
                        // not excluded: it still executes after dynamic loading.
                        AssemblyCapability capability;
                        if (capabilityMap.TryGetValue(name, out capability))
                        {
                            if (capability.classification == AssemblyClassification.BuildFiltered)
                                continue;
                            // Fixed precompiled Runtime plugins can contain optional
                            // target-profile APIs in code Unity later strips. Their
                            // AssemblyRefs and identities remain closed above, while
                            // source modules, candidates, Bootstrap and dynamically
                            // loaded NormalHotUpdate plugins retain full TypeRef checks.
                            if (capability.classification == AssemblyClassification.Runtime && capability.isPrecompiled &&
                                !capability.isShadowCapable && !capability.isBootstrap)
                                continue;
                        }
                        ModuleDefMD requester = moduleByName[name];
                        foreach (TypeRef reference in requester.GetTypeRefs())
                        {
                            string providerName = AssemblyIdentityUtil.CanonicalName(reference.DefinitionAssembly == null ? null : reference.DefinitionAssembly.Name.String);
                            ModuleDefMD provider;
                            if (!moduleByName.TryGetValue(providerName, out provider) ||
                                (!facades.Contains(providerName) && !provider.ExportedTypes.Any(t =>
                                    string.Equals(t.FullName, reference.FullName, StringComparison.Ordinal) && ForwardingTarget(t) != null)))
                                continue;
                            TypeDef resolved = null;
                            try { resolved = typeResolver.Resolve(reference, requester); }
                            catch (Exception) { }
                            if (resolved == null || !moduleByName.Values.Any(m => object.ReferenceEquals(m, resolved.Module)))
                                unresolvedTypes.Add(modulePathByName[name] + " -> TypeRef " + AssemblyIdentityUtil.TypeKey(reference));
                        }
                    }
                    if (unresolvedTypes.Count > 0)
                        throw new ShadowBuildException("UnresolvedForwardedType", string.Join("; ", unresolvedTypes.Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray()));
                }

                var descriptors = new Dictionary<string, AssemblyDescriptor>(StringComparer.OrdinalIgnoreCase);
                foreach (string path in snapshotPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                {
                    string key = AssemblyNameFromPath(path);
                    ModuleDefMD module = moduleByName[key];
                    AssemblyCapability capability;
                    capabilityMap.TryGetValue(key, out capability);
                    var report = AssemblySemanticHasher.Compute(module);
                    descriptors.Add(key, new AssemblyDescriptor
                    {
                        name = module.Assembly.Name.String,
                        mvid = module.Mvid == null ? string.Empty : module.Mvid.ToString(),
                        filePath = Path.GetFullPath(path),
                        sha256 = sourceBindings.Single(input => input.Name == key).Sha256,
                        semanticHash = report.semanticHash,
                        references = module.GetAssemblyRefs().Select(r => AssemblyIdentityUtil.CanonicalName(r.Name)).Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToArray(),
                        types = module.GetTypes().Select(t => new TypeDescriptor { typeKey = AssemblyIdentityUtil.TypeKey(t), name = t.Name.String, @namespace = t.Namespace ?? string.Empty, baseType = t.BaseType == null ? string.Empty : (t.BaseType.AssemblyQualifiedName ?? t.BaseType.FullName) }).OrderBy(t => t.typeKey, StringComparer.Ordinal).ToArray(),
                        isShadowCapable = capability != null && capability.isShadowCapable,
                        isBootstrap = capability != null && capability.isBootstrap,
                        isPrecompiled = capability != null && capability.isPrecompiled,
                        capabilityDeclared = capability != null && capability.capabilityDeclared,
                        classification = capability == null ? AssemblyClassification.Runtime : capability.classification
                    });
                }

                return new CompiledAssemblySet(descriptors, moduleByName, typeResolver, deferred, sourceBindings, targetFrameworkReferences);
            }
            catch
            {
                foreach (ModuleDefMD module in moduleByName.Values)
                    try { module.Dispose(); } catch (Exception) { }
                throw;
            }
        }

        /// <summary>
        /// Independent replay of the SAME captured input list and resolver policy.
        /// All images are registered before semantic decoding. This deliberately
        /// does not reopen one reference in an ambient/empty resolution context.
        /// It reads no directories, GAC, or unlisted fallback assemblies.
        /// </summary>
        internal static VerificationDomain LoadVerificationDomain(IReadOnlyList<CompiledAssemblySource> sources,
            VerifiedTargetFrameworkReferences frameworkReferences)
        {
            ShadowHash.Require(sources != null && sources.Count > 0, "EligibilityInputChanged", "Missing loaded input bindings.");
            var assemblies = new DictionaryAssemblyResolver(frameworkReferences);
            var resolver = new Resolver(assemblies) { ProjectWinMDRefs = false };
            var context = new ModuleContext(assemblies, resolver);
            var result = new VerificationDomain();
            try
            {
                foreach (var source in sources)
                {
                    byte[] bytes = File.ReadAllBytes(source.Path);
                    ShadowHash.Require(ShadowHash.Bytes(bytes) == source.Sha256,
                        "EligibilityInputChanged", "Replay input bytes changed: " + source.Name);
                    var module = ModuleDefMD.Load(bytes, context);
                    try
                    {
                        module.EnableTypeDefFindCache = true;
                        ShadowHash.Require(AssemblyName(module, source.Path) == source.Name && !result.Modules.ContainsKey(source.Name),
                            "EligibilityInputChanged", "Replay input identity/membership changed: " + source.Name);
                        assemblies.Add(module, source.ReferenceOnly, source.Sha256);
                        result.Modules.Add(source.Name, module);
                    }
                    catch { module.Dispose(); throw; }
                }
                return result;
            }
            catch { result.Dispose(); throw; }
        }

        internal sealed class VerificationDomain : IDisposable
        {
            internal readonly Dictionary<string, ModuleDefMD> Modules = new Dictionary<string, ModuleDefMD>(StringComparer.OrdinalIgnoreCase);
            public void Dispose()
            {
                foreach (var module in Modules.Values) module.Dispose();
                Modules.Clear();
            }
        }

        private static bool IsPureReferenceFacade(ModuleDefMD module)
        {
            return module.Assembly.CustomAttributes.Any(a => a.TypeFullName == "System.Runtime.CompilerServices.ReferenceAssemblyAttribute") &&
                module.GetTypes().All(t => t.IsGlobalModuleType && !t.HasMethods && !t.HasFields && !t.HasProperties && !t.HasEvents) &&
                module.Resources.Count == 0 && module.Metadata.TablesStream.FileTable.Rows == 0 &&
                module.ExportedTypes.Count > 0 && module.ExportedTypes.All(t => ForwardingTarget(t) != null);
        }

        private static AssemblyRef ForwardingTarget(ExportedType type)
        {
            var visited = new HashSet<ExportedType>();
            while (type != null && visited.Add(type))
            {
                var target = type.Implementation as AssemblyRef;
                if (target != null)
                    return type.IsForwarder ? target : null;
                type = type.Implementation as ExportedType;
            }
            return null;
        }

        private static bool IsForwardingOnlyReference(ModuleDefMD module, string referenceName)
        {
            // In a pure facade an AssemblyRef may still be an attribute/signature
            // dependency via a TypeRef. Such references remain mandatory.
            return module.ExportedTypes.Any(t =>
                AssemblyIdentityUtil.CanonicalName(ForwardingTarget(t).Name) == referenceName) &&
                !module.GetTypeRefs().Any(t => t.DefinitionAssembly != null &&
                    AssemblyIdentityUtil.CanonicalName(t.DefinitionAssembly.Name) == referenceName);
        }

        private static List<string> FindDlls(string directory)
        {
            return Directory.GetFiles(directory, "*.dll", SearchOption.AllDirectories)
                .Where(p => !p.EndsWith(".dll.bytes", StringComparison.OrdinalIgnoreCase))
                .Select(Path.GetFullPath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static string AssemblyNameFromPath(string path)
        {
            ModuleDefMD module = null;
            try
            {
                module = ModuleDefMD.Load(File.ReadAllBytes(path));
                return AssemblyName(module, path);
            }
            finally
            {
                if (module != null)
                    try { module.Dispose(); } catch (Exception) { }
            }
        }

        private static string AssemblyName(ModuleDefMD module, string path)
        {
            ShadowHash.Require(module.Assembly != null, "NotAssembly", "DLL has no assembly identity: " + path);
            return AssemblyIdentityUtil.CanonicalName(module.Assembly.Name);
        }

        private sealed class DictionaryAssemblyResolver : IAssemblyResolver
        {
            private readonly Dictionary<string, AssemblyDef> assemblies = new Dictionary<string, AssemblyDef>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<AssemblyDef, string> referenceHashes = new Dictionary<AssemblyDef, string>();
            private readonly VerifiedTargetFrameworkReferences frameworkReferences;
            private static readonly AssemblyNameComparer ExceptVersion = new AssemblyNameComparer(
                AssemblyNameComparerFlags.Name | AssemblyNameComparerFlags.PublicKeyToken | AssemblyNameComparerFlags.Culture | AssemblyNameComparerFlags.ContentType);

            public DictionaryAssemblyResolver(VerifiedTargetFrameworkReferences frameworkReferences)
            {
                this.frameworkReferences = frameworkReferences;
            }

            public void Add(ModuleDef module, bool referenceOnly, string sha256)
            {
                ShadowHash.Require(module != null && module.Assembly != null, "NotAssembly", "Module has no assembly identity.");
                string name = AssemblyIdentityUtil.CanonicalName(module.Assembly.Name);
                ShadowHash.Require(!assemblies.ContainsKey(name), "DuplicateAssembly", name);
                assemblies.Add(name, module.Assembly);
                if (referenceOnly) referenceHashes.Add(module.Assembly, sha256);
            }

            public bool IsCompatible(IAssembly requested, AssemblyDef supplied)
            {
                if (requested == null || supplied == null) return false;
                if (AssemblyNameComparer.CompareAll.Equals(requested, supplied)) return true;
                string hash;
                return frameworkReferences != null && ExceptVersion.Equals(requested, supplied) &&
                    referenceHashes.TryGetValue(supplied, out hash) && frameworkReferences.Contains(supplied, hash);
            }

            public AssemblyDef Resolve(IAssembly assembly, ModuleDef sourceModule)
            {
                if (assembly == null)
                    return null;
                AssemblyDef result;
                assemblies.TryGetValue(AssemblyIdentityUtil.CanonicalName(assembly.Name), out result);
                return IsCompatible(assembly, result) ? result : null;
            }
        }
    }
}
