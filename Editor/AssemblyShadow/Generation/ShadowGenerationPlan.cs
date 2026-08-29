using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using dnlib.DotNet;
using UnityEditor;

namespace HybridCLR.Editor.AssemblyShadow
{
    [Serializable]
    public sealed class GenerationImage
    {
        public string name, assemblyIdentity, mvid, path, sha256, pdbPath, pdbSha256, sourcePath, role;
    }

    [Serializable]
    public sealed class ShadowGenerationPlanReceipt
    {
        public int schemaVersion = 1;
        public string kind = "CompileOnlyGeneration";
        public string purpose = "NotDeployable";
        public string target, architecture, unityVersion, snapshotHash, snapshotReceiptSha256, policySha256, planHash;
        public ShadowSourcePins sourcePins;
        public string[] explicitRoots, closure, loadOrder, ordinaryAssemblies;
        public GenerationImage[] images, catalog;
    }

    public sealed class VerifiedGenerationPlan
    {
        private readonly ShadowGenerationPlanReceipt receipt;
        public string Root { get; private set; }
        public string PlanHash { get { return receipt.planHash; } }
        public string Target { get { return receipt.target; } }
        public string Architecture { get { return receipt.architecture; } }
        public IReadOnlyList<string> Closure { get { return Array.AsReadOnly((string[])receipt.closure.Clone()); } }
        public IReadOnlyList<string> LoadOrder { get { return Array.AsReadOnly((string[])receipt.loadOrder.Clone()); } }
        public IReadOnlyList<string> SelectedNames { get { return Array.AsReadOnly(receipt.images.Select(image => image.name).ToArray()); } }
        public ShadowGenerationPlanReceipt Receipt { get { return GenerationIO.Clone(receipt); } }
        internal VerifiedGenerationPlan(string root, ShadowGenerationPlanReceipt receipt)
        { Root = Path.GetFullPath(root); this.receipt = GenerationIO.Clone(receipt); }
        public void VerifyUnchanged()
        { ShadowGenerationPlan.ReadAndVerify(Root, (BuildTarget)Enum.Parse(typeof(BuildTarget), Target), Architecture, PlanHash); }
        public GenerationAssemblyResolver CreateResolver()
        { VerifyUnchanged(); return new GenerationAssemblyResolver(Root, receipt.catalog); }
    }

    public static class ShadowGenerationPlan
    {
        public const string ReceiptName = "generation-plan.json";

