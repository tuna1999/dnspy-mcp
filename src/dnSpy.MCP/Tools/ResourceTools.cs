using System;
using System.ComponentModel;
using System.Linq;
using System.Text;
using dnlib.DotNet;
using dnSpy.Contracts.Documents;
using System.ComponentModel;

namespace dnSpy.MCP.Tools {
    public static class ResourceTools {
        [Description("List all embedded resources in the currently loaded assembly.")]
        public static string GetResources() {
            var documentService = DnSpyContext.DocumentService;
            if (documentService == null) return "Error: dnSpy services not available.";

            var sb = new StringBuilder();
            var count = 0;

            foreach (var doc in documentService.GetDocuments()) {
                if (doc.ModuleDef is ModuleDef mod) {
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

        [Description("Get raw data of a specific embedded resource by name.")]
        public static string GetResourceData(string resourceName, int maxLength = 512) {
            var documentService = DnSpyContext.DocumentService;
            if (documentService == null) return "Error: dnSpy services not available.";

            foreach (var doc in documentService.GetDocuments()) {
                if (doc.ModuleDef is ModuleDef mod) {
                    foreach (var resource in mod.Resources) {
                        if (resource.Name == resourceName && resource is EmbeddedResource er) {
                            var data = er.CreateReader().ToArray();

                            var sb = new StringBuilder();
                            sb.AppendLine($"Resource: {resourceName}");
                            sb.AppendLine($"Size: {data.Length} bytes");
                            sb.AppendLine($"Hex: {BitConverter.ToString(data.Take(Math.Min(64, data.Length)).ToArray())}");
                            if (data.Length > 64) sb.AppendLine($"... ({data.Length - 64} more bytes)");
                            return sb.ToString();
                        }
                    }
                }
            }

            return $"Resource not found: {resourceName}";
        }

        [Description("Get PE and metadata information: headers, metadata version, strong name, assembly attributes.")]
        public static string GetMetadata() {
            var documentService = DnSpyContext.DocumentService;
            if (documentService == null) return "Error: dnSpy services not available.";

            foreach (var doc in documentService.GetDocuments()) {
                if (doc.ModuleDef is ModuleDef mod) {
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

                    if (doc.PEImage != null) {
                        var pe = doc.PEImage;
                        sb.AppendLine($"Machine: {pe.ImageNTHeaders?.FileHeader.Machine}");
                        if (pe.ImageSectionHeaders != null) {
                            sb.AppendLine("Sections:");
                            foreach (var s in pe.ImageSectionHeaders)
                                sb.AppendLine($"  {s.DisplayName}: VirtSize={s.VirtualSize}, RawSize={s.SizeOfRawData}");
                        }
                    }

                    return sb.ToString();
                }
            }

            return "No assembly loaded.";
        }
    }
}
