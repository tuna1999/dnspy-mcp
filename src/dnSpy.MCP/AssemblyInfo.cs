using System.Runtime.CompilerServices;

// Expose internal members to the test project so pure helpers like
// ToolRegistry.ToSnakeCase and IlPatchTools.ToCSharpTypeName can be unit-tested.
[assembly: InternalsVisibleTo("dnSpy.MCP.Tests")]