        public static VerifiedGenerationPlan Create(string outputRoot, string compileSnapshotRoot,
            ShadowPolicyConfiguration policy, IEnumerable<string> explicitRoots, IEnumerable<string> ordinaryHotUpdatePaths)
        {
            ShadowHash.Require(policy != null, "GenerationPolicy", "Explicit compile-time roles and graph declarations are required.");
            AssemblySnapshotReceipt source = AssemblySnapshot.ReadAndVerify(compileSnapshotRoot, false);
            ShadowHash.Require(source.kind == "CompilePlayerScripts", "GenerationSnapshotKind", "A generation plan requires compile-only inputs, not Player acceptance evidence.");
            string snapshotBoundary = Path.GetFullPath(compileSnapshotRoot).TrimEnd(Path.DirectorySeparatorChar);
            string destinationRoot = Path.GetFullPath(outputRoot).TrimEnd(Path.DirectorySeparatorChar);
            ShadowHash.Require(destinationRoot != snapshotBoundary && !destinationRoot.StartsWith(snapshotBoundary + Path.DirectorySeparatorChar, StringComparison.Ordinal),
                "GenerationPath", "The immutable plan cannot be created inside its source snapshot.");
            GenerationIO.NewRoot(outputRoot);
            GenerationIO.CopyTree(compileSnapshotRoot, Path.Combine(outputRoot, "Snapshot"));
            byte[] policyBytes = GenerationIO.Bytes(policy);
            File.WriteAllBytes(Path.Combine(outputRoot, "policy.json"), policyBytes);
            var receipt = new ShadowGenerationPlanReceipt
            {
                target = source.target, architecture = source.architecture, unityVersion = source.unityVersion,
                snapshotHash = source.snapshotHash, sourcePins = GenerationIO.Clone(source.sourcePins),
                snapshotReceiptSha256 = ShadowHash.File(Path.Combine(compileSnapshotRoot, AssemblySnapshot.ReceiptName)),
                policySha256 = ShadowHash.Bytes(policyBytes), explicitRoots = Names(explicitRoots),
                ordinaryAssemblies = new string[0], closure = new string[0], loadOrder = new string[0]
            };
            using (var set = LoadSet(outputRoot, policy))
            {
                var graph = new AssemblyReferenceGraph(set.Assemblies.Values, policy.dependencies);
                receipt.closure = graph.ReverseClosure(receipt.explicitRoots);
                receipt.loadOrder = graph.LoadOrder(receipt.closure);
                var catalog = AssemblySnapshot.AllFiles(source).Select(file => GenerationIO.Image(outputRoot,
                    "Snapshot/" + file.path, "Snapshot/" + (file.pdbPath ?? ""), file.sourcePath,
                    set.Assemblies.ContainsKey(AssemblyIdentityUtil.CanonicalName(file.name)) ? "Compiler" : "Reference")).ToList();
                var ordinary = new List<GenerationImage>();
                foreach (string sourcePath in ordinaryHotUpdatePaths ?? new string[0])
                {
                    ShadowHash.Require(File.Exists(sourcePath), "GenerationImageMissing", sourcePath);
                    using (var module = ModuleDefMD.Load(File.ReadAllBytes(sourcePath)))
                    {
                        string name = module.Assembly.Name.String, key = AssemblyIdentityUtil.CanonicalName(name);
                        AssemblyDescriptor descriptor;
                        ShadowHash.Require(set.Assemblies.TryGetValue(key, out descriptor) && descriptor.classification == AssemblyClassification.NormalHotUpdate &&
                            !descriptor.isBootstrap && !descriptor.isShadowCapable, "GenerationOrdinaryRole", name);
                        ShadowHash.Require(!ordinary.Any(image => AssemblyIdentityUtil.CanonicalName(image.name) == key), "GenerationCollision", name);
                        ShadowHash.Require(module.Assembly.FullName == set.GetModule(name).Assembly.FullName &&
                            AssemblySemanticHasher.Compute(module).semanticHash == AssemblySemanticHasher.Compute(set.GetModule(name)).semanticHash,
                            "GenerationOrdinaryMismatch", name + " does not match the current compiled ordinary hot-update assembly.");
                        string relative = "Ordinary/" + name + ".dll";
                        Directory.CreateDirectory(Path.Combine(outputRoot, "Ordinary"));
                        File.WriteAllBytes(ShadowHash.SafeChild(outputRoot, relative), File.ReadAllBytes(sourcePath));
                        string pdb = Path.ChangeExtension(sourcePath, ".pdb"), pdbRelative = null;
                        if (File.Exists(pdb)) { pdbRelative = "Ordinary/" + name + ".pdb"; File.Copy(pdb, ShadowHash.SafeChild(outputRoot, pdbRelative)); }
                        var ordinaryImage = GenerationIO.Image(outputRoot, relative, pdbRelative, Path.GetFullPath(sourcePath), "Ordinary");
                        ordinary.Add(ordinaryImage); catalog.RemoveAll(item => AssemblyIdentityUtil.CanonicalName(item.name) == key); catalog.Add(ordinaryImage);
                    }
                }
                receipt.ordinaryAssemblies = Names(ordinary.Select(image => image.name));
                RequireOrdinaryCoverage(set, receipt.ordinaryAssemblies);
                var selected = new HashSet<string>(receipt.loadOrder.Select(AssemblyIdentityUtil.CanonicalName), StringComparer.Ordinal);
                ShadowHash.Require(!ordinary.Any(image => selected.Contains(AssemblyIdentityUtil.CanonicalName(image.name))), "GenerationCollision", "Ordinary and shadow inputs overlap.");
                receipt.catalog = catalog.OrderBy(image => image.name, StringComparer.Ordinal).ToArray();
                receipt.images = receipt.loadOrder.Select(name => GenerationIO.Clone(catalog.Single(image => AssemblyIdentityUtil.CanonicalName(image.name) == AssemblyIdentityUtil.CanonicalName(name))))
                    .Concat(ordinary.OrderBy(image => AssemblyIdentityUtil.CanonicalName(image.name), StringComparer.Ordinal)).ToArray();
                foreach (var image in receipt.images.Where(image => image.role != "Ordinary")) image.role = "Shadow";
            }
            receipt.planHash = ComputeHash(receipt); GenerationIO.Write(Path.Combine(outputRoot, ReceiptName), receipt);
            return ReadAndVerify(outputRoot, (BuildTarget)Enum.Parse(typeof(BuildTarget), source.target), source.architecture, receipt.planHash);
        }

