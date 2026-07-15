using HomeHarbor.Tooling;

namespace HomeHarbor.Tests;

[TestClass]
public sealed class RootlessBuildExecutorTests
{
    [TestMethod]
    public void ManagedBuildPath_Rejects_Destructive_Overrides_And_Symlink_Parents()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "homeharbor-build-path-" + Guid.NewGuid().ToString("N"));
        var workRoot = Path.Combine(tempDir, ".work");
        var outside = Path.Combine(tempDir, "outside");
        try
        {
            _ = Directory.CreateDirectory(workRoot);
            _ = Directory.CreateDirectory(outside);
            Assert.AreEqual(
                Path.Combine(workRoot, "packages"),
                BuildToolCommands.RequireManagedBuildPath(Path.Combine(workRoot, "packages"), "work", workRoot));
            _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
                BuildToolCommands.RequireManagedBuildPath(tempDir, "work", workRoot));
            _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
                BuildToolCommands.RequireManagedBuildPath(workRoot, "work", workRoot));

            var link = Path.Combine(workRoot, "linked");
            _ = Directory.CreateSymbolicLink(link, outside);
            _ = Assert.ThrowsExactly<InvalidOperationException>(() =>
                BuildToolCommands.RequireManagedBuildPath(Path.Combine(link, "packages"), "work", workRoot));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [TestMethod]
    public void MappedRootArguments_Leaves_Mount_Propagation_Unchanged()
    {
        var arguments = RootlessBuildExecutor.MappedRootArguments("id", ["-u"]);

        CollectionAssert.AreEqual(
            new[]
            {
                "--fork",
                "--pid",
                "--mount",
                "--propagation",
                "unchanged",
                "--setgroups",
                "allow",
                "--map-auto",
                "--map-root-user",
                "--setuid",
                "0",
                "--setgid",
                "0",
                "id",
                "-u"
            },
            arguments.ToArray());
    }
}
