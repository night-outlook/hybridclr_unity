using System;
using System.IO;
using dnlib.DotNet;

namespace HybridCLR.Editor.AssemblyShadow
{
    public static class AssemblyIdentityUtil
    {
        public static string CanonicalName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;
            string name = value.Trim().Replace('\\', '/');
            int slash = name.LastIndexOf('/');
            if (slash >= 0) name = name.Substring(slash + 1);
            if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                name = name.Substring(0, name.Length - 4);
            return name.Trim().ToLowerInvariant();
        }

        public static string TypeKey(ITypeDefOrRef type)
        {
            if (type == null)
                return string.Empty;

            string assembly = string.Empty;
            try
            {
                IAssembly definitionAssembly = type.DefinitionAssembly;
                if (definitionAssembly != null)
                    assembly = CanonicalName(definitionAssembly.Name);
            }
            catch (Exception) { }

            if (string.IsNullOrEmpty(assembly) && type.Module != null && type.Module.Assembly != null)
                assembly = CanonicalName(type.Module.Assembly.Name);

            string nested = type.Name.String;
            ITypeDefOrRef declaring = type.DeclaringType;
            string ns = type.Namespace ?? string.Empty;
            while (declaring != null)
            {
                nested = declaring.Name.String + "/" + nested;
                if (string.IsNullOrEmpty(ns))
                    ns = declaring.Namespace ?? string.Empty;
                declaring = declaring.DeclaringType;
            }

            return assembly + ":" + ns + ":" + nested;
        }

        public static string AssemblyReferenceKey(AssemblyRef reference)
        {
            if (reference == null)
                return string.Empty;
            string name = CanonicalName(reference.Name);
            string version = reference.Version == null ? string.Empty : reference.Version.ToString();
            string culture = reference.Culture ?? string.Empty;
            string token = reference.PublicKeyOrToken == null ? string.Empty : Convert.ToBase64String(reference.PublicKeyOrToken.Data ?? new byte[0]);
            return name + ",version=" + version + ",culture=" + culture.ToLowerInvariant() + ",token=" + token +
                ",attributes=" + reference.Attributes + ",hash=" + Convert.ToBase64String(reference.Hash ?? new byte[0]);
        }
    }
}
