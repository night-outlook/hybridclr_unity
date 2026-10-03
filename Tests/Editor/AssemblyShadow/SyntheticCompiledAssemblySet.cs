using System;
using System.Collections.Generic;
using System.Reflection;
using dnlib.DotNet;

namespace HybridCLR.Editor.AssemblyShadow.Tests
{
    /// <summary>
    /// Constructs mutable, in-memory policy fixtures, NOT loaded source evidence.
    /// These fixtures intentionally lack file/hash authority: eligibility must
    /// reject them. Tests needing byte-bound qualification use DnlibAssemblyLoader.
    /// Keep this single checked reflection boundary outside production code.
    /// </summary>
    internal static class SyntheticCompiledAssemblySet
    {
        internal static CompiledAssemblySet Create(
            Dictionary<string, AssemblyDescriptor> descriptors,
            Dictionary<string, ModuleDefMD> modules, IResolver resolver,
            IEnumerable<string> deferredFacadeReferences)
        {
            if (descriptors == null) throw new ArgumentNullException(nameof(descriptors));
            if (modules == null) throw new ArgumentNullException(nameof(modules));
            if (resolver == null) throw new ArgumentNullException(nameof(resolver));
            if (deferredFacadeReferences == null) throw new ArgumentNullException(nameof(deferredFacadeReferences));
            var signature = new[] {
                typeof(Dictionary<string, AssemblyDescriptor>), typeof(Dictionary<string, ModuleDefMD>),
                typeof(IResolver), typeof(IEnumerable<string>), typeof(IEnumerable<CompiledAssemblySource>)
            };
            var constructor = typeof(CompiledAssemblySet).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic, null, signature, null);
            if (constructor == null)
                throw new InvalidOperationException("SyntheticCompiledAssemblySet: reviewed five-argument constructor contract changed.");
            // Empty is deliberate, not a default for real DLL input. Do not
            // invent paths/hashes for mutable policy descriptors or modules.
            return (CompiledAssemblySet)constructor.Invoke(new object[] {
                descriptors, modules, resolver, deferredFacadeReferences, Array.Empty<CompiledAssemblySource>()
            });
        }
    }
}
