using System;
using System.ComponentModel;
using System.Linq;
using System.Text;
using dnlib.DotNet;

namespace dnSpy.MCP.Tools {
    public static class TypeInspectorTools {
        [Description("List all members of a type (fields, properties, methods, events). Optionally filter by member type.")]
        public static string GetTypeMembers(
            [Description("Full type name, e.g. 'Namespace.ClassName'")] string typeFullName,
            [Description("Filter: 'fields', 'properties', 'methods', 'events', or 'all'")] string memberType = "all") {

            if (DnSpyContext.DocumentService == null)
                return "Error: DocumentService not available.";

            var type = DnSpyContext.Resolver.ResolveType(typeFullName);
            if (type == null)
                return $"Type not found: {typeFullName}";

            var sb = new StringBuilder();
            sb.AppendLine($"Members of {type.FullName}");
            sb.AppendLine($"Token: 0x{type.MDToken.Raw:X8}");
            sb.AppendLine();

            if (memberType == "all" || memberType == "fields") {
                sb.AppendLine($"Fields ({type.Fields.Count}):");
                foreach (var f in type.Fields) {
                    var access = FieldAccessStr(f);
                    sb.AppendLine($"  {access,-18} {f.FieldType?.FullName ?? "?"} {f.Name}  [0x{f.MDToken.Raw:X8}]");
                }
                sb.AppendLine();
            }

            if (memberType == "all" || memberType == "properties") {
                sb.AppendLine($"Properties ({type.Properties.Count}):");
                foreach (var p in type.Properties) {
                    var access = PropAccessStr(p);
                    sb.AppendLine($"  {access,-18} {p.PropertySig?.RetType?.FullName ?? "?"} {p.Name}  [0x{p.MDToken.Raw:X8}]");
                }
                sb.AppendLine();
            }

            if (memberType == "all" || memberType == "methods") {
                sb.AppendLine($"Methods ({type.Methods.Count}):");
                foreach (var m in type.Methods) {
                    var staticStr = m.IsStatic ? "static " : "";
                    sb.AppendLine($"  {MethodAccessStr(m),-18} {staticStr}{m.ReturnType?.FullName ?? "void"} {m.Name}({FormatParams(m)})  [0x{m.MDToken.Raw:X8}]");
                }
                sb.AppendLine();
            }

            if (memberType == "all" || memberType == "events") {
                sb.AppendLine($"Events ({type.Events.Count}):");
                foreach (var e in type.Events)
                    sb.AppendLine($"  {e.EventType?.FullName ?? "?"} {e.Name}  [0x{e.MDToken.Raw:X8}]");
                sb.AppendLine();
            }

            return sb.ToString();
        }

        [Description("Get detailed field info: type, accessibility, static/const, default values.")]
        public static string GetFields(
            [Description("Full type name")] string typeFullName,
            [Description("Optional name filter (substring match)")] string? nameFilter = null) {

            if (DnSpyContext.DocumentService == null)
                return "Error: DocumentService not available.";

            var type = DnSpyContext.Resolver.ResolveType(typeFullName);
            if (type == null)
                return $"Type not found: {typeFullName}";

            var fields = type.Fields.AsEnumerable();
            if (!string.IsNullOrEmpty(nameFilter))
                fields = fields.Where(f => f.Name.String.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0);

            var fieldList = fields.ToList();
            if (fieldList.Count == 0)
                return string.IsNullOrEmpty(nameFilter)
                    ? $"No fields in '{type.FullName}'."
                    : $"No fields matching '{nameFilter}' in '{type.FullName}'.";

            var sb = new StringBuilder();
            sb.AppendLine($"Fields of {type.FullName} ({fieldList.Count}):\n");

            foreach (var f in fieldList) {
                sb.AppendLine($"  {f.Name}  [0x{f.MDToken.Raw:X8}]");
                sb.AppendLine($"    Type:     {f.FieldType?.FullName ?? "?"}");
                sb.AppendLine($"    Access:   {FieldAccessStr(f)}");
                sb.AppendLine($"    Static:   {f.IsStatic}");
                sb.AppendLine($"    Literal:  {f.IsLiteral}");

                if (f.IsLiteral && f.Constant != null)
                    sb.AppendLine($"    Value:    {FormatConstant(f.Constant.Value)}");

                sb.AppendLine();
            }

            return sb.ToString();
        }

