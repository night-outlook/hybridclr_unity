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
            bool rejectUnresolvedReferences = true)
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

            var dictionaryResolver = new DictionaryAssemblyResolver();
            var typeResolver = new Resolver(dictionaryResolver) { ProjectWinMDRefs = false };
            ModuleContext context = new ModuleContext(dictionaryResolver, typeResolver);

            var moduleByName = new Dictionary<string, ModuleDefMD>(StringComparer.OrdinalIgnoreCase);
            var modulePathByName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                // Load every supplied root into one explicit resolver cache. This makes resolution
                // deterministic while still allowing an assembly to be loaded only once.
                foreach (KeyValuePair<string, string> pair in pathByName.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                {
                    ModuleDefMD module = ModuleDefMD.Load(File.ReadAllBytes(pair.Value), context);
                    module.EnableTypeDefFindCache = true;
                    string actualName = AssemblyName(module, pair.Value);
                    ShadowHash.Require(string.Equals(actualName, pair.Key, StringComparison.OrdinalIgnoreCase),
                        "AssemblyNameMismatch", "File name and assembly identity differ: " + pair.Value + " declares " + actualName);
                    ShadowHash.Require(!moduleByName.ContainsKey(actualName), "DuplicateAssembly", "Duplicate assembly simple name '" + actualName + "': " + pair.Value);
                    moduleByName.Add(actualName, module);
                    modulePathByName.Add(actualName, pair.Value);
                    dictionaryResolver.Add(module);
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
                        if (capabilityMap.TryGetValue(name, out capability) && capability.classification == AssemblyClassification.BuildFiltered)
                            continue;
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
                        sha256 = ShadowHash.File(path),
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

                return new CompiledAssemblySet(descriptors, moduleByName, typeResolver, deferred);
            }
            catch
            {
                foreach (ModuleDefMD module in moduleByName.Values)
                    try { module.Dispose(); } catch (Exception) { }
                throw;
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

            public void Add(ModuleDef module)
            {
                ShadowHash.Require(module != null && module.Assembly != null, "NotAssembly", "Module has no assembly identity.");
                string name = AssemblyIdentityUtil.CanonicalName(module.Assembly.Name);
                ShadowHash.Require(!assemblies.ContainsKey(name), "DuplicateAssembly", name);
                assemblies.Add(name, module.Assembly);
            }

            public AssemblyDef Resolve(IAssembly assembly, ModuleDef sourceModule)
            {
                if (assembly == null)
                    return null;
                AssemblyDef result;
                assemblies.TryGetValue(AssemblyIdentityUtil.CanonicalName(assembly.Name), out result);
                return result;
            }
        }
    }
}
