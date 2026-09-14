using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Pdb;

namespace HybridCLR.AssemblyShadow.CodeGen
{
    /// <summary>Preserve local constants; never replace an unknown value with null.</summary>
    public static class ReflectionPdbConstants
    {
        private sealed class Row
        {
            internal MethodDef Method;
            internal PdbScope Scope;
            internal string ScopePath;
            internal int Index;
            internal PdbConstant Constant;
        }

        public static string Snapshot(ModuleDef module)
        {
            using (var hash = new BindingHash("reflection-pdb-local-constants:1"))
            {
                foreach (var row in Rows(module))
                {
                    var c = row.Constant;
                    hash.Add(row.Method.FullName); hash.Add(row.ScopePath); hash.Add(row.Index);
                    hash.Add(Offset(row.Method, row.Scope.Start)); hash.Add(Offset(row.Method, row.Scope.End));
                    hash.Add(c.Name); hash.Add(TypeIdentity(c.Type)); hash.Add(ValueIdentity(c.Value));
                }
                return hash.Finish();
            }
        }

        public static string Describe(ModuleDef module)
        {
            var text = new StringBuilder();
            foreach (var row in Rows(module).Take(128))
                text.Append(row.Method.FullName).Append(" scope=").Append(row.ScopePath).Append(" constant=")
                    .Append(row.Constant.Name).Append(" type=").Append(row.Constant.Type == null ? "<null>" : row.Constant.Type.FullName)
                    .Append(" element=").Append(row.Constant.Type == null ? "<null>" : row.Constant.Type.ElementType.ToString())
                    .Append(" valueType=").Append(row.Constant.Value == null ? "<null>" : row.Constant.Value.GetType().FullName).AppendLine();
            return text.ToString();
        }

        // Explicit route B only. Route A supplies the correct context before PDB reading.
        public static int NormalizeResolvedEnums(ModuleDef module)
        {
            int changed = 0;
            foreach (var row in Rows(module))
            {
                var c = row.Constant;
                TypeSig type = c.Type == null ? null : c.Type.RemovePinnedAndModifiers();
                var klass = type as ClassSig;
                if (klass == null || c.Value == null || c.Value is byte[]) continue;
                TypeDef resolved = klass.TypeDefOrRef.ResolveTypeDef();
                BindingChecks.Require(resolved != null && resolved.IsEnum, "UnprovenPdbEnumConstant",
                    "Non-null class constant is not a resolved enum: " + row.Method.FullName + "/" + c.Name);
                TypeSig underlying = resolved.GetEnumUnderlyingType().RemovePinnedAndModifiers();
                BindingChecks.Require(ValueMatches(underlying.ElementType, c.Value), "PdbEnumUnderlyingTypeMismatch",
                    "Constant value does not match its resolved enum underlying type: " + c.Name);
                c.Type = ReplaceLeaf(c.Type, new ValueTypeSig(klass.TypeDefOrRef));
                ++changed;
            }
            return changed;
        }

        private static TypeSig ReplaceLeaf(TypeSig original, TypeSig replacement)
        {
            var required = original as CModReqdSig;
            if (required != null) return new CModReqdSig(required.Modifier, ReplaceLeaf(required.Next, replacement));
            var optional = original as CModOptSig;
            if (optional != null) return new CModOptSig(optional.Modifier, ReplaceLeaf(optional.Next, replacement));
            BindingChecks.Require(original is ClassSig, "UnsupportedPdbConstantModifier", "Only class constants with custom modifiers may be normalized.");
            return replacement;
        }

        private static bool ValueMatches(ElementType type, object value)
        {
            switch (type)
            {
                case ElementType.Boolean: return value is bool;
                case ElementType.Char: return value is char;
                case ElementType.I1: return value is sbyte;
                case ElementType.U1: return value is byte;
                case ElementType.I2: return value is short;
                case ElementType.U2: return value is ushort;
                case ElementType.I4: return value is int;
                case ElementType.U4: return value is uint;
                case ElementType.I8: return value is long;
                case ElementType.U8: return value is ulong;
                default: return false;
            }
        }

        private static string TypeIdentity(TypeSig signature)
        {
            if (signature == null) return "<null>";
            var leaf = signature.RemovePinnedAndModifiers() as TypeDefOrRefSig;
            string identity = signature.FullName + "@" + (signature.DefinitionAssembly == null ? "" : signature.DefinitionAssembly.FullNameToken);
            TypeDef resolved = leaf == null ? null : leaf.TypeDefOrRef.ResolveTypeDef();
            return resolved != null && resolved.IsEnum ? "enum:" + identity + ":" + resolved.GetEnumUnderlyingType().FullName : identity;
        }

        private static string ValueIdentity(object value)
        {
            if (value == null) return "null";
            var bytes = value as byte[];
            if (bytes != null) return "bytes:" + Convert.ToBase64String(bytes);
            if (value is float) return "Single:" + BitConverter.ToString(BitConverter.GetBytes((float)value));
            if (value is double) return "Double:" + BitConverter.ToString(BitConverter.GetBytes((double)value));
            if (value is DateTime) return "DateTime:" + ((DateTime)value).Ticks.ToString(CultureInfo.InvariantCulture);
            if (value is decimal) return "Decimal:" + string.Join(",", decimal.GetBits((decimal)value).Select(n => n.ToString(CultureInfo.InvariantCulture)));
            if (value is char) return "Char:" + ((int)(char)value).ToString(CultureInfo.InvariantCulture);
            return value.GetType().FullName + ":" + Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static int Offset(MethodDef method, dnlib.DotNet.Emit.Instruction instruction)
        { return instruction == null ? method.Body.Instructions.Count : method.Body.Instructions.IndexOf(instruction); }

        private static IEnumerable<Row> Rows(ModuleDef module)
        {
            foreach (var method in module.GetTypes().SelectMany(type => type.Methods)
                .Where(method => method.HasBody && !method.Name.String.StartsWith(ReflectionBindingTransformer.GuardPrefix, StringComparison.Ordinal))
                .OrderBy(method => method.FullName, StringComparer.Ordinal))
                if (method.Body.PdbMethod != null && method.Body.PdbMethod.Scope != null)
                    foreach (var row in Rows(method, method.Body.PdbMethod.Scope, "0")) yield return row;
        }

        private static IEnumerable<Row> Rows(MethodDef method, PdbScope scope, string path)
        {
            for (int i = 0; i < scope.Constants.Count; ++i)
                yield return new Row { Method = method, Scope = scope, ScopePath = path, Index = i, Constant = scope.Constants[i] };
            for (int i = 0; i < scope.Scopes.Count; ++i)
                foreach (var row in Rows(method, scope.Scopes[i], path + "/" + i.ToString(CultureInfo.InvariantCulture))) yield return row;
        }
    }
}
