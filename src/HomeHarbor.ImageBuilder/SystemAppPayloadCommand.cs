using System.CommandLine;
using HomeHarbor.Tooling;

internal static partial class ImageBuilderProgram
{
    private static Command CreateSystemAppPayloadCommand()
    {
        var appKeyArgument = RequiredArgument("app-key", "System app key.");
        var rootfsArgument = RequiredArgument("rootfs", "Root filesystem path.");
        var destinationArgument = RequiredArgument("destination", "Payload destination.");
        var packagesArgument = new Argument<string[]>("package")
        {
            Arity = ArgumentArity.OneOrMore,
            Description = "Package names."
        };
        var command = new Command("system-app-payload", "Copy pacman packages into a system app payload.");
        command.Arguments.Add(appKeyArgument);
        command.Arguments.Add(rootfsArgument);
        command.Arguments.Add(destinationArgument);
        command.Arguments.Add(packagesArgument);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            await SystemAppPayloadBuilder.CopyPacmanPackagesAsync(
                parseResult.GetValue(rootfsArgument)!,
                parseResult.GetValue(appKeyArgument)!,
                parseResult.GetValue(destinationArgument)!,
                parseResult.GetValue(packagesArgument) ?? [],
                cancellationToken);
            return 0;
        });
        return command;
    }
}
