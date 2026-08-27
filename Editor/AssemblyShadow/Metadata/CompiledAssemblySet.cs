using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;

namespace HybridCLR.Editor.AssemblyShadow
{
    public sealed class CompiledAssemblySet : IDisposable
    {
        private readonly Dictionary<string, AssemblyDescriptor> _assemblies;
        private readonly Dictionary<string, ModuleDefMD> _modules;
        private readonly IResolver _resolver;
        private readonly IReadOnlyList<string> _deferredFacadeReferences;
        private bool _disposed;

        internal CompiledAssemblySet(Dictionary<string, AssemblyDescriptor> assemblies, Dictionary<string, ModuleDefMD> modules,
            IResolver resolver, IEnumerable<string> deferredFacadeReferences)
        {
            _assemblies = new Dictionary<string, AssemblyDescriptor>(assemblies, StringComparer.OrdinalIgnoreCase);
            _modules = new Dictionary<string, ModuleDefMD>(modules, StringComparer.OrdinalIgnoreCase);
            _resolver = resolver;
            _deferredFacadeReferences = Array.AsReadOnly(ShadowHash.Sorted(deferredFacadeReferences));
        }

        public IReadOnlyDictionary<string, AssemblyDescriptor> Assemblies { get { return _assemblies; } }
        public IReadOnlyDictionary<string, ModuleDefMD> Modules { get { return _modules; } }
        public IReadOnlyList<string> DeferredFacadeReferences { get { return _deferredFacadeReferences; } }

        public AssemblyDescriptor Get(string name)
        {
            ThrowIfDisposed();
            AssemblyDescriptor result;
            if (!_assemblies.TryGetValue(AssemblyIdentityUtil.CanonicalName(name), out result))
                throw new ShadowBuildException("AssemblyNotFound", "Assembly was not loaded: " + name);
            return result;
        }

        public ModuleDefMD GetModule(string name)
        {
            ThrowIfDisposed();
            ModuleDefMD result;
            if (!_modules.TryGetValue(AssemblyIdentityUtil.CanonicalName(name), out result))
                throw new ShadowBuildException("AssemblyNotFound", "Module was not loaded: " + name);
            return result;
        }

        public TypeDef ResolveType(ITypeDefOrRef type)
        {
            ThrowIfDisposed();
            if (type == null)
                return null;
            ITypeDefOrRef scope = type is TypeSpec ? ((TypeSpec)type).ScopeType : type;
            TypeDef resolved = scope as TypeDef;
            TypeRef reference = scope as TypeRef;
            // Do not invoke a caller-owned TypeRef's resolver: it may search the GAC
            // or a different profile. Resolution stays within this set's dictionary.
            try { if (reference != null) resolved = _resolver.Resolve(reference, reference.Module); }
            catch (Exception) { }
            if (resolved != null && IsLoadedModule(resolved.Module))
                return resolved;

            string key = AssemblyIdentityUtil.TypeKey(type);
            foreach (ModuleDefMD module in _modules.Values)
            {
                foreach (TypeDef candidate in module.GetTypes())
                {
                    if (string.Equals(AssemblyIdentityUtil.TypeKey(candidate), key, StringComparison.Ordinal))
                        return candidate;
                }
            }
            return null;
        }

        private bool IsLoadedModule(ModuleDef module)
        {
            return module != null && _modules.Values.Any(candidate => object.ReferenceEquals(candidate, module));
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            foreach (ModuleDefMD module in _modules.Values)
            {
                try { module.Dispose(); }
                catch (Exception) { }
            }
            _modules.Clear();
            _assemblies.Clear();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException("CompiledAssemblySet");
        }
    }
}
