using System.CommandLine;
using HomeHarbor.Tooling;

internal static partial class ImageBuilderProgram
{
    private static Command CreateSystemBuildCommand()
    {
        var manifestArgument = RequiredArgument("manifest", "System manifest path.");
        var versionArgument = RequiredArgument("version", "Image version.");
        var repoRootArgument = OptionalArgument("repo-root", Directory.GetCurrentDirectory(), "Repository root.");
        var command = new Command("system-build", "Build a system image.");
        command.Arguments.Add(manifestArgument);
        command.Arguments.Add(versionArgument);
        command.Arguments.Add(repoRootArgument);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var systemPlan = SystemImageBuildDescriptor.LoadPlan(
                parseResult.GetValue(manifestArgument)!,
                parseResult.GetValue(repoRootArgument)!,
                parseResult.GetValue(versionArgument)!);
            await new SystemImageBuilder(parseResult.GetValue(repoRootArgument)!, parseResult.GetValue(versionArgument)!, systemPlan)
                .BuildAsync(cancellationToken);
            return 0;
        });
        return command;
    }
}
