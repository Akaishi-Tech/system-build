using System.CommandLine;
using HomeHarbor.Tooling;

internal static partial class ImageBuilderProgram
{
    private static Command CreateGenerateEfiAvbPublicKeyCommand()
    {
        var outputArgument = RequiredArgument("output-header", "Output header path.");
        var repoRootArgument = OptionalArgument("repo-root", Directory.GetCurrentDirectory(), "Repository root.");
        var command = new Command("generate-efi-avb-public-key", "Generate the EFI AVB public key header.");
        command.Arguments.Add(outputArgument);
        command.Arguments.Add(repoRootArgument);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            await new BuildToolCommands(parseResult.GetValue(repoRootArgument)!)
                .GenerateEfiAvbPublicKeyHeaderAsync(parseResult.GetValue(outputArgument)!, cancellationToken);
            return 0;
        });
        return command;
    }
}
