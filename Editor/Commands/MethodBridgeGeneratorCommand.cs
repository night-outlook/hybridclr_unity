using HybridCLR.Editor;
using HybridCLR.Editor.ABI;
using HybridCLR.Editor.Meta;
using HybridCLR.Editor.MethodBridge;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;
using HybridCLR.Editor.AssemblyShadow;

namespace HybridCLR.Editor.Commands
{
    using Analyzer = HybridCLR.Editor.MethodBridge.Analyzer;
    public class MethodBridgeGeneratorCommand
    {
        public static ShadowGenerationOutput GenerateMethodBridgeAndReversePInvokeWrapper(VerifiedGenerationPlan plan,
            VerifiedGenerationAotInputs aot, string templateFile, string outputFile, bool development, int maxIterations = 20)
        {
            plan.VerifyUnchanged(); aot.VerifyUnchanged(plan); ShadowGenerationOutput.NewOutput(outputFile);
            ShadowHash.Require(maxIterations > 0 && maxIterations <= 20, "GenerationIterations", maxIterations.ToString());
            using (var resolver = aot.CreateResolver(plan))
            {
                var roots = aot.RootNames.Concat(plan.SelectedNames).Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal).ToList();
                var collector = new AssemblyReferenceDeepCollector(resolver, roots); resolver.Bind(collector);
                var analyzer = new Analyzer(new Analyzer.Options { Collector = collector, MaxIterationCount = maxIterations }); analyzer.Run();
                var cache = new AssemblyCache(resolver);
                foreach (string name in plan.SelectedNames) cache.LoadModule(name);
                resolver.Bind(cache);
                var selected = plan.SelectedNames.ToList();
                var reverse = new MonoPInvokeCallbackAnalyzer(cache, selected); reverse.Run();
                var calli = new CalliAnalyzer(cache, selected); calli.Run();
                var pinvoke = new PInvokeAnalyzer(cache, selected); pinvoke.Run();
                var native = calli.CalliMethodSignatures.Concat(pinvoke.PInvokeMethodSignatures).ToList();
                byte[] template = File.ReadAllBytes(templateFile);
                var generator = new Generator(new Generator.Options { TemplateCode = Encoding.UTF8.GetString(template), OutputFile = outputFile,
                    GenericMethods = analyzer.GenericMethods, ReversePInvokeMethods = reverse.ReversePInvokeMethods,
                    CalliMethodSignatures = native, Development = development });
                GenerationInventory inventory = generator.Prepare(); generator.Generate();
                return ShadowGenerationOutput.Seal(outputFile, plan, aot, new ShadowGenerationOutputReceipt
                {
                    stage = "MethodBridge", development = development, maxIterations = maxIterations, templateSha256 = ShadowHash.Bytes(template),
                    collectorRoots = roots.ToArray(), resolverCatalog = resolver.Catalog.ToArray(),
                    collectorTypes = GenerationSignatures.Sorted(analyzer.GenericTypes.Select(type => GenerationSignatures.Type(type.ToTypeSig()))),
                    collectorMethods = GenerationSignatures.Sorted(analyzer.GenericMethods.Select(GenerationSignatures.Method)),
                    reverseMethods = GenerationSignatures.Sorted(reverse.ReversePInvokeMethods.Select(method => GenerationSignatures.Method(new GenericMethod(method.Method, null, null)))),
                    nativeCallSignatures = GenerationSignatures.Sorted(native.Select(method => (method.Callvention ?? method.MethodSig.CallingConvention) + "|" + GenerationSignatures.Signature(method.MethodSig))),
                    managedToNative = ShadowGenerationOutput.Entries(inventory.ManagedToNative), nativeToManaged = ShadowGenerationOutput.Entries(inventory.NativeToManaged),
                    adjustThunks = ShadowGenerationOutput.Entries(inventory.AdjustThunks), reversePInvoke = ShadowGenerationOutput.Entries(inventory.ReversePInvoke),
                    calli = ShadowGenerationOutput.Entries(inventory.Calli), structMappings = ShadowGenerationOutput.Entries(inventory.StructMappings),
                    nativePointerDispatchHasMethodInfo = inventory.NativePointerDispatchHasMethodInfo,
                    reversePInvokeGuardPolicy = "AbortBeforeExternalDispatch"
                });
            }
        }

