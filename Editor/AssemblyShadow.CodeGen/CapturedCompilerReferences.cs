using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using dnlib.DotNet;

namespace HybridCLR.AssemblyShadow.CodeGen
{
    /// <summary>Metadata-only resolution from the compiler's explicit reference list.</summary>
    public sealed class CapturedCompilerReferences : IAssemblyResolver, IDisposable
    {
        private readonly Dictionary<string, ModuleDefMD> modules = new Dictionary<string, ModuleDefMD>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> hashes = new Dictionary<string, string>(StringComparer.Ordinal);
        private bool disposed;
        public ModuleContext Context { get; private set; }

        public CapturedCompilerReferences(IEnumerable<string> referencePaths)
        {
            Context = new ModuleContext(this);
            try
            {
                foreach (string path in (referencePaths ?? new string[0]).Distinct(StringComparer.Ordinal).OrderBy(p => p, StringComparer.Ordinal))
                {
                    BindingChecks.Require(!string.IsNullOrWhiteSpace(path) && Path.IsPathRooted(path) && File.Exists(path),
                        "InvalidCompilerReference", "An existing absolute compiler reference is required: " + path);
                    byte[] bytes = File.ReadAllBytes(path);
                    var module = ModuleDefMD.Load(bytes, new ModuleCreationOptions { Context = Context, TryToLoadPdbFromDisk = false });
                    bool owned = false;
                    try
                    {
                        BindingChecks.Require(module.Assembly != null, "InvalidCompilerReference", "Reference is not an assembly: " + path);
                        string identity = module.Assembly.FullNameToken;
                        string hash = BindingChecks.Sha256(bytes);
                        string previous;
                        if (hashes.TryGetValue(identity, out previous))
                            BindingChecks.Require(previous == hash, "AmbiguousCompilerReference", "Different compiler bytes have the same full identity: " + identity);
                        else
                        {
                            modules.Add(identity, module); hashes.Add(identity, hash); owned = true;
                        }
                    }
                    finally { if (!owned) module.Dispose(); }
                }
            }
            catch { Dispose(); throw; }
        }

        public AssemblyDef Resolve(IAssembly assembly, ModuleDef sourceModule)
        {
            if (disposed) throw new ObjectDisposedException(nameof(CapturedCompilerReferences));
            if (assembly == null) return null;
            if (sourceModule != null && sourceModule.Assembly != null && sourceModule.Assembly.FullNameToken == assembly.FullNameToken)
                return sourceModule.Assembly;
            ModuleDefMD module;
            return modules.TryGetValue(assembly.FullNameToken, out module) ? module.Assembly : null;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach (var module in modules.Values) module.Dispose();
            modules.Clear(); hashes.Clear();
        }
    }
}
