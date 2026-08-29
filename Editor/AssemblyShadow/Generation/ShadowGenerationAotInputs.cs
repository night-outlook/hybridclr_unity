using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dnlib.DotNet;
using HybridCLR.Editor.Meta;

namespace HybridCLR.Editor.AssemblyShadow
{
    [Serializable]
    public sealed class ShadowGenerationAotReceipt
    {
        public int schemaVersion = 1;
        public string kind = "GeneratorStripInputs";
        public string planHash, target, architecture, sourceDirectory, inventoryHash;
        public string[] excludedSelectedNames;
        public GenerationImage[] images;
    }

    public sealed class VerifiedGenerationAotInputs
    {
        private readonly ShadowGenerationAotReceipt receipt;
        public string Root { get; private set; }
        public string InventoryHash { get { return receipt.inventoryHash; } }
        public ShadowGenerationAotReceipt Receipt { get { return GenerationIO.Clone(receipt); } }
        public IReadOnlyList<string> RootNames
        { get { return Array.AsReadOnly(receipt.images.Where(image => !receipt.excludedSelectedNames.Contains(AssemblyIdentityUtil.CanonicalName(image.name))).Select(image => image.name).ToArray()); } }
        internal VerifiedGenerationAotInputs(string root, ShadowGenerationAotReceipt receipt)
        { Root = Path.GetFullPath(root); this.receipt = GenerationIO.Clone(receipt); }
        public void VerifyUnchanged(VerifiedGenerationPlan plan)
        { ShadowGenerationAotInputs.ReadAndVerify(Root, plan, InventoryHash); }
        public GenerationAssemblyResolver CreateResolver(VerifiedGenerationPlan plan)
        {
            plan.VerifyUnchanged(); VerifyUnchanged(plan);
            var files = plan.Receipt.catalog.ToDictionary(image => AssemblyIdentityUtil.CanonicalName(image.name), image => new GenerationAssemblyResolver.Input(plan.Root, image));
            foreach (var image in receipt.images.Where(image => !receipt.excludedSelectedNames.Contains(AssemblyIdentityUtil.CanonicalName(image.name))))
                files[AssemblyIdentityUtil.CanonicalName(image.name)] = new GenerationAssemblyResolver.Input(Root, image);
            return new GenerationAssemblyResolver(files.Values);
        }
    }

    public static class ShadowGenerationAotInputs
    {
        public const string ReceiptName = "generation-aot-inputs.json";
        public static VerifiedGenerationAotInputs Capture(string outputRoot, VerifiedGenerationPlan plan, string strippedAotDirectory,
            IEnumerable<string> explicitExcludedSelectedNames)
        {
            plan.VerifyUnchanged(); GenerationIO.NewRoot(outputRoot);
            var images = new List<GenerationImage>();
            foreach (string path in Directory.GetFiles(strippedAotDirectory, "*.dll", SearchOption.TopDirectoryOnly).OrderBy(path => path, StringComparer.Ordinal))
            {
                string name;
                using (var module = ModuleDefMD.Load(File.ReadAllBytes(path))) name = module.Assembly.Name.String;
                ShadowHash.Require(!images.Any(image => string.Equals(image.name, name, StringComparison.OrdinalIgnoreCase)), "GenerationCollision", name);
                string relative = "Assemblies/" + name + ".dll"; Directory.CreateDirectory(Path.Combine(outputRoot, "Assemblies"));
                File.Copy(path, ShadowHash.SafeChild(outputRoot, relative));
                string pdb = Path.ChangeExtension(path, ".pdb"), pdbRelative = null;
                if (File.Exists(pdb)) { pdbRelative = "Assemblies/" + name + ".pdb"; File.Copy(pdb, ShadowHash.SafeChild(outputRoot, pdbRelative)); }
                images.Add(GenerationIO.Image(outputRoot, relative, pdbRelative, Path.GetFullPath(path), "StrippedAot"));
            }
            var receipt = new ShadowGenerationAotReceipt { planHash = plan.PlanHash, target = plan.Target, architecture = plan.Architecture,
                sourceDirectory = Path.GetFullPath(strippedAotDirectory), images = images.ToArray(),
                excludedSelectedNames = (explicitExcludedSelectedNames ?? new string[0]).Select(AssemblyIdentityUtil.CanonicalName).OrderBy(name => name, StringComparer.Ordinal).ToArray() };
            receipt.inventoryHash = ComputeHash(receipt); GenerationIO.Write(Path.Combine(outputRoot, ReceiptName), receipt);
            return ReadAndVerify(outputRoot, plan, receipt.inventoryHash);
        }
        public static VerifiedGenerationAotInputs ReadAndVerify(string root, VerifiedGenerationPlan plan, string expectedHash = null)
        {
            plan.VerifyUnchanged(); var receipt = GenerationIO.Read<ShadowGenerationAotReceipt>(Path.Combine(root, ReceiptName));
            ShadowHash.Require(receipt.schemaVersion == 1 && receipt.kind == "GeneratorStripInputs" && receipt.planHash == plan.PlanHash &&
                receipt.target == plan.Target && receipt.architecture == plan.Architecture && receipt.inventoryHash == ComputeHash(receipt) &&
                (expectedHash == null || receipt.inventoryHash == expectedHash), "GenerationAotReceipt", root);
            GenerationIO.VerifyImages(root, receipt.images);
            ShadowHash.Require(receipt.images.Length > 0 && receipt.excludedSelectedNames != null, "GenerationAotEmpty", root);
            RequireExclusions(receipt.images, plan.SelectedNames, receipt.excludedSelectedNames);
            foreach (var image in receipt.images)
                ShadowHash.Require(image.path == "Assemblies/" + image.name + ".dll" && image.role == "StrippedAot", "GenerationAotReceipt", image.name);
            var expected = new HashSet<string>(receipt.images.Select(image => Path.GetFullPath(ShadowHash.SafeChild(root, image.path))), StringComparer.Ordinal);
            ShadowHash.Require(expected.SetEquals(Directory.GetFiles(root, "*.dll", SearchOption.AllDirectories).Select(Path.GetFullPath)), "GenerationCatalog", root);
            return new VerifiedGenerationAotInputs(root, receipt);
        }
        internal static void RequireExclusions(GenerationImage[] images, IEnumerable<string> selectedNames, string[] excludedNames)
        {
            string[] collisions = images.Select(image => AssemblyIdentityUtil.CanonicalName(image.name))
                .Intersect(selectedNames.Select(AssemblyIdentityUtil.CanonicalName)).OrderBy(name => name, StringComparer.Ordinal).ToArray();
            ShadowHash.Require(excludedNames != null && collisions.SequenceEqual(excludedNames), "GenerationCollision",
                "Explicit exclusions must equal the exact selected/stripped collision set.");
        }
        public static string ComputeHash(ShadowGenerationAotReceipt receipt)
        { var copy = GenerationIO.Clone(receipt); copy.inventoryHash = null; return ShadowHash.Text("assembly-shadow-generation-aot:1\n" + Convert.ToBase64String(GenerationIO.Bytes(copy))); }
    }