        public static void CleanIl2CppBuildCache()
        {
            string il2cppBuildCachePath = SettingsUtil.Il2CppBuildCacheDir;
            if (!Directory.Exists(il2cppBuildCachePath))
            {
                return;
            }
            Debug.Log($"clean il2cpp build cache:{il2cppBuildCachePath}");
            Directory.Delete(il2cppBuildCachePath, true);
        }

        private static void GenerateMethodBridgeCppFile(IReadOnlyCollection<GenericMethod> genericMethods, List<RawMonoPInvokeCallbackMethodInfo> reversePInvokeMethods, IReadOnlyCollection<CallNativeMethodSignatureInfo> calliMethodSignatures, string tempFile, string outputFile)
        {
            string templateCode = File.ReadAllText(tempFile, Encoding.UTF8);
            var g = new Generator(new Generator.Options()
            {
                TemplateCode = templateCode,
                OutputFile = outputFile,
                GenericMethods = genericMethods,
                ReversePInvokeMethods = reversePInvokeMethods,
                CalliMethodSignatures = calliMethodSignatures,
                Development = EditorUserBuildSettings.development,
            });

            g.Generate();
            Debug.LogFormat("[MethodBridgeGeneratorCommand] output:{0}", outputFile);
        }

        [MenuItem("HybridCLR/Generate/MethodBridgeAndReversePInvokeWrapper", priority = 101)]
        public static void GenerateMethodBridgeAndReversePInvokeWrapper()
        {
            BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
            GenerateMethodBridgeAndReversePInvokeWrapper(target);
        }

        public static void GenerateMethodBridgeAndReversePInvokeWrapper(BuildTarget target)
        {
            string aotDllDir = SettingsUtil.GetAssembliesPostIl2CppStripDir(target);
            List<string> aotAssemblyNames = Directory.Exists(aotDllDir) ?
                Directory.GetFiles(aotDllDir, "*.dll", SearchOption.TopDirectoryOnly).Select(Path.GetFileNameWithoutExtension).ToList()
                : new List<string>();
            if (aotAssemblyNames.Count == 0)
            {
                throw new Exception($"no aot assembly found. please run `HybridCLR/Generate/All` or `HybridCLR/Generate/AotDlls` to generate aot dlls before runing `HybridCLR/Generate/MethodBridge`");
            }
            AssemblyReferenceDeepCollector collector = new AssemblyReferenceDeepCollector(MetaUtil.CreateAOTAssemblyResolver(target), aotAssemblyNames);

            var methodBridgeAnalyzer = new Analyzer(new Analyzer.Options
            {
                MaxIterationCount = Math.Min(20, SettingsUtil.HybridCLRSettings.maxMethodBridgeGenericIteration),
                Collector = collector,
            });

            methodBridgeAnalyzer.Run();

            List<string> hotUpdateDlls = SettingsUtil.HotUpdateAssemblyNamesExcludePreserved;
            var cache = new AssemblyCache(MetaUtil.CreateHotUpdateAndAOTAssemblyResolver(target, hotUpdateDlls));

            var reversePInvokeAnalyzer = new MonoPInvokeCallbackAnalyzer(cache, hotUpdateDlls);
            reversePInvokeAnalyzer.Run();

            var calliAnalyzer = new CalliAnalyzer(cache, hotUpdateDlls);
            calliAnalyzer.Run();
            var pinvokeAnalyzer = new PInvokeAnalyzer(cache, hotUpdateDlls);
            pinvokeAnalyzer.Run();
            var callPInvokeMethodSignatures = pinvokeAnalyzer.PInvokeMethodSignatures;

            string templateFile = $"{SettingsUtil.TemplatePathInPackage}/MethodBridge.cpp.tpl";
            string outputFile = $"{SettingsUtil.GeneratedCppDir}/MethodBridge.cpp";

            var callNativeMethodSignatures = calliAnalyzer.CalliMethodSignatures.Concat(pinvokeAnalyzer.PInvokeMethodSignatures).ToList();
            GenerateMethodBridgeCppFile(methodBridgeAnalyzer.GenericMethods, reversePInvokeAnalyzer.ReversePInvokeMethods, callNativeMethodSignatures, templateFile, outputFile);

            CleanIl2CppBuildCache();
        }
    }
}
