using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;

namespace HybridCLR.AssemblyShadow.CodeGen
{
    public sealed class ReflectionRetargetingModuleEvidence
    {
        public string AssemblyIdentity { get; internal set; }
        public string Sha256 { get; internal set; }
        public string Mvid { get; internal set; }
    }

    public sealed class ReflectionRetargetingForwarderEvidence
    {
        public string TypeFullName { get; internal set; }
        public string DestinationAssemblyIdentity { get; internal set; }
    }

    // Captured bytes are evidence, not an assembly-name trust list. The caller
    // must bind all supplied bytes to its exact Unity/target/architecture receipt.
    // This API never reads a host path, resolves through a GAC, or loads code.
    public sealed class CapturedReflectionRetargetingProfile
    {
        public const int PolicyVersion = 1;
        public const string RequiredFacadeIdentity = "netstandard, Version=2.1.0.0, Culture=neutral, PublicKeyToken=cc7b13ffcd2ddd51";
        private readonly Dictionary<string, string> destinations = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, HashSet<string>> definitions = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        private readonly List<ReflectionRetargetingModuleEvidence> runtimeModules = new List<ReflectionRetargetingModuleEvidence>();
        public string FacadeSha256 { get; private set; }
        public string SourceAssemblyIdentity { get; private set; }
        private string profileHash;

        private CapturedReflectionRetargetingProfile() { }

        public ReflectionRetargetingModuleEvidence[] RuntimeModules
        {
            get { return runtimeModules.Select(value => new ReflectionRetargetingModuleEvidence {
                AssemblyIdentity = value.AssemblyIdentity, Sha256 = value.Sha256, Mvid = value.Mvid }).ToArray(); }
        }

        public string ComputeHash() { return profileHash; }

        public ReflectionRetargetingForwarderEvidence[] Forwarders
        {
            get { return destinations.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new ReflectionRetargetingForwarderEvidence {
                TypeFullName = pair.Key, DestinationAssemblyIdentity = pair.Value }).ToArray(); }
        }

