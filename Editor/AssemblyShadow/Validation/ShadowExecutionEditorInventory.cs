using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace HybridCLR.Editor.AssemblyShadow
{
    // Returned observations are immutable. They do not authorize execution or
    // replace the byte-bound compiled-module policy checks.
    public sealed class ShadowExecutionStartupScript
    {
        public string RootAssetPath { get; private set; }
        public string AssetPath { get; private set; }
        public string ScriptPath { get; private set; }
        public string AssemblyIdentity { get; private set; }
        public string TypeName { get; private set; }
        public string Phase { get; private set; }
        public int ExecutionOrder { get; private set; }
        public bool IsCandidate { get; private set; }
        public bool IsBootstrapRunner { get; private set; }
        public bool IsCandidateDependent { get; private set; }
        public bool DependencyProved { get; private set; }
        private readonly string[] callbacks;
        private readonly string[] dependencyEvidence;
        public string[] Callbacks { get { return (string[])callbacks.Clone(); } }
        public string[] DependencyEvidence { get { return (string[])dependencyEvidence.Clone(); } }
        internal ShadowExecutionStartupScript(string root, string asset, string script, Type type, string phase, int order, bool candidate, bool bootstrap, string[] callbacks, bool dependent, bool proved, string[] evidence)
        { RootAssetPath = root; AssetPath = asset; ScriptPath = script; AssemblyIdentity = type.Assembly.FullName; TypeName = type.FullName; Phase = phase; ExecutionOrder = order; IsCandidate = candidate; IsBootstrapRunner = bootstrap; this.callbacks = (string[])callbacks.Clone(); IsCandidateDependent = dependent; DependencyProved = proved; dependencyEvidence = (string[])evidence.Clone(); }
    }

    public sealed class ShadowExecutionPreloadedAsset
    {
        public string AssetPath { get; private set; }
        public string TypeName { get; private set; }
        public string AssemblyIdentity { get; private set; }
        public bool IsCandidate { get; private set; }
        internal ShadowExecutionPreloadedAsset(string path, Type type, bool candidate)
        { AssetPath = path; TypeName = type.FullName; AssemblyIdentity = type.Assembly.FullName; IsCandidate = candidate; }
    }

    public sealed class ShadowExecutionEditorInventory
    {
        public string StartupScenePath { get; private set; }
        public string BootstrapScriptPath { get; private set; }
        public string BootstrapAssemblyIdentity { get; private set; }
        public string BootstrapTypeName { get; private set; }
        public int BootstrapExecutionOrder { get; private set; }
        private readonly ShadowExecutionStartupScript[] scripts;
        private readonly ShadowExecutionPreloadedAsset[] preloaded;
        private readonly string[] diagnostics;
        public ShadowExecutionStartupScript[] Scripts { get { return (ShadowExecutionStartupScript[])scripts.Clone(); } }
        public ShadowExecutionPreloadedAsset[] PreloadedAssets { get { return (ShadowExecutionPreloadedAsset[])preloaded.Clone(); } }
        public string[] Diagnostics { get { return (string[])diagnostics.Clone(); } }
        public bool IsValid { get { return diagnostics.Length == 0; } }
        internal readonly ShadowPolicyValidationResult Validation;
        internal ShadowExecutionEditorInventory(string scene, string script, Type runner, int order, IEnumerable<ShadowExecutionStartupScript> scripts, IEnumerable<ShadowExecutionPreloadedAsset> assets, ShadowPolicyValidationResult validation)
        {
            StartupScenePath = scene; BootstrapScriptPath = script; BootstrapAssemblyIdentity = runner == null ? "" : runner.Assembly.FullName;
            BootstrapTypeName = runner == null ? "" : runner.FullName; BootstrapExecutionOrder = order;
            this.scripts = scripts.ToArray(); preloaded = assets.ToArray(); diagnostics = validation.Errors.ToArray(); Validation = validation;
        }
    }

    public static partial class ShadowExecutionPolicy
    {
        public static ShadowPolicyValidationResult ValidateCurrentEditor(ShadowPolicyConfiguration policy, Type bootstrapRunner)
        { return CaptureCurrentEditor(policy, bootstrapRunner).Validation; }
        public static ShadowPolicyValidationResult ValidateCurrentEditor(ShadowPolicyConfiguration policy, Type bootstrapRunner, CompiledAssemblySet compiled)
        { return CaptureCurrentEditor(policy, bootstrapRunner, compiled).Validation; }

        /// <summary>
        /// Reads the actual imported startup scene, preloaded assets, MonoScript
        /// identities and MonoImporter orders. Does not open scenes or invoke
        /// business callbacks. M06 requires a single direct active root runner
        /// with Awake; prefab-instantiated or disabled runners are not assumed safe.
        /// </summary>
        public static ShadowExecutionEditorInventory CaptureCurrentEditor(ShadowPolicyConfiguration policy, Type bootstrapRunner)
        { return CaptureCurrentEditor(policy, bootstrapRunner, null); }

        public static ShadowExecutionEditorInventory CaptureCurrentEditor(ShadowPolicyConfiguration policy, Type bootstrapRunner, CompiledAssemblySet compiled)
        {
            var result = new ShadowPolicyValidationResult(); var scripts = new List<ShadowExecutionStartupScript>(); var preloaded = new List<ShadowExecutionPreloadedAsset>();
            string scenePath = "", runnerPath = ""; int runnerOrder = 0;
            var capabilities = (policy == null ? null : policy.assemblies) ?? new AssemblyCapability[0];
            var candidates = new HashSet<string>(capabilities.Where(item => item != null && item.isShadowCapable).Select(item => AssemblyNamePolicy.Canonical(item.name)), StringComparer.OrdinalIgnoreCase);
            var dependencies = new Dictionary<string, StartupDependency>(StringComparer.Ordinal);
            try
            {
                if (bootstrapRunner == null || !typeof(MonoBehaviour).IsAssignableFrom(bootstrapRunner) || !capabilities.Any(item => item != null && item.isBootstrap &&
                    !item.isShadowCapable && item.classification == AssemblyClassification.Runtime && string.Equals(AssemblyNamePolicy.Canonical(item.name), bootstrapRunner.Assembly.GetName().Name, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("Actual runner must be a MonoBehaviour in the configured fixed Runtime Bootstrap.");
                if (!StartupCallbacks(bootstrapRunner).Contains("Awake")) throw new InvalidOperationException("Bootstrap must gate activation in Awake, not a later lifecycle phase.");
                var runnerScripts = MonoImporter.GetAllRuntimeMonoScripts().Where(script => script != null && script.GetClass() == bootstrapRunner).ToArray();
                if (runnerScripts.Length != 1) throw new InvalidOperationException("Bootstrap runner must resolve to one actual imported runtime MonoScript.");
                MonoScript runner = runnerScripts[0]; runnerPath = AssetDatabase.GetAssetPath(runner); runnerOrder = MonoImporter.GetExecutionOrder(runner);
                var scenes = (EditorBuildSettings.scenes ?? new EditorBuildSettingsScene[0]).Where(scene => scene != null && scene.enabled).ToArray();
                if (scenes.Length != 1) throw new InvalidOperationException("M06 requires one enabled Bootstrap scene.");
                scenePath = scenes[0].path;
                string project = Directory.GetParent(Application.dataPath).FullName;
                string physicalScene = ShadowAssemblyPolicyValidator.ResolveAssetPath(project, scenePath);
                string sceneText = File.ReadAllText(physicalScene);
                ValidateActiveRootRunner(sceneText, runner, result);
                var roots = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>(scenePath, "StartupScene") };
                foreach (UnityEngine.Object asset in PlayerSettings.GetPreloadedAssets() ?? new UnityEngine.Object[0])
                {
                    if (asset == null) { result.Error("PreloadedAssetUnresolved", "PlayerSettings contains a missing preloaded asset."); continue; }
                    string path = AssetDatabase.GetAssetPath(asset); Type type = asset.GetType(); bool candidate = candidates.Contains(type.Assembly.GetName().Name);
                    preloaded.Add(new ShadowExecutionPreloadedAsset(path, type, candidate));
                    if (string.IsNullOrEmpty(path)) { result.Error("PreloadedAssetUnresolved", "Preloaded object has no imported asset path: " + type.FullName); continue; }
                    if (candidate) result.Error("PreloadedCandidateAsset", path + " -> " + type.Assembly.FullName + "::" + type.FullName);
                    roots.Add(new KeyValuePair<string, string>(path, "PreloadedAsset"));
                }
                var visited = new HashSet<string>(StringComparer.Ordinal);
                foreach (var root in roots)
                foreach (string path in new[] { root.Key }.Concat(AssetDatabase.GetDependencies(root.Key, true) ?? new string[0]).Distinct(StringComparer.Ordinal))
                {
                    if (!visited.Add(root.Key + "\n" + path)) continue;
                    if (!new[] { ".unity", ".prefab", ".asset" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) continue;
                    string physical = ShadowAssemblyPolicyValidator.ResolveAssetPath(project, path);
                    if (!File.Exists(physical)) { result.Error("StartupAssetMissing", path); continue; }
                    var parsed = UnitySerializedReferenceParser.Parse(File.ReadAllText(physical), (guid, localId) =>
                    {
                        string scriptPath = AssetDatabase.GUIDToAssetPath(guid);
                        var matches = AssetDatabase.LoadAllAssetsAtPath(scriptPath).OfType<MonoScript>().Where(script =>
                        { string actualGuid; long actualId; return AssetDatabase.TryGetGUIDAndLocalFileIdentifier(script, out actualGuid, out actualId) && actualId == localId && string.Equals(actualGuid, guid, StringComparison.OrdinalIgnoreCase); }).ToArray();
                        Type type = matches.Length == 1 ? matches[0].GetClass() : null;
                        if (type == null) return null;
                        string[] callbacks = StartupCallbacks(type); bool candidate = candidates.Contains(type.Assembly.GetName().Name), bootstrap = type == bootstrapRunner;
                        StartupDependency dependency;
                        if (!dependencies.TryGetValue(type.Assembly.FullName, out dependency))
                        { dependency = ReadStartupDependency(type.Assembly, candidates, policy, compiled); dependencies.Add(type.Assembly.FullName, dependency); }
                        int order = MonoImporter.GetExecutionOrder(matches[0]);
                        scripts.Add(new ShadowExecutionStartupScript(root.Key, path, scriptPath, type, root.Value, order, candidate, bootstrap, callbacks, dependency.Dependent, dependency.Proved, dependency.Evidence.ToArray()));
                        if (candidate) result.Error(root.Value == "PreloadedAsset" ? "PreloadedCandidateAsset" : "StartupCandidateAsset", path + " -> " + type.Assembly.FullName + "::" + type.FullName);
                        if (!bootstrap && callbacks.Length != 0 && (root.Value == "PreloadedAsset" || typeof(ScriptableObject).IsAssignableFrom(type) || order <= runnerOrder))
                        {
                            if (dependency.Dependent) result.Error("ScriptExecutionBeforeBootstrap", path + " -> " + type.FullName + " [" + string.Join(",", callbacks) + "] candidate-dependent order " + order + " precedes or ties actual Bootstrap order " + runnerOrder + ".");
                            else if (!dependency.Proved) result.Error("StartupDependencyUnproved", path + " -> " + type.FullName + " needs a closed actual module/dependency inventory before its early callback can be admitted.");
                        }
                        return new ResourceTypeIdentity { assembly = type.Assembly.GetName().Name, @namespace = type.Namespace ?? "", type = type.Name };
                    });
                    foreach (string unknown in parsed.unknowns) result.Error("StartupAssetUninspectable", path + ": " + unknown);
                    foreach (var managed in parsed.managedReferenceTypes)
                        if (candidates.Contains(AssemblyNamePolicy.Canonical(managed.assembly))) result.Error(root.Value == "PreloadedAsset" ? "PreloadedCandidateAsset" : "StartupCandidateAsset", path + " -> managed reference " + managed.assembly + "::" + managed.type);
                }
                if (!scripts.Any(script => script.IsBootstrapRunner && script.AssetPath == scenePath)) result.Error("BootstrapRunnerMissing", "Actual runner is absent from the startup scene.");
                // Preserve the existing GUID/package/SerializeReference preflight;
                // this new evidence does not replace its established admission.
                ShadowAssemblyPolicyValidator.ValidateBootstrapResources(roots.SelectMany(root => new[] { root.Key }.Concat(AssetDatabase.GetDependencies(root.Key, true) ?? new string[0]))
                    .Select(path => ShadowAssemblyPolicyValidator.ResolveAssetPath(project, path)).Where(File.Exists), project, policy, result);
            }
            catch (Exception error) { result.Error("ExecutionStartupInventoryFailed", error.Message); }
            return new ShadowExecutionEditorInventory(scenePath, runnerPath, bootstrapRunner, runnerOrder, scripts, preloaded, result);
        }

        private sealed class StartupDependency
        {
            internal bool Proved = true, Dependent;
            internal readonly List<string> Evidence = new List<string>();
        }

        private static StartupDependency ReadStartupDependency(Assembly actual, HashSet<string> candidates, ShadowPolicyConfiguration policy, CompiledAssemblySet compiled)
        {
            var result = new StartupDependency(); var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Queue<string>(); pending.Enqueue(actual.FullName);
            var loaded = AppDomain.CurrentDomain.GetAssemblies().GroupBy(assembly => assembly.FullName, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
            var edges = policy == null || policy.dependencies == null ? new DeclaredRuntimeDependency[0] : policy.dependencies.runtimeDependencies ?? new DeclaredRuntimeDependency[0];
            var entries = policy == null || policy.dependencies == null ? new BootstrapEntrypointDeclaration[0] : policy.dependencies.bootstrapEntrypoints ?? new BootstrapEntrypointDeclaration[0];
            while (pending.Count != 0)
            {
                string identity = pending.Dequeue(); var name = new AssemblyName(identity).Name;
                if (!seen.Add(name)) continue;
                if (seen.Count > 4096) { result.Proved = false; result.Evidence.Add("analysis-limit:4096"); break; }
                if (candidates.Contains(name)) { result.Dependent = true; result.Evidence.Add("candidate:" + identity); continue; }
                foreach (string provider in edges.Where(edge => edge != null && string.Equals(AssemblyNamePolicy.Canonical(edge.consumer), name, StringComparison.OrdinalIgnoreCase)).Select(edge => edge.provider)
                    .Concat(entries.Where(entry => entry != null && string.Equals(AssemblyNamePolicy.Canonical(entry.consumer), name, StringComparison.OrdinalIgnoreCase)).Select(entry => entry.provider)).Distinct(StringComparer.OrdinalIgnoreCase))
                { result.Evidence.Add("declared:" + name + " -> " + provider); pending.Enqueue(provider); }
                if (compiled != null)
                {
                    dnlib.DotNet.ModuleDefMD module;
                    if (!compiled.Modules.TryGetValue(name, out module)) { result.Proved = false; result.Evidence.Add("missing-compiled:" + identity); continue; }
                    if (name == actual.GetName().Name && module.Assembly.FullName != actual.FullName)
                    { result.Proved = false; result.Evidence.Add("compiled-script-identity-mismatch:" + actual.FullName + " -> " + module.Assembly.FullName); continue; }
                    result.Evidence.Add("compiled:" + module.Assembly.FullName + "|mvid=" + module.Mvid);
                    foreach (var reference in module.GetAssemblyRefs())
                    {
                        string deferred = module.Assembly.Name.String + " -> " + AssemblyIdentityUtil.AssemblyReferenceKey(reference);
                        if (compiled.DeferredFacadeReferences.Contains(deferred)) { result.Evidence.Add("verified-deferred-facade:" + deferred); continue; }
                        result.Evidence.Add("compiled-reference:" + module.Assembly.FullName + " -> " + reference.FullName); pending.Enqueue(reference.FullName);
                    }
                }
                else
                {
                    Assembly[] matches;
                    if (identity.IndexOf(',') < 0)
                    {
                        var identities = loaded.Keys.Where(key => string.Equals(new AssemblyName(key).Name, name, StringComparison.OrdinalIgnoreCase)).ToArray();
                        if (identities.Length == 1) identity = identities[0];
                    }
                    if (!loaded.TryGetValue(identity, out matches) || matches.Length != 1)
                    { result.Proved = false; result.Evidence.Add("missing-current-editor:" + identity); continue; }
                    string location = matches[0].Location;
                    if (string.IsNullOrEmpty(location) || !File.Exists(location)) { result.Proved = false; result.Evidence.Add("unbound-current-editor:" + identity); continue; }
                    result.Evidence.Add("current-editor:" + identity + "|sha256=" + ShadowHash.File(location));
                    foreach (var reference in matches[0].GetReferencedAssemblies())
                    { result.Evidence.Add("current-editor-reference:" + identity + " -> " + reference.FullName); pending.Enqueue(reference.FullName); }
                }
            }
            return result;
        }

        private static string[] StartupCallbacks(Type type)
        {
            var callbacks = new HashSet<string>(StringComparer.Ordinal);
            for (Type current = type; current != null && current != typeof(MonoBehaviour) && current != typeof(ScriptableObject); current = current.BaseType)
                foreach (MethodInfo method in current.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                    if ((method.Name == "Awake" || method.Name == "OnEnable" || method.Name == "Start") && method.GetParameters().Length == 0) callbacks.Add(method.Name);
            return callbacks.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        }

        private static void ValidateActiveRootRunner(string yaml, MonoScript runner, ShadowPolicyValidationResult result)
        {
            string guid; long scriptId;
            if (!AssetDatabase.TryGetGUIDAndLocalFileIdentifier(runner, out guid, out scriptId) || !yaml.StartsWith("%YAML", StringComparison.Ordinal))
            { result.Error("BootstrapRunnerUninspectable", "Runner requires actual MonoScript identity and a text-serialized scene."); return; }
            var headers = Regex.Matches(yaml, @"(?m)^--- !u!(?<class>\d+) &(?<id>-?\d+)[^\r\n]*");
            var documents = new Dictionary<string, string>(StringComparer.Ordinal); var kinds = new Dictionary<string, string>(StringComparer.Ordinal);
            for (int i = 0; i < headers.Count; ++i)
            {
                string id = headers[i].Groups["id"].Value;
                documents.Add(id, yaml.Substring(headers[i].Index, (i + 1 == headers.Count ? yaml.Length : headers[i + 1].Index) - headers[i].Index)); kinds.Add(id, headers[i].Groups["class"].Value);
            }
            var selected = documents.Where(pair => kinds[pair.Key] == "114" && Regex.IsMatch(pair.Value,
                @"(?m)^  m_Script: \{fileID: " + scriptId + @", guid: " + Regex.Escape(guid) + @", type: 3\}\r?$" )).ToArray();
            if (selected.Length != 1) { result.Error("BootstrapRunnerMultiplicity", "Startup scene must contain exactly one direct Bootstrap runner."); return; }
            string component = selected[0].Value;
            var gameObject = Regex.Match(component, @"(?m)^  m_GameObject: \{fileID: (?<id>-?\d+)\}\r?$");
            string body;
            bool active = Regex.IsMatch(component, @"(?m)^  m_Enabled: 1\r?$") && gameObject.Success && documents.TryGetValue(gameObject.Groups["id"].Value, out body) &&
                kinds[gameObject.Groups["id"].Value] == "1" && Regex.IsMatch(body, @"(?m)^  m_IsActive: 1\r?$") &&
                Regex.IsMatch(body, @"(?m)^  - component: \{fileID: " + selected[0].Key + @"\}\r?$") &&
                documents.Any(pair => kinds[pair.Key] == "4" && Regex.IsMatch(body, @"(?m)^  - component: \{fileID: " + pair.Key + @"\}\r?$") &&
                    Regex.IsMatch(pair.Value, @"(?m)^  m_GameObject: \{fileID: " + gameObject.Groups["id"].Value + @"\}\r?$") && Regex.IsMatch(pair.Value, @"(?m)^  m_Father: \{fileID: 0\}\r?$"));
            if (!active) result.Error("BootstrapRunnerInactive", "Bootstrap must be enabled on a directly serialized active root GameObject.");
        }
    }
}
