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
foreach (var path in cli.ExpandLoads()) {
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
foreach (var path in cli.ExpandLoads())
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
AutoToolRegistration.RegisterAll(mcpBuilder, ctx);

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
