using HomeHarbor.Tooling;

namespace HomeHarbor.Tests;

[TestClass]
public sealed class HomeHarborInitHelperBuildTests
{
    [TestMethod]
    public void CompileArguments_Disables_Emergency_Shell_By_Default()
    {
        var arguments = HomeHarborInitHelperBuild.CompileArguments(
            "/tmp/homeharbor-verity",
            "/repo/boot/init/homeharbor-verity.c",
            ReleaseChannel.Stable,
            debugShellRequested: false);

        Assert.DoesNotContain(HomeHarborInitHelperBuild.EmergencyShellDefineArgument, arguments);
    }

    [TestMethod]
    public void CompileArguments_Enables_Emergency_Shell_For_Explicit_Dev_Debug_Build()
    {
        var arguments = HomeHarborInitHelperBuild.CompileArguments(
            "/tmp/homeharbor-verity",
            "/repo/boot/init/homeharbor-verity.c",
            ReleaseChannel.Dev,
            debugShellRequested: true);

        Assert.Contains(HomeHarborInitHelperBuild.EmergencyShellDefineArgument, arguments);
    }

    [TestMethod]
    public void CompileArguments_Rejects_Emergency_Shell_For_NonDev_Build()
    {
        var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
            HomeHarborInitHelperBuild.CompileArguments(
                "/tmp/homeharbor-verity",
                "/repo/boot/init/homeharbor-verity.c",
                ReleaseChannel.Daily,
                debugShellRequested: true));

        Assert.Contains("only allowed for dev debug builds", ex.Message);
    }
}
