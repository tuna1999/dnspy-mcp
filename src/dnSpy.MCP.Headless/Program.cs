using dnSpy.Contracts.Decompiler;
using dnSpy.MCP.Core.Adapters;
using dnSpy.MCP.Core.Mcp;
using dnSpy.MCP.Headless;
using dnSpy.MCP.Headless.Adapters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

// Parse CLI args (fail-fast for --help and bad args)
if (args.Length > 0 && (args[0] == "--help" || args[0] == "-h")) {
    CliOptions.PrintHelp();
    return 0;
}

CliOptions cli;
try {
    cli = CliOptions.Parse(args);
}
catch (ArgumentException ex) {
    Console.Error.WriteLine($"Argument error: {ex.Message}");
    CliOptions.PrintHelp();
    return 2;
}

// Validate pre-load paths exist before starting the server
var preLoadPaths = cli.ExpandLoads();
foreach (var path in preLoadPaths) {
    if (!File.Exists(path)) {
        Console.Error.WriteLine($"Error: --load file not found: {path}");
        return 2;
    }
}

// Fail-fast: validate decompiler DLLs load BEFORE accepting requests
IDecompiler decompiler;
try {
    decompiler = DnSpyDecompilerLoader.LoadCSharp();
}
catch (Exception ex) {
    Console.Error.WriteLine($"Failed to load dnSpy decompiler: {ex.Message}");
    Console.Error.WriteLine("Ensure dnSpy.Decompiler.ILSpy.Core.dll and dependencies are next to the exe (or in deps/).");
    return 3;
}

// Build McpContext eagerly — needed for AutoToolRegistration before DI container builds
var uiScheduler = new InlineUIThreadScheduler();
var loader = new DnlibAssemblyLoader();
foreach (var path in preLoadPaths)
    loader.Load(path);

var ctx = new McpContext(
    assemblyLoader: loader,
    sourceDecompiler: new DnSpyDecompilerSourceProvider(decompiler),
    ui: uiScheduler,
    log: new StderrLogSink(),
    treeRefresh: new NoOpTreeRefreshNotifier());

// Build host with MCP SDK stdio transport
var builder = Host.CreateApplicationBuilder(args);

// CRITICAL: stderr-only logging (stdout is reserved for MCP JSON-RPC frames)
builder.Logging.AddConsole(options => {
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Services.AddSingleton(ctx);

var mcpBuilder = builder.Services.AddMcpServer().WithStdioServerTransport();

// Serialize destructive tool calls (rename_*, update_*, patch_*) so parallel batch
// requests can't race on shared ModuleDef metadata. Mirrors McpServerHost._mutationLock
// in the Extension HTTP transport — both share ToolRegistry.IsMutationTool so the
// prefix list is the single source of truth.
var mutationLock = MutationLockFilter.CreateLock();
var filters = new ModelContextProtocol.Server.McpServerFilters();
// Argument normalization runs before the mutation gate so rewritten keys reach
// the tool regardless of pipeline order.
filters.Request.CallToolFilters.Add((next) => (request, ct) => {
    var normalized = ArgumentNameNormalizer.Normalize(
        request.Params?.Name ?? "", request.Params?.Arguments);
    if (normalized is not null)
        request.Params!.Arguments = normalized;
    return next(request, ct);
});
filters.Request.CallToolFilters.Add(MutationLockFilter.Build(mutationLock));
builder.Services.Configure<ModelContextProtocol.Server.McpServerOptions>(o => o.Filters = filters);

// Rewrite the published input schemas so CLIENT-SIDE validators (which check
// arguments against the tools/list schema before the call is ever sent) accept
// alias spellings:
//   - each snake_case alias is published as an optional property mirroring the
//     declared one
//   - the "required" array is dropped — presence is enforced server-side by the
//     tools themselves, which return actionable messages for missing args.
// Without this, a client validating {member_full_name: ...} against
// required: ["memberFullName"] rejects the call before it reaches the
// server-side ArgumentNameNormalizer.
filters.Request.ListToolsFilters.Add(next => async (request, ct) => {
    var result = await next(request, ct);
    if (result?.Tools is null) return result!;
    foreach (var tool in result.Tools) {
        var aliases = ArgumentNameNormalizer.GetAliases(tool.Name);
        if (tool.InputSchema.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
        if (System.Text.Json.Nodes.JsonNode.Parse(tool.InputSchema.GetRawText())
            is not System.Text.Json.Nodes.JsonObject node) continue;
        if (node["properties"] is not System.Text.Json.Nodes.JsonObject props) continue;
        if (aliases is not null) {
            foreach (var (alias, canonical) in aliases) {
                if (props[canonical]?.DeepClone() is System.Text.Json.Nodes.JsonNode cloned)
                    props[alias] = cloned;
            }
        }
        node.Remove("required");
        tool.InputSchema = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(node);
    }
    return result;
});

AutoToolRegistration.RegisterCoreTools(mcpBuilder, ctx);

try {
    await builder.Build().RunAsync();
    return 0;
}
catch (OperationCanceledException) {
    return 0;
}
catch (Exception ex) {
    Console.Error.WriteLine($"Fatal: {ex}");
    return 1;
}
