using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;

namespace HybridCLR.Editor.AssemblyShadow
{
    /// <summary>Typed boundary for resource inspection; also permits deterministic index regression tests.</summary>
    public interface IResourceAssetReader
    {
        string[] GetDependencies(string path);
        ResourceAssetReferences Read(string path);
    }

    public sealed class ResourceAssetReferences
    {
        public string guid;
        public ResourceTypeIdentity[] scriptTypes = new ResourceTypeIdentity[0];
        public ResourceTypeIdentity[] managedReferenceTypes = new ResourceTypeIdentity[0];
        public string[] unknowns = new string[0];
    }

    public sealed class ResourceTypeIdentity
    {
        public string assembly;
        public string @namespace;
        public string type;
    }

    /// <summary>Reads Unity 2022 text serialization, not arbitrary type-name substrings.</summary>
    public static class UnitySerializedReferenceParser
    {
        private static readonly Regex Documents = new Regex(@"(?m)^--- !u!(?<class>[0-9]+) &[^\r\n]*");
        private static readonly Regex ScriptLine = new Regex(@"^  m_Script:\s*\{(?<value>[^}\r\n]*)\}\s*$");
        private static readonly Regex Guid = new Regex(@"\bguid:\s*(?<guid>[0-9a-fA-F]{32})\b");
        private static readonly Regex FileId = new Regex(@"\bfileID:\s*(?<id>-?[0-9]+)\b");
        private static readonly Regex ManagedLine = new Regex(@"^      type:\s*\{class:\s*(?<class>[^,}]*),\s*ns:\s*(?<ns>[^,}]*),\s*asm:\s*(?<asm>[^,}]*)\}\s*$");

        public static ResourceAssetReferences Parse(string yaml, Func<string, long, ResourceTypeIdentity> resolveScript)
        {
            if (resolveScript == null) throw new ArgumentNullException("resolveScript");
            var scripts = new List<ResourceTypeIdentity>();
            var managed = new List<ResourceTypeIdentity>();
            var unknown = new List<string>();
            if (string.IsNullOrEmpty(yaml) || !yaml.StartsWith("%YAML", StringComparison.Ordinal))
                return new ResourceAssetReferences { unknowns = new[] { "not Unity serialized YAML" } };
            var documents = Documents.Matches(yaml);
            if (documents.Count == 0) unknown.Add("missing Unity serialized document headers");
            for (int i = 0; i < documents.Count; ++i)
            {
                var header = documents[i];
                if (header.Groups["class"].Value != "114") continue;
                int end = i + 1 == documents.Count ? yaml.Length : documents[i + 1].Index;
                string body = yaml.Substring(header.Index + header.Length, end - header.Index - header.Length);
                bool references = false, scriptSeen = false;
                foreach (var rawLine in body.Split('\n'))
                {
                    string line = rawLine.TrimEnd('\r');
                    if (line.StartsWith("  m_Script:", StringComparison.Ordinal))
                    {
                        if (scriptSeen) unknown.Add("ambiguous duplicate m_Script identity");
                        scriptSeen = true;
                        var match = ScriptLine.Match(line);
                        var guid = Guid.Match(match.Groups["value"].Value);
                        var id = FileId.Match(match.Groups["value"].Value);
                        long localId;
                        if (!match.Success || !guid.Success || !id.Success || !long.TryParse(id.Groups["id"].Value, out localId))
                        { unknown.Add("missing or malformed m_Script GUID/fileID"); continue; }
                        var identity = resolveScript(guid.Groups["guid"].Value, localId);
                        if (identity == null) unknown.Add("unresolved m_Script: " + guid.Groups["guid"].Value + ":" + localId);
                        else scripts.Add(identity);
                    }
                    if (line == "  references:") { references = true; continue; }
                    if (line.Length > 2 && line.StartsWith("  ", StringComparison.Ordinal) && line[2] != ' ') references = false;
                    if (!references) continue;
                    if (line.StartsWith("    version:", StringComparison.Ordinal) && line.Trim() != "version: 2") unknown.Add("unsupported managed reference serialization version");
                    if (!line.StartsWith("      type:", StringComparison.Ordinal)) continue;
                    var type = ManagedLine.Match(line);
                    if (!type.Success) { unknown.Add("unsupported managed reference type encoding"); continue; }
                    string name = Unquote(type.Groups["class"].Value);
                    if (name == string.Empty || name == "null") continue;
                    string assembly = Unquote(type.Groups["asm"].Value);
                    if (assembly.Length == 0) unknown.Add("managed reference assembly is missing: " + name);
                    else managed.Add(new ResourceTypeIdentity { assembly = assembly, @namespace = Unquote(type.Groups["ns"].Value), type = name });
                }
                if (!scriptSeen && !header.Value.EndsWith(" stripped", StringComparison.Ordinal)) unknown.Add("MonoBehaviour document has no m_Script");
            }
            return new ResourceAssetReferences { scriptTypes = scripts.ToArray(), managedReferenceTypes = managed.ToArray(), unknowns = unknown.ToArray() };
        }
        private static string Unquote(string text) { return text.Trim().Trim('\'', '"'); }
    }