        public static VerifiedGenerationPlan ReadAndVerify(string root, BuildTarget expectedTarget, string expectedArchitecture, string expectedHash = null)
        {
            var receipt = GenerationIO.Read<ShadowGenerationPlanReceipt>(Path.Combine(root, ReceiptName));
            ShadowHash.Require(receipt != null && receipt.schemaVersion == 1 && receipt.kind == "CompileOnlyGeneration" && receipt.purpose == "NotDeployable",
                "GenerationSchema", "A generation plan is not a baseline or deployment manifest.");
            ShadowHash.Require(receipt.target == expectedTarget.ToString() && receipt.architecture == expectedArchitecture, "GenerationTarget", root);
            ShadowHash.Require(receipt.planHash == ComputeHash(receipt) && (expectedHash == null || expectedHash == receipt.planHash), "GenerationPlanHash", root);
            var snapshot = AssemblySnapshot.ReadAndVerify(Path.Combine(root, "Snapshot"), false);
            ShadowHash.Require(snapshot.kind == "CompilePlayerScripts" && snapshot.snapshotHash == receipt.snapshotHash && snapshot.target == receipt.target && snapshot.architecture == receipt.architecture &&
                snapshot.unityVersion == receipt.unityVersion && GenerationIO.Equal(snapshot.sourcePins, receipt.sourcePins) &&
                snapshot.sourcePins.target == receipt.target && snapshot.sourcePins.architecture == receipt.architecture &&
                snapshot.sourcePins.unityVersion == receipt.unityVersion &&
                ShadowHash.File(Path.Combine(root, "Snapshot", AssemblySnapshot.ReceiptName)) == receipt.snapshotReceiptSha256,
                "GenerationSnapshotMismatch", root);
            string policyPath = Path.Combine(root, "policy.json");
            ShadowHash.Require(ShadowHash.File(policyPath) == receipt.policySha256, "GenerationPolicyHash", root);
            var policy = GenerationIO.Read<ShadowPolicyConfiguration>(policyPath);
            using (var set = LoadSet(root, policy))
            {
                var graph = new AssemblyReferenceGraph(set.Assemblies.Values, policy.dependencies);
                ShadowHash.Require(receipt.explicitRoots != null && receipt.explicitRoots.SequenceEqual(Names(receipt.explicitRoots)), "GenerationRoots", root);
                ShadowHash.Require(receipt.closure != null && receipt.loadOrder != null && graph.ReverseClosure(receipt.explicitRoots).SequenceEqual(receipt.closure) &&
                    graph.LoadOrder(receipt.closure).SequenceEqual(receipt.loadOrder), "GenerationClosure", root);
                GenerationIO.VerifyImages(root, receipt.catalog); GenerationIO.VerifyImages(root, receipt.images);
                ShadowHash.Require(receipt.ordinaryAssemblies != null && receipt.ordinaryAssemblies.SequenceEqual(Names(receipt.ordinaryAssemblies)), "GenerationOrdinaryRole", root);
                RequireOrdinaryCoverage(set, receipt.ordinaryAssemblies);
                var expected = receipt.loadOrder.Concat(receipt.ordinaryAssemblies).Select(AssemblyIdentityUtil.CanonicalName).ToArray();
                ShadowHash.Require(expected.Distinct().Count() == expected.Length && receipt.images.Select(image => AssemblyIdentityUtil.CanonicalName(image.name)).SequenceEqual(expected), "GenerationSelection", root);
                var originals = AssemblySnapshot.AllFiles(snapshot).ToDictionary(file => AssemblyIdentityUtil.CanonicalName(file.name));
                ShadowHash.Require(originals.Keys.OrderBy(name => name).SequenceEqual(receipt.catalog.Select(image => AssemblyIdentityUtil.CanonicalName(image.name)).OrderBy(name => name)), "GenerationCatalog", root);
                foreach (var image in receipt.catalog)
                {
                    string key = AssemblyIdentityUtil.CanonicalName(image.name); bool ordinary = receipt.ordinaryAssemblies.Contains(key);
                    if (ordinary)
                    {
                        var descriptor = set.Get(key);
                        ShadowHash.Require(image.role == "Ordinary" && image.path == "Ordinary/" + image.name + ".dll" &&
                            descriptor.classification == AssemblyClassification.NormalHotUpdate && !descriptor.isShadowCapable && !descriptor.isBootstrap,
                            "GenerationOrdinaryRole", image.name);
                        using (var module = ModuleDefMD.Load(ShadowHash.SafeChild(root, image.path)))
                            ShadowHash.Require(module.Assembly.FullName == set.GetModule(key).Assembly.FullName &&
                                AssemblySemanticHasher.Compute(module).semanticHash == AssemblySemanticHasher.Compute(set.GetModule(key)).semanticHash, "GenerationOrdinaryMismatch", key);
                    }
                    else
                    {
                        SnapshotFile file = originals[key];
                        ShadowHash.Require(image.role == (set.Assemblies.ContainsKey(key) ? "Compiler" : "Reference") &&
                            image.path == "Snapshot/" + file.path && image.sourcePath == file.sourcePath && image.sha256 == file.sha256 && image.pdbSha256 == file.pdbSha256 &&
                            image.pdbPath == (string.IsNullOrEmpty(file.pdbPath) ? null : "Snapshot/" + file.pdbPath), "GenerationCatalog", key);
                    }
                }
                foreach (var image in receipt.images)
                {
                    var source = receipt.catalog.Single(item => item.name == image.name);
                    var expectedImage = GenerationIO.Clone(source); expectedImage.role = source.role == "Ordinary" ? "Ordinary" : "Shadow";
                    ShadowHash.Require(GenerationIO.Equal(image, expectedImage), "GenerationSelection", image.name);
                }
            }
            return new VerifiedGenerationPlan(root, receipt);
        }

