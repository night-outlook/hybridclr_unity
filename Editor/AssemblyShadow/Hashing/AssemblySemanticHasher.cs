using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace HybridCLR.Editor.AssemblyShadow
{
    public sealed class SemanticHashOptions
    {
        // Only non-semantic diagnostics/debug metadata is ignored by default.
        // CompilerGeneratedAttribute is intentionally retained because it can affect
        // reflection and generated-code contracts.
        public string[] IgnoredAttributeNames = new[]
        {
            "System.Diagnostics.DebuggableAttribute",
            "System.Diagnostics.DebuggerBrowsableAttribute",
            "System.Diagnostics.DebuggerDisplayAttribute",
            "System.Diagnostics.DebuggerHiddenAttribute",
            "System.Diagnostics.DebuggerNonUserCodeAttribute",
            "System.Diagnostics.DebuggerStepperBoundaryAttribute"
        };

        public bool IsIgnoredAttribute(string fullName)
        {
            return (IgnoredAttributeNames ?? new string[0]).Any(n => string.Equals(n, fullName, StringComparison.Ordinal));
        }
    }

    public static class AssemblySemanticHasher
    {
        public static SemanticHashReport Compute(ModuleDef module, SemanticHashOptions options = null)
        {
            if (module == null)
                throw new ArgumentNullException("module");
            options = options ?? new SemanticHashOptions();

            string identity = WriteIdentity(module);
            string types = WriteTypes(module, options);
            string methods = WriteMethods(module, options);
            string attributes = WriteAttributes(module, options);
            string resources = WriteResources(module, options);
            var sections = new SemanticHashSections
            {
                identity = ShadowHash.Text(identity),
                types = ShadowHash.Text(types),
                methods = ShadowHash.Text(methods),
                attributes = ShadowHash.Text(attributes),
                resources = ShadowHash.Text(resources)
            };
            string aggregate = SemanticHashSchema.Name + "\n" + sections.identity + "\n" + sections.types + "\n" + sections.methods + "\n" + sections.attributes + "\n" + sections.resources;
            return new SemanticHashReport
            {
                schema = SemanticHashSchema.Version,
                assembly = module.Assembly == null ? string.Empty : module.Assembly.Name.String,
                semanticHash = ShadowHash.Text(aggregate),
                sections = sections
            };
        }

        private static string WriteIdentity(ModuleDef module)
        {
            var writer = new CanonicalSignatureWriter();
            writer.Line(SemanticHashSchema.Name);
            writer.Line(module.Assembly == null ? string.Empty : AssemblyIdentityUtil.CanonicalName(module.Assembly.Name));
            writer.Line("module-kind:" + module.Kind);
            writer.Line("runtime-version:" + module.RuntimeVersion);
            ShadowHash.Require(!module.HasNativeEntryPoint, "UnsupportedNativeEntryPoint", "Native entry points are not represented by a managed DLL semantic hash.");
            if (module.ManagedEntryPoint != null)
            {
                var entry = module.ManagedEntryPoint as MethodDef;
                ShadowHash.Require(entry != null, "UnsupportedModuleEntryPoint", "Multi-module entry points require an explicit complete module snapshot.");
                writer.Line("entry:" + MethodKey(entry));
            }
            if (module.Assembly != null)
            {
                writer.Line(module.Assembly.Version == null ? string.Empty : module.Assembly.Version.ToString());
                writer.Line(module.Assembly.Culture ?? string.Empty);
                writer.Line(module.Assembly.Attributes.ToString());
                writer.Line(module.Assembly.PublicKey == null ? string.Empty : Convert.ToBase64String(module.Assembly.PublicKey.Data ?? new byte[0]));
                foreach (AssemblyRef reference in module.GetAssemblyRefs().OrderBy(AssemblyIdentityUtil.AssemblyReferenceKey, StringComparer.Ordinal))
                    writer.Line(AssemblyIdentityUtil.AssemblyReferenceKey(reference));
            }
            return writer.ToString();
        }

        private static string WriteResources(ModuleDef module, SemanticHashOptions options)
        {
            var writer = new CanonicalSignatureWriter();
            foreach (Resource resource in module.Resources.OrderBy(r => r.Name.String, StringComparer.Ordinal))
            {
                writer.Line(resource.Name.String);
                writer.Line(resource.Attributes.ToString());
                var embedded = resource as EmbeddedResource;
                ShadowHash.Require(embedded != null, "UnsupportedLinkedResource", "A DLL snapshot cannot prove external resource bytes: " + resource.Name);
                writer.Line(ShadowHash.Bytes(embedded.CreateReader().ToArray()));
                WriteCustomAttributes(writer, resource.CustomAttributes, options);
            }
            return writer.ToString();
        }

        private static string WriteTypes(ModuleDef module, SemanticHashOptions options)
        {
            var writer = new CanonicalSignatureWriter();
            foreach (TypeDef type in module.GetTypes().OrderBy(AssemblyIdentityUtil.TypeKey, StringComparer.Ordinal))
            {
                writer.Line("type");
                writer.Line(AssemblyIdentityUtil.TypeKey(type));
                writer.Line(type.Attributes.ToString());
                writer.Line(Invariant(type.ClassSize));
                writer.Line(Invariant(type.PackingSize));
                writer.Line(type.BaseType == null ? string.Empty : TypeRefText(type.BaseType));
                foreach (InterfaceImpl iface in type.Interfaces)
                {
                    writer.Line("interface:" + (iface.Interface == null ? string.Empty : TypeRefText(iface.Interface)));
                    WriteCustomAttributes(writer, iface.CustomAttributes, options);
                }
                foreach (GenericParam parameter in type.GenericParameters.OrderBy(p => p.Number))
                {
                    WriteGenericParameter(writer, parameter, options);
                }
                foreach (FieldDef field in type.Fields)
                {
                    WriteField(writer, field, options);
                }
                foreach (PropertyDef property in type.Properties)
                {
                    writer.Line("property:" + PropertyKey(property));
                    writer.Line(property.Attributes.ToString());
                    if (property.HasConstant) writer.Line("constant:" + ConstantText(property.Constant.Value));
                    writer.Line(property.PropertySig == null ? string.Empty : SigText(property.PropertySig.RetType));
                    WriteAccessorLinks(writer, property.GetMethods, "get");
                    WriteAccessorLinks(writer, property.SetMethods, "set");
                    WriteAccessorLinks(writer, property.OtherMethods, "other");
                }
                foreach (EventDef @event in type.Events)
                {
                    writer.Line("event:" + EventKey(@event));
                    writer.Line(@event.Attributes.ToString());
                    writer.Line(@event.EventType == null ? string.Empty : TypeRefText(@event.EventType));
                    WriteAccessorLinks(writer, @event.AddMethod == null ? null : new[] { @event.AddMethod }, "add");
                    WriteAccessorLinks(writer, @event.RemoveMethod == null ? null : new[] { @event.RemoveMethod }, "remove");
                    WriteAccessorLinks(writer, @event.InvokeMethod == null ? null : new[] { @event.InvokeMethod }, "invoke");
                    WriteAccessorLinks(writer, @event.OtherMethods, "other");
                }
                WriteCustomAttributes(writer, type.CustomAttributes, options);
            }
            foreach (var exported in module.ExportedTypes.OrderBy(e => e.FullName, StringComparer.Ordinal))
            {
                writer.Line("exported:" + ExportedTypeText(exported, 0));
                WriteCustomAttributes(writer, exported.CustomAttributes, options);
            }
            return writer.ToString();
        }

        private static string ExportedTypeText(ExportedType type, int depth)
        {
            ShadowHash.Require(depth < 64, "UnsupportedExportedType", "Cyclic or excessively deep exported type.");
            var writer = new CanonicalSignatureWriter();
            writer.Token(type.FullName);
            writer.Token(type.Attributes.ToString());
            var assembly = type.Implementation as AssemblyRef;
            var parent = type.Implementation as ExportedType;
            if (assembly != null) writer.Token(AssemblyIdentityUtil.AssemblyReferenceKey(assembly));
            else if (parent != null) writer.Token(ExportedTypeText(parent, depth + 1));
            else throw new ShadowBuildException("UnsupportedExportedType", "External netmodule implementation is not part of this DLL snapshot: " + type.FullName);
            return writer.ToString();
        }

        private static string WriteMethods(ModuleDef module, SemanticHashOptions options)
        {
            var writer = new CanonicalSignatureWriter();
            var methods = module.GetTypes().SelectMany(t => t.Methods).OrderBy(MethodKey, StringComparer.Ordinal);
            foreach (MethodDef method in methods)
            {
                WriteMethod(writer, method, options);
            }
            return writer.ToString();
        }

        // Resource callback analysis uses the identical typed signatures and exact IEEE
        // operand encoding; do not maintain a second, weaker display-name-based encoder.
        internal static string MethodSemanticText(MethodDef method)
        {
            var writer = new CanonicalSignatureWriter();
            WriteMethod(writer, method, new SemanticHashOptions());
            foreach (DeclSecurity security in method.DeclSecurities)
                writer.Line("security:" + SecurityText(security));
            return writer.ToString();
        }

        internal static string FieldSemanticText(FieldDef field)
        {
            var writer = new CanonicalSignatureWriter();
            WriteField(writer, field, new SemanticHashOptions());
            return writer.ToString();
        }

        private static void WriteField(CanonicalSignatureWriter writer, FieldDef field, SemanticHashOptions options)
        {
            writer.Line("field:" + FieldKey(field));
            writer.Line(field.Attributes.ToString());
            writer.Line(field.FieldType == null ? string.Empty : SigText(field.FieldType));
            writer.Line("offset:" + (field.FieldOffset.HasValue ? Invariant(field.FieldOffset.Value) : string.Empty));
            if (field.HasConstant)
                writer.Line("constant:" + ConstantText(field.Constant == null ? null : field.Constant.Value));
            if (field.HasMarshalType)
                writer.Line("marshal:" + MarshalText(field.MarshalType));
            if (field.HasFieldRVA)
                writer.Line("rva:" + Convert.ToBase64String(field.InitialValue ?? new byte[0]));
            WriteCustomAttributes(writer, field.CustomAttributes, options);
        }

        private static void WriteMethod(CanonicalSignatureWriter writer, MethodDef method, SemanticHashOptions options)
        {
            ShadowHash.Require(method.NativeBody == null, "UnsupportedNativeMethodBody", method.FullName);
            writer.Line("method:" + MethodKey(method));
            writer.Line(method.Attributes.ToString());
            writer.Line(method.ImplAttributes.ToString());
            if (method.MethodSig != null)
                writer.Line("sig:" + MethodSigText(method.MethodSig));
            foreach (ParamDef parameter in method.ParamDefs)
            {
                writer.Line("param:" + Invariant(parameter.Sequence) + ":" + parameter.Attributes + ":" + Utf8Text(parameter.Name));
                if (parameter.HasMarshalType)
                    writer.Line("marshal:" + MarshalText(parameter.MarshalType));
                if (parameter.HasConstant)
                    writer.Line("constant:" + ConstantText(parameter.Constant == null ? null : parameter.Constant.Value));
                WriteCustomAttributes(writer, parameter.CustomAttributes, options);
            }
            foreach (GenericParam parameter in method.GenericParameters.OrderBy(p => p.Number))
                WriteGenericParameter(writer, parameter, options);
            if (method.ImplMap != null)
                writer.Line("pinvoke:" + PInvokeText(method.ImplMap));
            WriteCustomAttributes(writer, method.CustomAttributes, options);
            foreach (MethodOverride @override in method.Overrides)
                writer.Line("override:" + MethodRefText(@override.MethodBody) + ":" + MethodRefText(@override.MethodDeclaration));
            if (method.HasBody)
                WriteBody(writer, method.Body);
        }

        private static string WriteAttributes(ModuleDef module, SemanticHashOptions options)
        {
            var writer = new CanonicalSignatureWriter();
            WriteCustomAttributes(writer, module.CustomAttributes, options);
            if (module.Assembly != null)
            {
                WriteCustomAttributes(writer, module.Assembly.CustomAttributes, options);
                foreach (DeclSecurity security in module.Assembly.DeclSecurities)
                    writer.Line("security:" + SecurityText(security));
            }
            foreach (TypeDef type in module.GetTypes().OrderBy(AssemblyIdentityUtil.TypeKey, StringComparer.Ordinal))
            {
                writer.Line("type:" + AssemblyIdentityUtil.TypeKey(type));
                WriteCustomAttributes(writer, type.CustomAttributes, options);
                foreach (DeclSecurity security in type.DeclSecurities)
                    writer.Line("security:" + SecurityText(security));
                foreach (FieldDef field in type.Fields.OrderBy(FieldKey, StringComparer.Ordinal))
                    WriteCustomAttributes(writer, field.CustomAttributes, options);
                foreach (MethodDef method in type.Methods.OrderBy(MethodKey, StringComparer.Ordinal))
                {
                    WriteCustomAttributes(writer, method.CustomAttributes, options);
                    foreach (DeclSecurity security in method.DeclSecurities)
                        writer.Line("security:" + SecurityText(security));
                }
                foreach (PropertyDef property in type.Properties.OrderBy(p => p.Name.String, StringComparer.Ordinal))
                {
                    writer.Line("property:" + property.Name);
                    WriteCustomAttributes(writer, property.CustomAttributes, options);
                }
                foreach (EventDef @event in type.Events.OrderBy(e => e.Name.String, StringComparer.Ordinal))
                {
                    writer.Line("event:" + @event.Name);
                    WriteCustomAttributes(writer, @event.CustomAttributes, options);
                }
            }
            return writer.ToString();
        }

        private static void WriteBody(CanonicalSignatureWriter writer, CilBody body)
        {
            writer.Line("body");
            writer.Line("maxstack:" + Invariant(body.MaxStack));
            writer.Line("initlocals:" + body.InitLocals);
            foreach (Local local in body.Variables)
                writer.Line("local:" + Invariant(local.Index) + ":" + SigText(local.Type));
            for (int index = 0; index < body.Instructions.Count; ++index)
            {
                Instruction instruction = body.Instructions[index];
                writer.Line(Invariant(index) + ":" + instruction.OpCode.Code + ":" + OperandText(instruction.Operand, body.Instructions));
            }
            foreach (ExceptionHandler handler in body.ExceptionHandlers)
            {
                writer.Line("eh:" + handler.HandlerType + ":" + Invariant(InstructionIndex(body.Instructions, handler.TryStart)) + ":" + Invariant(InstructionIndex(body.Instructions, handler.TryEnd)) + ":" + Invariant(InstructionIndex(body.Instructions, handler.HandlerStart)) + ":" + Invariant(InstructionIndex(body.Instructions, handler.HandlerEnd)) + ":" + Invariant(InstructionIndex(body.Instructions, handler.FilterStart)) + ":" + (handler.CatchType == null ? string.Empty : TypeRefText(handler.CatchType)));
            }
        }

        private static string OperandText(object operand, IList<Instruction> instructions)
        {
            if (operand == null)
                return string.Empty;
            Instruction target = operand as Instruction;
            if (target != null)
                return "branch:" + Invariant(InstructionIndex(instructions, target));
            Instruction[] targets = operand as Instruction[];
            if (targets != null)
                return "switch:" + string.Join(",", targets.Select(t => Invariant(InstructionIndex(instructions, t))).ToArray());
            IList<Instruction> targetList = operand as IList<Instruction>;
            if (targetList != null)
                return "switch:" + string.Join(",", targetList.Select(t => Invariant(InstructionIndex(instructions, t))).ToArray());
            Local local = operand as Local;
            if (local != null)
                return "local:" + Invariant(local.Index) + ":" + SigText(local.Type);
            Parameter parameter = operand as Parameter;
            if (parameter != null)
                return "parameter:" + Invariant(parameter.Index);
            MethodSpec methodSpec = operand as MethodSpec;
            if (methodSpec != null)
            {
                string args = methodSpec.GenericInstMethodSig == null ? string.Empty : string.Join(",", methodSpec.GenericInstMethodSig.GenericArguments.Select(SigText).ToArray());
                return "methodspec:" + MethodRefText(methodSpec.Method) + ":" + args;
            }
            ITypeDefOrRef type = operand as ITypeDefOrRef;
            if (type != null)
                return "type:" + TypeRefText(type);
            // MemberRef implements both IMethod and IField. Dispatch its actual
            // metadata signature before either interface or a field loses its type.
            var member = operand as MemberRef;
            if (member != null)
            {
                if (member.IsFieldRef) return "field:" + FieldRefText(member);
                if (member.IsMethodRef) return "method:" + MethodRefText(member);
                throw new ShadowBuildException("UnsupportedMemberReference", member.FullName);
            }
            IMethod method = operand as IMethod;
            if (method != null)
                return "method:" + MethodRefText(method);
            IField field = operand as IField;
            if (field != null)
                return "field:" + FieldRefText(field);
            MethodSig callSite = operand as MethodSig;
            if (callSite != null)
                return "callsite:" + MethodSigText(callSite);
            if (operand is string) return "string:" + operand;
            if (operand is sbyte || operand is byte || operand is short || operand is ushort || operand is int || operand is uint ||
                operand is long || operand is ulong || operand is float || operand is double)
                return operand.GetType().FullName + ":" + InvariantObject(operand);
            throw new ShadowBuildException("UnsupportedIlOperand", operand.GetType().FullName);
        }

        private static int InstructionIndex(IList<Instruction> instructions, Instruction instruction)
        {
            return instruction == null ? -1 : instructions.IndexOf(instruction);
        }

        private static void WriteCustomAttributes(CanonicalSignatureWriter writer, IList<CustomAttribute> attributes, SemanticHashOptions options)
        {
            if (attributes == null)
                return;
            foreach (CustomAttribute attribute in attributes
                .Where(a => a != null && !options.IsIgnoredAttribute(a.AttributeType == null ? string.Empty : a.AttributeType.FullName))
                .OrderBy(a => a.AttributeType == null ? string.Empty : a.AttributeType.FullName, StringComparer.Ordinal)
                .ThenBy(AttributeText, StringComparer.Ordinal))
                writer.Line("attribute:" + AttributeText(attribute));
        }

        private static void WriteGenericParameter(CanonicalSignatureWriter writer, GenericParam parameter, SemanticHashOptions options)
        {
            writer.Line("generic:" + Invariant(parameter.Number) + ":" + parameter.Flags + ":" + Utf8Text(parameter.Name));
            WriteCustomAttributes(writer, parameter.CustomAttributes, options);
            foreach (GenericParamConstraint constraint in parameter.GenericParamConstraints)
            {
                writer.Line("constraint:" + TypeRefText(constraint.Constraint));
                WriteCustomAttributes(writer, constraint.CustomAttributes, options);
            }
        }

        private static void WriteAccessorLinks(CanonicalSignatureWriter writer, IEnumerable<MethodDef> methods, string kind)
        {
            if (methods == null)
                return;
            foreach (MethodDef method in methods)
                writer.Line("accessor:" + kind + ":" + MethodKey(method));
        }

        private static string AttributeText(CustomAttribute attribute)
        {
            if (attribute == null)
                return string.Empty;
            var writer = new CanonicalSignatureWriter();
            writer.Token(TypeRefText(attribute.AttributeType));
            if (attribute.Constructor != null)
                writer.Token(MethodSigText(attribute.Constructor.MethodSig));
            if (attribute.IsRawBlob)
                writer.Token("raw:" + Convert.ToBase64String(attribute.RawData ?? new byte[0]));
            foreach (CAArgument argument in attribute.ConstructorArguments)
                writer.Token("arg:" + ArgumentText(argument));
            foreach (CANamedArgument argument in attribute.NamedArguments.OrderBy(a => (a.IsField ? "field:" : "property:") + a.Name.String, StringComparer.Ordinal))
                writer.Token("named:" + (argument.IsField ? "field:" : "property:") + argument.Name.String + ":" + SigText(argument.Type) + ":" + ArgumentText(argument.Argument));
            return writer.ToString();
        }

        private static string ArgumentText(CAArgument argument)
        {
            var writer = new CanonicalSignatureWriter();
            writer.Token(SigText(argument.Type));
            IList<CAArgument> values = argument.Value as IList<CAArgument>;
            if (values != null)
            {
                writer.Token("array");
                writer.Token(Invariant(values.Count));
                foreach (var value in values) writer.Token(ArgumentText(value));
            }
            else
            {
                writer.Token(argument.Value == null ? "null" : "value");
                if (argument.Value != null) writer.Token(InvariantObject(argument.Value));
            }
            return writer.ToString();
        }

        private static string SecurityText(DeclSecurity security)
        {
            var writer = new CanonicalSignatureWriter();
            writer.Token(security.Action.ToString());
            writer.Token(Convert.ToBase64String(security.GetBlob() ?? new byte[0]));
            foreach (SecurityAttribute attribute in security.SecurityAttributes.OrderBy(a => a.TypeFullName, StringComparer.Ordinal))
            {
                writer.Token(TypeRefText(attribute.AttributeType));
                foreach (CANamedArgument argument in attribute.NamedArguments.OrderBy(a => (a.IsField ? "field:" : "property:") + a.Name.String, StringComparer.Ordinal))
                    writer.Token((argument.IsField ? "field:" : "property:") + argument.Name.String + ":" + SigText(argument.Type) + ":" + ArgumentText(argument.Argument));
            }
            return writer.ToString();
        }

        private static string FieldKey(FieldDef field)
        {
            return (field.DeclaringType == null ? string.Empty : AssemblyIdentityUtil.TypeKey(field.DeclaringType)) + ":" + field.Name + ":" + SigText(field.FieldType);
        }

        private static string MethodKey(MethodDef method)
        {
            return (method.DeclaringType == null ? string.Empty : AssemblyIdentityUtil.TypeKey(method.DeclaringType)) + ":" + method.Name + ":" + MethodSigText(method.MethodSig);
        }

        private static string PropertyKey(PropertyDef property)
        {
            return (property.DeclaringType == null ? string.Empty : AssemblyIdentityUtil.TypeKey(property.DeclaringType)) + ":" + property.Name + ":" + (property.PropertySig == null ? string.Empty : property.PropertySig.CallingConvention + ":" + SigText(property.PropertySig.RetType) + ":" + string.Join(",", property.PropertySig.Params.Select(SigText).ToArray()));
        }

        private static string EventKey(EventDef @event)
        {
            return (@event.DeclaringType == null ? string.Empty : AssemblyIdentityUtil.TypeKey(@event.DeclaringType)) + ":" + @event.Name + ":" + TypeRefText(@event.EventType);
        }

        private static string ConstantText(object value)
        {
            if (value == null)
                return "null";
            byte[] bytes = value as byte[];
            if (bytes != null)
                return Convert.ToBase64String(bytes);
            return value.GetType().FullName + ":" + InvariantObject(value);
        }

        private static string TypeRefText(ITypeDefOrRef type)
        {
            var specification = type as TypeSpec;
            if (specification != null) return "typespec:" + SigText(specification.TypeSig);
            return type == null ? string.Empty : (type.AssemblyQualifiedName ?? type.FullName ?? string.Empty);
        }

        private static string SigText(TypeSig type)
        {
            if (type == null) return string.Empty;
            var writer = new CanonicalSignatureWriter();
            writer.Token(type.ElementType.ToString());
            var generic = type as GenericInstSig;
            var variable = type as GenericSig;
            var modifier = type as ModifierSig;
            var array = type as ArraySig;
            var function = type as FnPtrSig;
            var definition = type as TypeDefOrRefSig;
            if (generic != null)
            {
                writer.Token(SigText(generic.GenericType));
                foreach (var argument in generic.GenericArguments) writer.Token(SigText(argument));
            }
            else if (variable != null) writer.Token(Invariant(variable.Number));
            else if (modifier != null) { writer.Token(TypeRefText(modifier.Modifier)); writer.Token(SigText(modifier.Next)); }
            else if (array != null)
            {
                writer.Token(Invariant(array.Rank));
                writer.Token(string.Join(",", array.Sizes.Select(Invariant).ToArray()));
                writer.Token(string.Join(",", array.LowerBounds.Select(Invariant).ToArray()));
                writer.Token(SigText(array.Next));
            }
            else if (function != null)
            {
                var signature = function.Signature as MethodSig;
                ShadowHash.Require(signature != null, "UnsupportedSignature", "Function pointer must have a method signature.");
                writer.Token(MethodSigText(signature));
            }
            else if (definition != null) writer.Token(TypeRefText(definition.TypeDefOrRef));
            else if (type.Next != null) writer.Token(SigText(type.Next));
            else if (type.ElementType != ElementType.Sentinel && type.ElementType != ElementType.End)
                throw new ShadowBuildException("UnsupportedSignature", type.GetType().FullName);
            return writer.ToString();
        }

        private static string MethodSigText(MethodSig signature)
        {
            if (signature == null)
                return string.Empty;
            return signature.CallingConvention + ":" + Invariant(signature.GenParamCount) + ":" + SigText(signature.RetType) + ":" + string.Join(",", signature.Params.Select(SigText).ToArray()) +
                ":varargs:" + string.Join(",", (signature.ParamsAfterSentinel ?? new List<TypeSig>()).Select(SigText).ToArray());
        }

        internal static string MethodRefText(IMethod method)
        {
            if (method == null)
                return string.Empty;
            return TypeRefText(method.DeclaringType) + ":" + Utf8Text(method.Name) + ":" + MethodSigText(method.MethodSig);
        }

        internal static string FieldRefText(IField field)
        {
            return field == null ? string.Empty : TypeRefText(field.DeclaringType) + ":" + Utf8Text(field.Name) + ":" + SigText(field.FieldSig == null ? null : field.FieldSig.Type);
        }

        private static string MarshalText(MarshalType marshal)
        {
            if (marshal == null)
                return string.Empty;
            var raw = marshal as RawMarshalType;
            if (raw != null)
                return "raw:" + Convert.ToBase64String(raw.Data ?? new byte[0]);
            var fixedString = marshal as FixedSysStringMarshalType;
            if (fixedString != null)
                return "fixed-string:" + Invariant(fixedString.Size);
            var safeArray = marshal as SafeArrayMarshalType;
            if (safeArray != null)
                return "safe-array:" + safeArray.VariantType + ":" + TypeRefText(safeArray.UserDefinedSubType);
            var fixedArray = marshal as FixedArrayMarshalType;
            if (fixedArray != null)
                return "fixed-array:" + fixedArray.ElementType + ":" + Invariant(fixedArray.Size);
            var array = marshal as ArrayMarshalType;
            if (array != null)
                return "array:" + array.ElementType + ":" + Invariant(array.ParamNumber) + ":" + Invariant(array.Size) + ":" + Invariant(array.Flags);
            var custom = marshal as CustomMarshalType;
            if (custom != null)
                return "custom:" + Utf8Text(custom.Guid) + ":" + Utf8Text(custom.NativeTypeName) + ":" + TypeRefText(custom.CustomMarshaler) + ":" + Utf8Text(custom.Cookie);
            var interop = marshal as InterfaceMarshalType;
            if (interop != null)
                return "interface:" + interop.NativeType + ":" + Invariant(interop.IidParamIndex);
            return marshal.GetType().FullName + ":" + marshal.NativeType;
        }

        private static string PInvokeText(ImplMap map)
        {
            if (map == null)
                return string.Empty;
            return map.Attributes + ":" + Utf8Text(map.Name) + ":" + (map.Module == null ? string.Empty : Utf8Text(map.Module.Name));
        }

        private static string Invariant(int value) { return value.ToString(System.Globalization.CultureInfo.InvariantCulture); }
        private static string Invariant(uint value) { return value.ToString(System.Globalization.CultureInfo.InvariantCulture); }
        private static string Invariant(ushort value) { return value.ToString(System.Globalization.CultureInfo.InvariantCulture); }
        private static string InvariantObject(object value)
        {
            if (value == null)
                return "null";
            // Mono's default G7/G15 conversion rounds distinct IEEE values, which
            // would hide real IL/constants/attribute changes. Preserve every bit.
            if (value is float)
                return "float32:" + BitConverter.ToInt32(BitConverter.GetBytes((float)value), 0).ToString("x8", System.Globalization.CultureInfo.InvariantCulture);
            if (value is double)
                return "float64:" + BitConverter.DoubleToInt64Bits((double)value).ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
            UTF8String utf8 = value as UTF8String;
            if (!object.ReferenceEquals(utf8, null))
                return "string:" + Utf8Text(utf8);
            if (value is string) return "string:" + (string)value;
            if (value is CAArgument) return "boxed:" + ArgumentText((CAArgument)value);
            ITypeDefOrRef type = value as ITypeDefOrRef;
            if (type != null)
                return "type:" + TypeRefText(type);
            TypeSig signature = value as TypeSig;
            if (signature != null)
                return "type:" + SigText(signature);
            return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
        }

        private static string Utf8Text(UTF8String value) { return object.ReferenceEquals(value, null) ? string.Empty : value.String; }
    }
}
