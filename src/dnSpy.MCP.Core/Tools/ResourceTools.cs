using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection.PortableExecutable;
using System.Text;
using dnlib.DotNet;
using dnSpy.MCP.Core.Mcp;

namespace dnSpy.MCP.Core.Tools {
    public sealed class ResourceTools {
        private readonly McpContext _ctx;
        public ResourceTools(McpContext ctx) => _ctx = ctx;

        [Description("List all embedded resources in the currently loaded assembly.")]
        public string GetResources() {
            if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
                return "Error: No assemblies loaded.";

            var sb = new StringBuilder();
            var count = 0;

            foreach (var loaded in _ctx.AssemblyLoader.GetDocuments()) {
                if (loaded.Module is ModuleDef mod) {
                    sb.AppendLine($"Module: {mod.Name}");
                    foreach (var resource in mod.Resources) {
                        count++;
                        sb.AppendLine($"  {resource.Name}");
                        sb.AppendLine($"    Type: {resource.ResourceType}, Offset: {resource.Offset}");
                        if (resource is EmbeddedResource er)
                            sb.AppendLine($"    Size: {er.Length} bytes");
                    }
                }
            }

            return count == 0 ? "No resources found." : $"Resources ({count}):\n\n{sb}";
        }

        [Description("Get raw data of a specific embedded resource by name. maxLength caps how many bytes are dumped (default 512).")]
        public string GetResourceData(string resourceName, int maxLength = 512) {
            if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
                return "Error: No assemblies loaded.";

            // Clamp to a sane range so callers can't request gigabytes of hex.
            var cap = Math.Max(0, Math.Min(maxLength, dataDumpHardCap));

            foreach (var loaded in _ctx.AssemblyLoader.GetDocuments()) {
                if (loaded.Module is ModuleDef mod) {
                    foreach (var resource in mod.Resources) {
                        if (resource.Name == resourceName && resource is EmbeddedResource er) {
                            var data = er.CreateReader().ToArray();
                            var shown = Math.Min(cap, data.Length);

                            var sb = new StringBuilder();
                            sb.AppendLine($"Resource: {resourceName}");
                            sb.AppendLine($"Size: {data.Length} bytes");
                            sb.AppendLine($"Hex: {BitConverter.ToString(data.Take(shown).ToArray())}");
                            if (data.Length > shown) sb.AppendLine($"... ({data.Length - shown} more bytes)");
                            return sb.ToString();
                        }
                    }
                }
            }

            return $"Resource not found: {resourceName}";
        }

        const int dataDumpHardCap = 4096;

        [Description("Get PE and metadata information: headers, metadata version, strong name, assembly attributes.")]
        public string GetMetadata(string? assembly = null) {
            if (_ctx.AssemblyLoader.GetDocuments().Count == 0)
                return "Error: No assemblies loaded.";

            foreach (var loaded in _ctx.AssemblyLoader.GetDocuments()) {
                if (loaded.Module is not ModuleDef mod) continue;

                if (!string.IsNullOrEmpty(assembly) &&
                    !string.Equals(mod.Assembly?.Name?.String, assembly, StringComparison.OrdinalIgnoreCase))
                    continue;

                var sb = new StringBuilder();
                sb.AppendLine($"Module: {mod.Name}");
                sb.AppendLine($"MVID: {mod.Mvid}");
                sb.AppendLine($"Runtime: {mod.RuntimeVersion}");

                var asm = mod.Assembly;
                if (asm != null) {
                    sb.AppendLine($"Assembly: {asm.Name}");
                    sb.AppendLine($"Version: {asm.Version}");
                    sb.AppendLine($"Culture: {asm.Culture}");
                }

                if (mod.EntryPoint != null)
                    sb.AppendLine($"EntryPoint: {mod.EntryPoint.DeclaringType?.FullName}::{mod.EntryPoint.Name}");

                AppendPeHeaders(sb, loaded.Path);

                return sb.ToString();
            }

            return "No assembly loaded.";
        }

        /// <summary>
        /// Reads PE headers from the on-disk file via <see cref="PEReader"/>. The previous
        /// Extension implementation read them from dnSpy's <c>IDsDocument.PEImage</c>, which is
        /// not exposed by <see cref="LoadedModule"/>. For in-memory modules <paramref name="path"/>
        /// is empty and we skip PE header output rather than crash on <see cref="File.OpenRead"/>.
        /// </summary>
        private static void AppendPeHeaders(StringBuilder sb, string path) {
            if (string.IsNullOrEmpty(path)) {
                sb.AppendLine("PE Headers: (in-memory module, no on-disk image)");
                return;
            }

            try {
                using var fs = File.OpenRead(path);
                using var peReader = new PEReader(fs);
                var headers = peReader.PEHeaders;

                sb.AppendLine($"Machine: {headers.CoffHeader?.Machine}");
                if (headers.SectionHeaders.Length > 0) {
                    sb.AppendLine("Sections:");
                    foreach (var s in headers.SectionHeaders)
                        sb.AppendLine($"  {s.Name}: VirtSize={s.VirtualSize}, RawSize={s.SizeOfRawData}");
                }
            }
            catch (Exception ex) {
                sb.AppendLine($"PE Headers: (failed to read '{path}': {ex.Message})");
            }
        }
    }
}
