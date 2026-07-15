using System.CommandLine;
using HomeHarbor.Tooling;

internal static partial class ImageBuilderProgram
{
    private static Command CreateBuildHomeHarborInitCommand()
    {
        var outputArgument = RequiredArgument("output", "Output binary path.");
        var repoRootArgument = OptionalArgument("repo-root", Directory.GetCurrentDirectory(), "Repository root.");
        var command = new Command("build-homeharbor-init", "Build the HomeHarbor initramfs helper.");
        command.Arguments.Add(outputArgument);
        command.Arguments.Add(repoRootArgument);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            await new BuildToolCommands(parseResult.GetValue(repoRootArgument)!)
                .BuildHomeHarborInitAsync(parseResult.GetValue(outputArgument)!, cancellationToken);
            return 0;
        });
        return command;
    }
}