    public static class AssetScriptReferenceIndexer
    {
        public static ResourceScriptIndex Build(AssetBundleBuild[] builds, ResourceAbiDescriptor descriptor,
            ShadowDependencyConfiguration dependencies = null)
        {
            return Build(builds, descriptor, dependencies, new UnityResourceAssetReader());
        }

        public static ResourceScriptIndex Build(AssetBundleBuild[] builds, ResourceAbiDescriptor descriptor,
            ShadowDependencyConfiguration dependencies, IResourceAssetReader reader)
        {
            if (descriptor == null) throw new ArgumentNullException("descriptor");
            if (reader == null) throw new ArgumentNullException("reader");
            var unknowns = new HashSet<string>(StringComparer.Ordinal);
            if (descriptor.schemaVersion != ResourceAbiHasher.SchemaVersion) unknowns.Add("unsupported resource ABI schema");
            var byType = new Dictionary<string, ResourceAbiTypeDescriptor>(StringComparer.Ordinal);
            foreach (var type in descriptor.types ?? new ResourceAbiTypeDescriptor[0])
            {
                if (type == null || string.IsNullOrEmpty(type.typeKey)) { unknowns.Add("invalid ABI type identity"); continue; }
                if (byType.ContainsKey(type.typeKey)) unknowns.Add("duplicate ABI type identity: " + type.typeKey);
                else byType.Add(type.typeKey, type);
            }
            var targetAssemblies = new HashSet<string>(byType.Values.Select(t => AssemblyIdentityUtil.CanonicalName(t.assembly)), StringComparer.Ordinal);
            var names = byType.Values.GroupBy(t => Identity(t.assembly, t.@namespace, t.type), StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Select(t => t.typeKey).ToArray(), StringComparer.Ordinal);
            var aggregate = new Dictionary<string, MutableEntry>(StringComparer.Ordinal);
            var bundles = new HashSet<string>(StringComparer.Ordinal);
            foreach (var build in builds ?? new AssetBundleBuild[0])
            {
                string bundle = build.assetBundleName ?? string.Empty;
                if (!string.IsNullOrEmpty(build.assetBundleVariant)) bundle += "." + build.assetBundleVariant;
                if (string.IsNullOrEmpty(bundle)) { unknowns.Add("unnamed bundle build"); continue; }
                bundles.Add(bundle);
                if (build.assetNames == null || build.assetNames.Length == 0) unknowns.Add(bundle + ":empty resource build map");
                foreach (var assetPath in build.assetNames ?? new string[0])
                {
                    if (string.IsNullOrEmpty(assetPath)) { unknowns.Add(bundle + ":empty asset path"); continue; }
                    string[] paths;
                    try { paths = reader.GetDependencies(assetPath); }
                    catch (Exception ex) { unknowns.Add(bundle + ":" + assetPath + ":dependency inspection failed: " + ex.Message); paths = new string[0]; }
                    if (paths == null) { unknowns.Add(bundle + ":" + assetPath + ":missing dependency result"); paths = new string[0]; }
                    foreach (var path in paths.Concat(new[] { assetPath }).Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal))
                    {
                        if (IsCode(path)) continue; // Script GUIDs identify types, never serialized resource assets.
                        ResourceAssetReferences asset;
                        try { asset = reader.Read(path); }
                        catch (Exception ex) { unknowns.Add(bundle + ":" + path + ":resource inspection failed: " + ex.Message); continue; }
                        if (asset == null || string.IsNullOrEmpty(asset.guid)) { unknowns.Add(bundle + ":" + path + ":missing resource GUID"); continue; }
                        foreach (var reason in asset.unknowns ?? new string[0]) unknowns.Add(bundle + ":" + path + ":" + reason);
                        foreach (var identity in (asset.scriptTypes ?? new ResourceTypeIdentity[0]).Concat(asset.managedReferenceTypes ?? new ResourceTypeIdentity[0]))
                        {
                            if (identity == null) { unknowns.Add(bundle + ":" + path + ":missing serialized type identity"); continue; }
                            string key = Identity(identity.assembly, identity.@namespace, identity.type);
                            string[] matches;
                            if (!names.TryGetValue(key, out matches) || matches.Length != 1)
                            {
                                bool managed = (asset.managedReferenceTypes ?? new ResourceTypeIdentity[0]).Contains(identity);
                                if (managed || targetAssemblies.Contains(AssemblyIdentityUtil.CanonicalName(identity.assembly))) unknowns.Add(bundle + ":" + path + ":unresolved or ambiguous serialized type: " + key);
                                continue;
                            }
                            var pending = new Queue<string>(matches);
                            var seen = new HashSet<string>(StringComparer.Ordinal);
                            while (pending.Count != 0)
                            {
                                string typeKey = pending.Dequeue();
                                if (!seen.Add(typeKey)) continue;
                                ResourceAbiTypeDescriptor type;
                                if (!byType.TryGetValue(typeKey, out type)) { unknowns.Add(bundle + ":" + path + ":missing ABI dependency: " + typeKey); continue; }
                                if (type.hasUnknown) unknowns.Add(bundle + ":" + path + ":unproven ABI: " + typeKey);
                                Add(aggregate, typeKey, path, asset.guid, bundle);
                                foreach (var child in (type.referencedTypeKeys ?? new string[0]).Concat(type.serializeReferenceCandidates ?? new string[0])) pending.Enqueue(child);
                            }
                        }
                    }
                }
            }
            foreach (var declaration in (dependencies == null ? new DeclaredResourceDependency[0] : dependencies.resourceDependencies) ?? new DeclaredResourceDependency[0])
            {
                if (declaration == null || string.IsNullOrEmpty(declaration.bundle) || string.IsNullOrEmpty(declaration.assembly))
                { unknowns.Add("incomplete declared resource dependency"); continue; }
                bundles.Add(declaration.bundle);
                var types = byType.Values.Where(t => AssemblyIdentityUtil.CanonicalName(t.assembly) == AssemblyIdentityUtil.CanonicalName(declaration.assembly)).ToArray();
                if (types.Length == 0) unknowns.Add("declared resource assembly has no ABI: " + declaration.assembly);
                foreach (var type in types) Add(aggregate, type.typeKey, null, null, declaration.bundle);
            }
            return new ResourceScriptIndex {
                entries = aggregate.Values.Select(e => new ResourceScriptIndexEntry { typeKey = e.typeKey,
                    assetPaths = e.paths.OrderBy(v => v, StringComparer.Ordinal).ToArray(), assetGuids = e.guids.OrderBy(v => v, StringComparer.Ordinal).ToArray(),
                    bundleNames = e.bundles.OrderBy(v => v, StringComparer.Ordinal).ToArray() }).OrderBy(e => e.typeKey, StringComparer.Ordinal).ToArray(),
                bundles = bundles.OrderBy(v => v, StringComparer.Ordinal).ToArray(), hasUnknown = unknowns.Count != 0,
                unknowns = unknowns.OrderBy(v => v, StringComparer.Ordinal).ToArray(),
            };
        }

