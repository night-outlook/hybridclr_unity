using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;

namespace HybridCLR.Editor.AssemblyShadow
{
    // A bounded reflection-name grammar, not a runtime type resolver. In
    // particular, dnlib's reflection parser normalizes primitive scopes and
    // accepts duplicate/unknown assembly attributes; neither is policy proof.
    internal static class ReflectionTypeLiteralResolver
    {
        internal sealed class Component
        {
            internal string provider, typeName;
        }

        private sealed class Name
        {
            internal string type;
            internal Identity assembly;
            internal readonly List<Name> arguments = new List<Name>();
        }

        private sealed class Identity
        {
            internal string name, culture, token;
            internal Version version;
        }

        private sealed class InvalidLiteral : Exception { }

        internal static bool TryResolve(IReadOnlyDictionary<string, ModuleDefMD> modules, string literal,
            string requiredAssembly, out Component[] components)
        {
            components = null;
            if (modules == null || string.IsNullOrEmpty(literal) || literal.Length > 16384) return false;
            try
            {
                var parser = new Parser(literal);
                Name name = parser.Parse();
                if (requiredAssembly != null)
                {
                    // Assembly.GetType takes a type name, not an outer AQN.
                    if (name.assembly != null) return false;
                    name.assembly = new Identity { name = requiredAssembly };
                }
                var found = new List<Component>();
                if (!Resolve(modules, name, found)) return false;
                components = found.GroupBy(value => value.provider + "\n" + value.typeName, StringComparer.Ordinal)
                    .Select(group => group.First()).ToArray();
                return true;
            }
            catch (InvalidLiteral) { return false; }
        }

        private static bool Resolve(IReadOnlyDictionary<string, ModuleDefMD> modules, Name name, List<Component> found)
        {
            // No search-all-assemblies or namespace-based core fallback. Every
            // constructed argument must name its assembly explicitly.
            if (name.assembly == null) return false;
            ModuleDefMD module = FindModule(modules, name.assembly);
            if (module == null) return false;
            var visited = new HashSet<ModuleDefMD>();
            TypeDef definition = null;
            while (module != null && visited.Count < 32 && visited.Add(module))
            {
                TypeDef[] definitions = module.GetTypes().Where(type => type.FullName == name.type).ToArray();
                ExportedType[] exports = module.ExportedTypes.Where(type => type.FullName == name.type).ToArray();
                if (definitions.Length + exports.Length != 1) return false;
                found.Add(new Component { provider = Key(modules, module), typeName = name.type });
                if (definitions.Length == 1) { definition = definitions[0]; break; }
                AssemblyRef target = ForwardingTarget(exports[0]);
                if (target == null) return false;
                module = FindModule(modules, new Identity
                {
                    name = target.Name.String, version = target.Version,
                    culture = Culture(target.Culture), token = Token(target.PublicKeyOrToken)
                });
                // Forwarders are metadata identities, never partial names.
                if (module == null || module.Assembly.FullName != target.FullName) return false;
            }
            if (definition == null || !ValidArity(definition, name.arguments.Count)) return false;
            foreach (Name argument in name.arguments) if (!Resolve(modules, argument, found)) return false;
            return true;
        }

        private static bool ValidArity(TypeDef definition, int count)
        {
            if (definition.GenericParameters.Count != count) return false;
            var visited = new HashSet<TypeDef>();
            for (TypeDef type = definition; type != null; type = type.DeclaringType)
            {
                if (visited.Count >= 32 || !visited.Add(type)) return false;
                string value = type.Name.String;
                int marker = value.IndexOf('`'), own = 0;
                if (marker >= 0 && (!int.TryParse(value.Substring(marker + 1), out own) || own < 1)) return false;
                int inherited = type.DeclaringType == null ? 0 : type.DeclaringType.GenericParameters.Count;
                if (type.GenericParameters.Count != inherited + own) return false;
                // Duplicate/out-of-range generic parameter numbers are not a
                // valid definition, even if the total happens to match.
                if (!type.GenericParameters.Select(parameter => (int)parameter.Number).OrderBy(number => number)
                    .SequenceEqual(Enumerable.Range(0, type.GenericParameters.Count))) return false;
            }
            return true;
        }

        private static AssemblyRef ForwardingTarget(ExportedType export)
        {
            var visited = new HashSet<ExportedType>();
            while (export != null && visited.Count < 32 && visited.Add(export))
            {
                var target = export.Implementation as AssemblyRef;
                if (target != null) return export.IsForwarder ? target : null;
                // Nested exported children need not carry the Forwarder bit.
                var parent = export.Implementation as ExportedType;
                if (parent == null || parent.Module != export.Module ||
                    export.Module.ExportedTypes.Count(item => item.FullName == parent.FullName) != 1) return null;
                export = parent;
            }
            return null;
        }