        private static CompiledAssemblySet LoadSet(string root, ShadowPolicyConfiguration policy)
        {
            string snapshotRoot = Path.Combine(root, "Snapshot");
            return ShadowBaselineManifestBuilder.LoadSnapshot(snapshotRoot, policy, AssemblySnapshot.ReadAndVerify(snapshotRoot, false));
        }
        private static void RequireOrdinaryCoverage(CompiledAssemblySet set, string[] ordinary)
        {
            string[] expected = Names(set.Assemblies.Values.Where(assembly => assembly.classification == AssemblyClassification.NormalHotUpdate).Select(assembly => assembly.name));
            ShadowHash.Require(expected.SequenceEqual(ordinary), "GenerationOrdinaryCoverage", "Supply exact staged bytes for every current ordinary hot-update assembly.");
        }
        private static string[] Names(IEnumerable<string> names)
        {
            string[] supplied = (names ?? new string[0]).ToArray();
            ShadowHash.Require(supplied.All(name => !string.IsNullOrWhiteSpace(name) && name.IndexOfAny(new[] { '/', '\\', ',' }) < 0),
                "GenerationName", "Expected simple assembly names, not paths or display identities.");
            string[] result = supplied.Select(AssemblyIdentityUtil.CanonicalName).ToArray();
            ShadowHash.Require(result.All(name => !string.IsNullOrWhiteSpace(name)) && result.Distinct().Count() == result.Length, "GenerationCollision", "Duplicate or empty input name.");
            return result.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        }
        public static string ComputeHash(ShadowGenerationPlanReceipt receipt)
        { var copy = GenerationIO.Clone(receipt); copy.planHash = null; return ShadowHash.Text("assembly-shadow-generation-plan:1\n" + Convert.ToBase64String(GenerationIO.Bytes(copy))); }
    }