        private static string Identity(string assembly, string ns, string type)
        { return AssemblyIdentityUtil.CanonicalName(assembly) + ":" + (ns ?? string.Empty) + ":" + (type ?? string.Empty).Replace('+', '/'); }
        private static bool IsCode(string path)
        { string extension = Path.GetExtension(path ?? string.Empty); return new[] { ".cs", ".dll", ".asmdef", ".asmref", ".meta" }.Contains(extension, StringComparer.OrdinalIgnoreCase); }
        private static void Add(IDictionary<string, MutableEntry> aggregate, string key, string path, string guid, string bundle)
        {
            MutableEntry entry;
            if (!aggregate.TryGetValue(key, out entry)) aggregate.Add(key, entry = new MutableEntry { typeKey = key });
            if (!string.IsNullOrEmpty(path)) entry.paths.Add(path);
            if (!string.IsNullOrEmpty(guid)) entry.guids.Add(guid);
            entry.bundles.Add(bundle);
        }
        private sealed class MutableEntry
        {
            public string typeKey;
            public readonly HashSet<string> paths = new HashSet<string>(StringComparer.Ordinal);
            public readonly HashSet<string> guids = new HashSet<string>(StringComparer.Ordinal);
            public readonly HashSet<string> bundles = new HashSet<string>(StringComparer.Ordinal);
        }