        private static ModuleDefMD FindModule(IReadOnlyDictionary<string, ModuleDefMD> modules, Identity identity)
        {
            ModuleDefMD[] matches = modules.Values.Distinct().Where(module => module.Assembly != null &&
                string.Equals(module.Assembly.Name.String, identity.name, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1) return null;
            ModuleDefMD result = matches[0];
            IAssembly assembly = result.Assembly;
            if (result.Assembly.Modules.Count != 1 || result.Metadata.TablesStream.FileTable.Rows != 0) return null;
            if (identity.version != null && assembly.Version != identity.version ||
                identity.culture != null && !string.Equals(Culture(assembly.Culture), identity.culture, StringComparison.OrdinalIgnoreCase) ||
                identity.token != null && !string.Equals(Token(assembly.PublicKeyOrToken), identity.token, StringComparison.OrdinalIgnoreCase)) return null;
            return result;
        }

        private static string Key(IReadOnlyDictionary<string, ModuleDefMD> modules, ModuleDefMD module)
        { return modules.First(pair => ReferenceEquals(pair.Value, module)).Key; }
        private static string Culture(UTF8String culture)
        { return UTF8String.IsNullOrEmpty(culture) || culture.String == "neutral" ? "neutral" : culture.String; }
        private static string Token(PublicKeyBase key)
        {
            PublicKeyToken token = key == null ? null : key.Token;
            return token == null || token.Data == null || token.Data.Length == 0 ? "null" : token.ToString();
        }

        private sealed class Parser
        {
            private readonly string text;
            private int position, nodes;
            internal Parser(string text) { this.text = text; }
            internal Name Parse()
            {
                if (text.Any(character => character < ' ' || character > '~')) throw new InvalidLiteral();
                Name result = ReadName(0);
                White(); if (position != text.Length) throw new InvalidLiteral();
                return result;
            }
            private Name ReadName(int depth)
            {
                if (depth > 32 || ++nodes > 256) throw new InvalidLiteral();
                White(); int start = position;
                while (position < text.Length && text[position] != '[' && text[position] != ']' && text[position] != ',') ++position;
                string type = text.Substring(start, position - start).Trim();
                ValidateTypeName(type);
                var result = new Name { type = type.Replace('+', '/') };
                if (Take('['))
                {
                    // Only closed, explicitly assembly-qualified arguments.
                    // Arrays, pointers, byrefs, escapes and unqualified generic
                    // arguments are deliberately unsupported in this version.
                    do
                    {
                        White(); Require('[');
                        Name argument = ReadName(depth + 1);
                        if (argument.assembly == null) throw new InvalidLiteral();
                        result.arguments.Add(argument); White(); Require(']'); White();
                    } while (Take(','));
                    Require(']'); White();
                }
                if (Take(',')) result.assembly = ReadIdentity();
                return result;
            }
            private Identity ReadIdentity()
            {
                White(); string name = ReadPart();
                if (name.Length == 0 || name.Any(character => !NameCharacter(character))) throw new InvalidLiteral();
                var result = new Identity { name = name };
                var fields = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                while (Take(','))
                {
                    White(); string field = ReadPart(); int equals = field.IndexOf('=');
                    if (equals <= 0 || field.IndexOf('=', equals + 1) >= 0) throw new InvalidLiteral();
                    string key = field.Substring(0, equals).Trim(), value = field.Substring(equals + 1).Trim();
                    if (!fields.Add(key) || value.Length == 0) throw new InvalidLiteral();
                    if (key.Equals("Version", StringComparison.OrdinalIgnoreCase))
                    {
                        string[] parts = value.Split('.'); ushort number;
                        if (parts.Length != 4 || parts.Any(part => part.Length == 0 || part.Any(character => character < '0' || character > '9') ||
                            !ushort.TryParse(part, out number))) throw new InvalidLiteral();
                        result.version = new Version(value);
                    }
                    else if (key.Equals("Culture", StringComparison.OrdinalIgnoreCase))
                    {
                        if (value.Any(character => !NameCharacter(character) || character == '.')) throw new InvalidLiteral();
                        result.culture = value;
                    }
                    else if (key.Equals("PublicKeyToken", StringComparison.OrdinalIgnoreCase))
                    {
                        if (!value.Equals("null", StringComparison.OrdinalIgnoreCase) &&
                            (value.Length != 16 || value.Any(character => !Uri.IsHexDigit(character)))) throw new InvalidLiteral();
                        result.token = value;
                    }
                    else throw new InvalidLiteral();
                }
                return result;
            }
            private string ReadPart()
            {
                int start = position;
                while (position < text.Length && text[position] != ',' && text[position] != ']') ++position;
                return text.Substring(start, position - start).Trim();
            }
            private static void ValidateTypeName(string value)
            {
                if (value.Length == 0) throw new InvalidLiteral();
                foreach (string part in value.Split('+'))
                {
                    if (part.Length == 0) throw new InvalidLiteral();
                    int marker = part.IndexOf('`'); string plain = marker < 0 ? part : part.Substring(0, marker);
                    if (plain.Split('.').Any(segment => segment.Length == 0 || segment.Any(character =>
                        !(character >= 'a' && character <= 'z' || character >= 'A' && character <= 'Z' ||
                          character >= '0' && character <= '9' || character == '_')))) throw new InvalidLiteral();
                    int arity;
                    if (marker >= 0 && (part.Substring(marker + 1).Length == 0 || part.Substring(marker + 1).Any(character => character < '0' || character > '9') ||
                        !int.TryParse(part.Substring(marker + 1), out arity) || arity < 1 || arity > 256)) throw new InvalidLiteral();
                }
            }
            private static bool NameCharacter(char value)
            { return value >= 'a' && value <= 'z' || value >= 'A' && value <= 'Z' || value >= '0' && value <= '9' || value == '.' || value == '_' || value == '-'; }
            private bool Take(char value)
            { if (position >= text.Length || text[position] != value) return false; ++position; return true; }
            private void Require(char value) { if (!Take(value)) throw new InvalidLiteral(); }
            private void White() { while (position < text.Length && text[position] == ' ') ++position; }
        }
    }
}