        public static CapturedReflectionRetargetingProfile Load(byte[] capturedFacadePe, string expectedFacadeSha256,
            IEnumerable<byte[]> capturedRuntimeFrameworkModules)
        {
            BindingChecks.Require(capturedFacadePe != null && capturedFacadePe.Length > 0 && BindingChecks.IsHash(expectedFacadeSha256) &&
                BindingChecks.Sha256(capturedFacadePe) == expectedFacadeSha256, "RetargetingFacadeHashMismatch", "Captured facade bytes must match their receipt SHA-256.");
            BindingChecks.Require(capturedRuntimeFrameworkModules != null, "MissingRetargetingDestinations", "Captured runtime framework modules are required.");
            var result = new CapturedReflectionRetargetingProfile { FacadeSha256 = expectedFacadeSha256 };
            using (var facade = ModuleDefMD.Load(capturedFacadePe, new ModuleCreationOptions { TryToLoadPdbFromDisk = false }))
            {
                BindingChecks.Require(facade.Assembly != null && facade.Assembly.FullName == RequiredFacadeIdentity,
                    "UnsupportedRetargetingFacade", "Version 1 accepts only the exact netstandard 2.1 facade identity.");
                result.SourceAssemblyIdentity = facade.Assembly.FullName;
                var exports = new HashSet<ExportedType>(facade.ExportedTypes);
                BindingChecks.Require(exports.Count == facade.ExportedTypes.Count && exports.Count > 0, "InvalidRetargetingForwarder", "Facade exports must be nonempty and unique.");
                foreach (var export in facade.ExportedTypes)
                {
                    string name, destination;
                    Forwarder(export, exports, new HashSet<ExportedType>(), out name, out destination);
                    BindingChecks.Require(!result.destinations.ContainsKey(name), "AmbiguousRetargetingForwarder", name);
                    result.destinations.Add(name, destination);
                }
            }
            foreach (var bytes in capturedRuntimeFrameworkModules)
            {
                BindingChecks.Require(bytes != null && bytes.Length > 0, "InvalidRetargetingDestination", "Captured runtime module bytes are required.");
                using (var module = ModuleDefMD.Load(bytes, new ModuleCreationOptions { TryToLoadPdbFromDisk = false }))
                {
                    BindingChecks.Require(module.Assembly != null && module.Assembly.FullName != result.SourceAssemblyIdentity &&
                        !result.definitions.ContainsKey(module.Assembly.FullName), "AmbiguousRetargetingDestination", "Destination assembly identities must be unique and distinct from the facade.");
                    var names = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var type in module.GetTypes())
                        BindingChecks.Require(names.Add(type.FullName), "AmbiguousRetargetingDestinationType", type.FullName);
                    result.definitions.Add(module.Assembly.FullName, names);
                    result.runtimeModules.Add(new ReflectionRetargetingModuleEvidence { AssemblyIdentity = module.Assembly.FullName,
                        Sha256 = BindingChecks.Sha256(bytes), Mvid = module.Mvid.HasValue ? module.Mvid.Value.ToString("D") : "" });
                }
            }
            using (var hash = new BindingHash("assembly-shadow-reflection-retargeting-profile:1"))
            {
                hash.Add(PolicyVersion); hash.Add(result.FacadeSha256); hash.Add(result.SourceAssemblyIdentity);
                hash.Add(result.destinations.Count);
                foreach (var pair in result.destinations.OrderBy(pair => pair.Key, StringComparer.Ordinal)) { hash.Add(pair.Key); hash.Add(pair.Value); }
                hash.Add(result.runtimeModules.Count);
                foreach (var module in result.runtimeModules.OrderBy(module => module.AssemblyIdentity, StringComparer.Ordinal))
                { hash.Add(module.AssemblyIdentity); hash.Add(module.Sha256); hash.Add(module.Mvid); }
                result.profileHash = hash.Finish();
            }
            return result;
        }

        private static void Forwarder(ExportedType export, HashSet<ExportedType> exports, HashSet<ExportedType> chain,
            out string name, out string destination)
        {
            BindingChecks.Require(export != null && exports.Contains(export) && chain.Count < 128 && chain.Add(export), "InvalidRetargetingForwarder", "Missing, cyclic or excessively nested ExportedType implementation.");
            string simpleName = export.TypeName.String;
            BindingChecks.Require(!string.IsNullOrEmpty(simpleName) && simpleName.IndexOf('/') < 0, "InvalidRetargetingForwarder", "Invalid exported type name.");
            var parent = export.Implementation as ExportedType;
            if (parent != null)
            {
                BindingChecks.Require(string.IsNullOrEmpty(export.TypeNamespace.String), "InvalidRetargetingForwarder", "Nested exports cannot declare another namespace.");
                Forwarder(parent, exports, chain, out name, out destination);
                name += "/" + simpleName;
            }
            else
            {
                var assembly = export.Implementation as AssemblyRef;
                BindingChecks.Require(export.IsForwarder && assembly != null && assembly.FullName != RequiredFacadeIdentity,
                    "InvalidRetargetingForwarder", "Top-level exports must forward to an explicit different assembly identity.");
                name = string.IsNullOrEmpty(export.TypeNamespace.String) ? simpleName : export.TypeNamespace.String + "." + simpleName;
                destination = assembly.FullName;
            }
        }

        internal string LinkedScope(ITypeDefOrRef type)
        {
            var source = type.DefinitionAssembly;
            BindingChecks.Require(source != null, "UnsupportedTypeScope", type.FullName);
            if (source.FullName != SourceAssemblyIdentity) return source.FullName;
            string destination;
            BindingChecks.Require(destinations.TryGetValue(type.FullName, out destination), "MissingRetargetingForwarder", type.FullName);
            HashSet<string> names;
            BindingChecks.Require(definitions.TryGetValue(destination, out names) && names.Contains(type.FullName),
                "MissingRetargetingDestinationType", type.FullName + " => " + destination);
            return destination;
        }
    }
}