        private sealed class UnityResourceAssetReader : IResourceAssetReader
        {
            public string[] GetDependencies(string path) { return AssetDatabase.GetDependencies(path, true); }
            public ResourceAssetReferences Read(string path)
            {
                if (string.IsNullOrEmpty(path)) throw new IOException("resource path is missing");
                var result = new ResourceAssetReferences { guid = AssetDatabase.AssetPathToGUID(path) };
                var scripts = new List<ResourceTypeIdentity>();
                var managed = new List<ResourceTypeIdentity>();
                var unknown = new List<string>();
                // Only Unity's serialized YAML is parsed; ordinary text/source files never imply type usage.
                string text = null;
                if (File.Exists(path) && AssetDatabase.GetMainAssetTypeAtPath(path) != typeof(UnityEngine.TextAsset)) using (var input = new StreamReader(path))
                {
                    var prefix = new char[5];
                    int count = input.Read(prefix, 0, prefix.Length);
                    if (count == prefix.Length && new string(prefix) == "%YAML") text = "%YAML" + input.ReadToEnd();
                }
                if (text != null)
                {
                    var parsed = UnitySerializedReferenceParser.Parse(text, ResolveScript);
                    scripts.AddRange(parsed.scriptTypes); managed.AddRange(parsed.managedReferenceTypes); unknown.AddRange(parsed.unknowns);
                }
                else
                {
                    // Binary scene inspection would require opening a scene, which is unsafe here.
                    if (path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) unknown.Add("binary scene requires serialized YAML for safe inspection");
                    else
                    {
                        var assets = AssetDatabase.LoadAllAssetsAtPath(path);
                        if (assets == null || assets.Length == 0) unknown.Add("resource cannot be loaded for serialized inspection");
                        if (string.IsNullOrEmpty(result.guid))
                        {
                            // Built-in imported resources may have no backing file; their actual
                            // object identity is still queryable. Do not special-case path strings.
                            var guids = new HashSet<string>(StringComparer.Ordinal);
                            foreach (var asset in assets ?? new UnityEngine.Object[0])
                            {
                                string objectGuid; long localId;
                                if (asset != null && AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset, out objectGuid, out localId) && !string.IsNullOrEmpty(objectGuid)) guids.Add(objectGuid);
                            }
                            if (guids.Count == 1) result.guid = guids.Single();
                            else unknown.Add("missing or ambiguous imported resource GUID");
                        }
                        var objects = new List<UnityEngine.Object>(assets ?? new UnityEngine.Object[0]);
                        foreach (var gameObject in (assets ?? new UnityEngine.Object[0]).OfType<UnityEngine.GameObject>())
                            objects.AddRange(gameObject.GetComponentsInChildren<UnityEngine.Component>(true));
                        foreach (var asset in objects.Distinct())
                        {
                            if (asset == null) { unknown.Add("unresolved serialized object"); continue; }
                            if (!(asset is UnityEngine.MonoBehaviour) && !(asset is UnityEngine.ScriptableObject)) continue;
                            var serialized = new SerializedObject(asset);
                            var property = serialized.GetIterator();
                            while (property.Next(true))
                            {
                                if (property.propertyType == SerializedPropertyType.ObjectReference && property.name == "m_Script")
                                {
                                    var script = property.objectReferenceValue as MonoScript;
                                    var type = script == null ? null : script.GetClass();
                                    if (type == null) unknown.Add("unresolved binary m_Script"); else scripts.Add(FromType(type));
                                }
                                if (property.propertyType == SerializedPropertyType.ManagedReference && !string.IsNullOrEmpty(property.managedReferenceFullTypename))
                                {
                                    string value = property.managedReferenceFullTypename;
                                    int space = value.IndexOf(' ');
                                    if (space < 1) { unknown.Add("invalid managed reference typename: " + value); continue; }
                                    string fullName = value.Substring(space + 1);
                                    int dot = fullName.LastIndexOf('.');
                                    managed.Add(new ResourceTypeIdentity { assembly = value.Substring(0, space), @namespace = dot < 0 ? string.Empty : fullName.Substring(0, dot), type = dot < 0 ? fullName : fullName.Substring(dot + 1) });
                                }
                            }
                            serialized.Dispose();
                        }
                    }
                }
                result.scriptTypes = scripts.ToArray(); result.managedReferenceTypes = managed.ToArray(); result.unknowns = unknown.ToArray();
                return result;
            }
            private static ResourceTypeIdentity ResolveScript(string guid, long fileId)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(path)) return null;
                // DLLs contain multiple MonoScripts under one GUID: local fileID is mandatory.
                var matches = AssetDatabase.LoadAllAssetsAtPath(path).OfType<MonoScript>().Where(s => {
                    string scriptGuid; long scriptId;
                    return AssetDatabase.TryGetGUIDAndLocalFileIdentifier(s, out scriptGuid, out scriptId) && scriptId == fileId &&
                        string.Equals(scriptGuid, guid, StringComparison.OrdinalIgnoreCase);
                }).ToArray();
                var type = matches.Length == 1 ? matches[0].GetClass() : null;
                return type == null ? null : FromType(type);
            }
            private static ResourceTypeIdentity FromType(Type type)
            {
                return new ResourceTypeIdentity { assembly = type.Assembly.GetName().Name, @namespace = type.Namespace ?? string.Empty,
                    type = (string.IsNullOrEmpty(type.Namespace) ? type.FullName : type.FullName.Substring(type.Namespace.Length + 1)).Replace('+', '/') };
            }
        }
    }
}
