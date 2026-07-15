using System.CommandLine;
using HomeHarbor.Tooling;

internal static partial class ImageBuilderProgram
{
    private static Command CreateArchPackageCommand()
    {
        var versionArgument = OptionalArgument("version", "0.1.0-dev", "Package version.");
        var repoRootArgument = OptionalArgument("repo-root", Directory.GetCurrentDirectory(), "Repository root.");
        var command = new Command("arch-package", "Build Arch package artifacts.");
        command.Arguments.Add(versionArgument);
        command.Arguments.Add(repoRootArgument);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            _ = await new BuildToolCommands(parseResult.GetValue(repoRootArgument)!)
                .ArchPackageAsync(parseResult.GetValue(versionArgument)!, cancellationToken);
            return 0;
        });
        return command;
    }
}
