using System.Runtime.CompilerServices;

// Expose internal members to the test project. Currently no Extension internals are
// under test (Extension tool classes moved to Core in Phase 4); this attribute is kept
// so future Extension-only internals (e.g. TreeViewTools helpers) can be tested without
// re-editing AssemblyInfo.
[assembly: InternalsVisibleTo("dnSpy.MCP.Tests")]
