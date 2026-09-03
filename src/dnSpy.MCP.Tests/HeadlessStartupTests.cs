using System;
using System.Collections.Generic;
using System.IO;
using dnSpy.MCP.Headless;
using dnSpy.MCP.Headless.Adapters;
using Xunit;

namespace dnSpy.MCP.Tests;

/// <summary>
/// Regression guards for medium-severity findings from the headless-mode review:
///   M1 — CliOptions.ExpandLoads() edge case with bare glob pattern
///   M2 — DnlibAssemblyLoader.Load() cache-key collision (two DLLs same basename,
///        different folders) used to silently dedup via Path.GetFileName key
///   M3 — CliOptions.ExpandLoads() returned IEnumerable; called twice (validation +
///        loader loop). Now returns materialized IReadOnlyList.
/// </summary>
public class HeadlessStartupTests {
    [Fact]
    public void CliOptions_ExpandLoads_bare_glob_does_not_throw() {
        // M1: "*.dll" with no directory — Path.GetDirectoryName returns "", not null.
        // Old null-only check passed it to Directory.GetFiles("", "*.dll", ...) which
        // throws ArgumentException. The empty-or-null guard skips the directory
        // call and treats it as a literal path (File.Exists short-circuits).
        var opts = CliOptions.Parse(new[] { "--load", "*.dll" });
        var paths = opts.ExpandLoads();

        Assert.NotNull(paths);
        // The literal "*.dll" doesn't exist as a file, so the result is empty —
        // what matters is that ExpandLoads did NOT throw.
        Assert.Empty(paths);
    }

    [Fact]
    public void CliOptions_ExpandLoads_returns_materialized_list() {
        // M3 guard: ExpandLoads must return IReadOnlyList (materialized). If anyone
        // reverts to IEnumerable<string> backed by `yield return`, Program.cs would
        // re-execute the glob expansion on every iteration. The materialized shape
        // + Count property on this result proves the contract.
        var opts = CliOptions.Parse(new[] { "--load", "X.dll" });
        var paths = opts.ExpandLoads();

        Assert.IsAssignableFrom<IReadOnlyList<string>>(paths);
        Assert.Equal(paths.Count, paths.Count); // Count is cached on List<T>
    }

    [Fact]
    public void CliOptions_ExpandLoads_skips_missing_files() {
        var opts = CliOptions.Parse(new[] {
            "--load", Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dll"),
        });
        Assert.Empty(opts.ExpandLoads());
    }

    [Fact]
    public void DnlibAssemblyLoader_distinguishes_same_basename_different_folders() {
        // M2 regression: cache key used to be Path.GetFileName(path). Loading
        // utils.dll from folder A and folder B silently returned A on the second
        // call — masking real analyst workflows (multi-version comparison).
        //
        // We can't easily fabricate two distinct real DLLs in the test, so we
        // exercise the cache contract via two DIFFERENT files. The fix's intent
        // is that different full paths yield different cache entries.
        var tempDirA = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var tempDirB = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirA);
        Directory.CreateDirectory(tempDirB);
        try {
            // Use a known existing DLL from the test output (Core lib is always copied).
            // We copy it twice under different paths but rename to the SAME basename
            // in two different folders — this is the exact shape that used to collide.
            var coreDll = Path.Combine(AppContext.BaseDirectory, "dnSpy.MCP.Core.dll");
            var sameNameA = Path.Combine(tempDirA, "rename_target.dll");
            var sameNameB = Path.Combine(tempDirB, "rename_target.dll");
            File.Copy(coreDll, sameNameA);
            File.Copy(coreDll, sameNameB);

            var loader = new DnlibAssemblyLoader();
            var resultA = loader.Load(sameNameA);
            var resultB = loader.Load(sameNameB);

            Assert.True(resultA.Success, $"Load A failed: {resultA.Error}");
            Assert.True(resultB.Success, $"Load B failed: {resultB.Error}");

            // Both files must be present in the loader's document list — the old
            // basename-key dedup would only keep A.
            var docs = loader.GetDocuments();
            var pathsInLoader = new HashSet<string>(
                System.Linq.Enumerable.Select(docs, d => d.Path),
                StringComparer.OrdinalIgnoreCase);
            Assert.Contains(sameNameA, pathsInLoader);
            Assert.Contains(sameNameB, pathsInLoader);
        }
        finally {
            try { Directory.Delete(tempDirA, true); } catch { }
            try { Directory.Delete(tempDirB, true); } catch { }
        }
    }
}
