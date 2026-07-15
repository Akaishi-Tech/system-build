namespace HomeHarbor.Tooling;

public static class HomeHarborInitHelperBuild
{
    public const string DebugShellEnvironmentVariable = "HOMEHARBOR_DEBUG_INITRAMFS_SHELL";
    public const string EmergencyShellDefineArgument = "-DHOMEHARBOR_ENABLE_INITRAMFS_EMERGENCY_SHELL=1";

    public static IReadOnlyList<string> CompileArguments(string output, string source)
        => CompileArguments(
            output,
            source,
            Env.String("HOMEHARBOR_CHANNEL", ReleaseChannel.Dev),
            Env.Flag(DebugShellEnvironmentVariable));

    public static IReadOnlyList<string> CompileArguments(
        string output,
        string source,
        string channel,
        bool debugShellRequested)
    {
        var arguments = new List<string>
        {
            "-O2",
            "-Wall",
            "-Wextra"
        };

        if (DebugShellEnabled(channel, debugShellRequested))
        {
            arguments.Add(EmergencyShellDefineArgument);
        }

        arguments.AddRange(["-o", output, source]);
        return arguments;
    }

    private static bool DebugShellEnabled(string channel, bool requested)
    {
        if (!requested)
        {
            return false;
        }

        var normalizedChannel = ReleaseChannel.Require(channel, "HOMEHARBOR_CHANNEL");
        if (normalizedChannel != ReleaseChannel.Dev)
        {
            throw new InvalidOperationException(
                $"Refusing to enable initramfs emergency shell for {normalizedChannel} builds; " +
                $"{DebugShellEnvironmentVariable}=1 is only allowed for dev debug builds.");
        }

        return true;
    }
}