    // Both the legacy path resolver and subsequent dnlib type resolution stay
    // inside the same byte-bound catalog. Missing optional netstandard lookups
    // throw too, preventing AssemblyCacheBase's ambient fallback branch.
    public sealed class GenerationAssemblyResolver : HybridCLR.Editor.Meta.IAssemblyResolver, IDisposable
    {
        internal sealed class Input
        {
            internal string root;
            internal GenerationImage image;
            internal Input(string root, GenerationImage image) { this.root = root; this.image = GenerationIO.Clone(image); }
        }
        private readonly Dictionary<string, Input> inputs;
        private readonly List<ModuleDefMD> resolutionModules = new List<ModuleDefMD>();
        internal GenerationAssemblyResolver(string root, IEnumerable<GenerationImage> images) : this(images.Select(image => new Input(root, image))) { }
        internal GenerationAssemblyResolver(IEnumerable<Input> values)
        { inputs = values.ToDictionary(input => AssemblyIdentityUtil.CanonicalName(input.image.name), StringComparer.Ordinal); }
        public string ResolveAssembly(string assemblyName, bool throwExIfNotFind)
        {
            Input input;
            ShadowHash.Require(inputs.TryGetValue(AssemblyIdentityUtil.CanonicalName(assemblyName), out input), "GenerationResolutionMissing", assemblyName);
            GenerationIO.VerifyImages(input.root, new[] { input.image });
            return ShadowHash.SafeChild(input.root, input.image.path);
        }
        public IReadOnlyList<GenerationImage> Catalog
        { get { return Array.AsReadOnly(inputs.Values.Select(input => GenerationIO.Clone(input.image)).OrderBy(image => image.name, StringComparer.Ordinal).ToArray()); } }
        public void Bind(AssemblyCacheBase cache)
        {
            var resolver = new ExactResolver(); var context = cache.ModCtx;
            context.AssemblyResolver = resolver; context.Resolver = new Resolver(resolver) { ProjectWinMDRefs = false };
            foreach (Input input in inputs.Values)
            {
                ModuleDefMD module;
                if (!cache.LoadedModules.TryGetValue(input.image.name, out module))
                { module = ModuleDefMD.Load(File.ReadAllBytes(ResolveAssembly(input.image.name, true)), context); resolutionModules.Add(module); }
                resolver.Add(module, input.image.assemblyIdentity);
            }
        }
        private sealed class ExactResolver : dnlib.DotNet.IAssemblyResolver
        {
            private readonly Dictionary<string, AssemblyDef> assemblies = new Dictionary<string, AssemblyDef>(StringComparer.Ordinal);
            internal void Add(ModuleDefMD module, string identity)
            { ShadowHash.Require(module.Assembly.FullName == identity && !assemblies.ContainsKey(identity), "GenerationIdentity", identity); assemblies.Add(identity, module.Assembly); }
            public AssemblyDef Resolve(IAssembly assembly, ModuleDef sourceModule)
            {
                AssemblyDef found;
                ShadowHash.Require(assembly != null && assemblies.TryGetValue(assembly.FullName, out found), "GenerationResolutionMissing", assembly == null ? "null" : assembly.FullName);
                return assemblies[assembly.FullName];
            }
        }
        public void Dispose() { foreach (var module in resolutionModules) module.Dispose(); resolutionModules.Clear(); }
    }
}
