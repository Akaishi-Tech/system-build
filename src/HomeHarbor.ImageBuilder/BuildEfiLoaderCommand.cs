using System.CommandLine;
using HomeHarbor.Tooling;

internal static partial class ImageBuilderProgram
{
    private static Command CreateBuildEfiLoaderCommand()
    {
        var outputArgument = RequiredArgument("output", "Output binary path.");
        var repoRootArgument = OptionalArgument("repo-root", Directory.GetCurrentDirectory(), "Repository root.");
        var command = new Command("build-efi-loader", "Build the EFI loader.");
        command.Arguments.Add(outputArgument);
        command.Arguments.Add(repoRootArgument);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            await new BuildToolCommands(parseResult.GetValue(repoRootArgument)!)
                .BuildEfiLoaderAsync(parseResult.GetValue(outputArgument)!, cancellationToken);
            return 0;
        });
        return command;
    }
}