        [Description("Get detailed property info: getter/setter signatures, property type, accessibility.")]
        public static string GetProperties(
            [Description("Full type name")] string typeFullName,
            [Description("Optional name filter (substring match)")] string? nameFilter = null) {

            if (DnSpyContext.DocumentService == null)
                return "Error: DocumentService not available.";

            var type = DnSpyContext.Resolver.ResolveType(typeFullName);
            if (type == null)
                return $"Type not found: {typeFullName}";

            var props = type.Properties.AsEnumerable();
            if (!string.IsNullOrEmpty(nameFilter))
                props = props.Where(p => p.Name.String.IndexOf(nameFilter, StringComparison.OrdinalIgnoreCase) >= 0);

            var propList = props.ToList();
            if (propList.Count == 0)
                return string.IsNullOrEmpty(nameFilter)
                    ? $"No properties in '{type.FullName}'."
                    : $"No properties matching '{nameFilter}' in '{type.FullName}'.";

            var sb = new StringBuilder();
            sb.AppendLine($"Properties of {type.FullName} ({propList.Count}):\n");

            foreach (var p in propList) {
                sb.AppendLine($"  {p.Name}  [0x{p.MDToken.Raw:X8}]");
                sb.AppendLine($"    Type:   {p.PropertySig?.RetType?.FullName ?? "?"}");
                sb.AppendLine($"    Access: {PropAccessStr(p)}");

                if (p.GetMethod != null) {
                    var gm = p.GetMethod;
                    sb.AppendLine($"    Getter: {MethodAccessStr(gm)} {(gm.IsStatic ? "static " : "")}{gm.ReturnType?.FullName ?? "void"} {gm.Name}()");
                }

                if (p.SetMethod != null) {
                    var sm = p.SetMethod;
                    sb.AppendLine($"    Setter: {MethodAccessStr(sm)} {(sm.IsStatic ? "static " : "")}void {sm.Name}({p.PropertySig?.RetType?.FullName ?? "?"} value)");
                }

                if (p.GetMethod == null && p.SetMethod == null)
                    sb.AppendLine("    (no getter/setter)");

                sb.AppendLine();
            }

            return sb.ToString();
        }

        // --- Helpers ---

        private static string FieldAccessStr(FieldDef f) {
            if (f.IsPublic) return "public";
            if (f.IsPrivate) return "private";
            if (f.IsFamily) return "protected";
            if (f.IsAssembly) return "internal";
            if (f.IsFamilyOrAssembly) return "protected internal";
            if (f.IsFamilyAndAssembly) return "private protected";
            return f.Access.ToString();
        }

        private static string MethodAccessStr(MethodDef m) {
            if (m.IsPublic) return "public";
            if (m.IsPrivate) return "private";
            if (m.IsFamily) return "protected";
            if (m.IsAssembly) return "internal";
            if (m.IsFamilyOrAssembly) return "protected internal";
            if (m.IsFamilyAndAssembly) return "private protected";
            return m.Access.ToString();
        }

        private static string PropAccessStr(PropertyDef p) {
            if (p.GetMethod != null) return MethodAccessStr(p.GetMethod);
            if (p.SetMethod != null) return MethodAccessStr(p.SetMethod);
            return "unknown";
        }

        private static string FormatParams(MethodDef m) {
            var ps = m.Parameters.Where(p => !p.IsHiddenThisParameter);
            return string.Join(", ", ps.Select(p => $"{p.Type?.FullName ?? "?"} {p.Name ?? $"arg{p.Index}"}"));
        }

        private static string FormatConstant(object? value) {
            if (value == null) return "null";
            if (value is byte[] bytes)
                return $"{bytes.Length} bytes: {BitConverter.ToString(bytes, 0, Math.Min(bytes.Length, 32))}{(bytes.Length > 32 ? "..." : "")}";
            return value.ToString() ?? "null";
        }
    }
}
