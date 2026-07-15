using System.CommandLine;
using HomeHarbor.Tooling;

internal static partial class ImageBuilderProgram
{
    private static Command CreateReleaseBuildCommand()
    {
        var manifestArgument = RequiredArgument("manifest", "System manifest path.");
        var versionArgument = RequiredArgument("version", "Release version.");
        var repoRootArgument = OptionalArgument("repo-root", Directory.GetCurrentDirectory(), "Repository root.");
        var command = new Command("release-build", "Build system artifacts, OTA bundles, channel metadata, and the full live installer ISO.");
        command.Arguments.Add(manifestArgument);
        command.Arguments.Add(versionArgument);
        command.Arguments.Add(repoRootArgument);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var repoRoot = parseResult.GetValue(repoRootArgument)!;
            var version = parseResult.GetValue(versionArgument)!;
            var systemPlan = SystemImageBuildDescriptor.LoadPlan(
                parseResult.GetValue(manifestArgument)!,
                repoRoot,
                version);
            await new SystemImageBuilder(repoRoot, version, systemPlan).BuildAsync(cancellationToken);
            _ = await new ReleaseArtifactBuilder(repoRoot, version, systemPlan).BuildAsync(cancellationToken);
            return 0;
        });
        return command;
    }
}
