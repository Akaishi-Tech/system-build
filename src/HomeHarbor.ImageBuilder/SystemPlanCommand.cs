using System.CommandLine;
using System.Text.Json;
using HomeHarbor.Tooling;

internal static partial class ImageBuilderProgram
{
    private static Command CreateSystemPlanCommand()
    {
        var manifestArgument = RequiredArgument("manifest", "System manifest path.");
        var versionArgument = RequiredArgument("version", "Image version.");
        var repoRootArgument = OptionalArgument("repo-root", Directory.GetCurrentDirectory(), "Repository root.");
        var command = new Command("system-plan", "Print a system image build plan.");
        command.Arguments.Add(manifestArgument);
        command.Arguments.Add(versionArgument);
        command.Arguments.Add(repoRootArgument);
        command.SetAction(parseResult =>
        {
            var systemPlan = SystemImageBuildDescriptor.LoadPlan(
                parseResult.GetValue(manifestArgument)!,
                parseResult.GetValue(repoRootArgument)!,
                parseResult.GetValue(versionArgument)!);
            parseResult.InvocationConfiguration.Output.WriteLine(JsonSerializer.Serialize(systemPlan, JsonOptions));
            return 0;
        });
        return command;
    }
}