    internal static class GenerationIO
    {
        internal static byte[] Bytes<T>(T value)
        { using (var stream = new MemoryStream()) { new DataContractJsonSerializer(typeof(T)).WriteObject(stream, value); return stream.ToArray(); } }
        internal static T Clone<T>(T value)
        { using (var stream = new MemoryStream(Bytes(value))) return (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream); }
        internal static bool Equal<T>(T left, T right) { return Bytes(left).SequenceEqual(Bytes(right)); }
        internal static void Write<T>(string path, T value) { File.WriteAllBytes(path, Bytes(value)); }
        internal static T Read<T>(string path)
        {
            byte[] bytes = File.ReadAllBytes(path); T result;
            using (var stream = new MemoryStream(bytes)) result = (T)new DataContractJsonSerializer(typeof(T)).ReadObject(stream);
            // This new domain has one canonical wire representation. Unknown,
            // missing or duplicate fields cannot disappear during deserialization.
            ShadowHash.Require(Bytes(result).SequenceEqual(bytes), "GenerationJson", path);
            return result;
        }
        internal static void NewRoot(string root)
        { ShadowHash.Require(!Directory.Exists(root) && !File.Exists(root), "GenerationExists", root); Directory.CreateDirectory(root); }
        internal static void CopyTree(string source, string target)
        {
            source = Path.GetFullPath(source);
            Directory.CreateDirectory(target);
            foreach (string directory in Directory.GetDirectories(source, "*", SearchOption.AllDirectories).Prepend(source))
                ShadowHash.Require((File.GetAttributes(directory) & System.IO.FileAttributes.ReparsePoint) == 0, "GenerationSymlink", directory);
            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                ShadowHash.Require((File.GetAttributes(file) & System.IO.FileAttributes.ReparsePoint) == 0, "GenerationSymlink", file);
                string destination = Path.Combine(target, file.Substring(Path.GetFullPath(source).TrimEnd(Path.DirectorySeparatorChar).Length + 1));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)); File.Copy(file, destination);
            }
        }
        internal static GenerationImage Image(string root, string path, string pdb, string source, string role)
        {
            string full = ShadowHash.SafeChild(root, path);
            if (string.IsNullOrEmpty(pdb) || pdb.EndsWith("/", StringComparison.Ordinal)) pdb = null;
            using (var module = ModuleDefMD.Load(File.ReadAllBytes(full)))
            {
                ShadowHash.Require(module.Assembly != null && module.Mvid.HasValue, "GenerationIdentity", path);
                return new GenerationImage { name = module.Assembly.Name.String, assemblyIdentity = module.Assembly.FullName,
                    mvid = module.Mvid.ToString(), path = path, sha256 = ShadowHash.File(full), pdbPath = pdb,
                    pdbSha256 = pdb == null ? null : ShadowHash.File(ShadowHash.SafeChild(root, pdb)), sourcePath = source, role = role };
            }
        }
        internal static void VerifyImages(string root, GenerationImage[] images)
        {
            ShadowHash.Require(images != null && images.All(image => image != null) &&
                images.Select(image => AssemblyIdentityUtil.CanonicalName(image.name)).Distinct().Count() == images.Length, "GenerationCollision", root);
            foreach (var image in images)
            {
                NoSymlink(root, image.path);
                if (!string.IsNullOrEmpty(image.pdbPath)) NoSymlink(root, image.pdbPath);
                ShadowHash.Require(GenerationIO.Equal(Image(root, image.path, image.pdbPath, image.sourcePath, image.role), image), "GenerationImageMismatch", image.name);
            }
        }
        private static void NoSymlink(string root, string path)
        {
            for (string current = ShadowHash.SafeChild(root, path), boundary = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar); current != null &&
                (current == boundary || current.StartsWith(boundary + Path.DirectorySeparatorChar, StringComparison.Ordinal)); current = Path.GetDirectoryName(current))
                ShadowHash.Require((File.GetAttributes(current) & System.IO.FileAttributes.ReparsePoint) == 0, "GenerationSymlink", current);
        }
    }
}
