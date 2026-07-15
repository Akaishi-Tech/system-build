using System.CommandLine;
using System.Text.Json;
using HomeHarbor.Tooling;

internal static partial class ImageBuilderProgram
{
    private static Command CreatePlanCommand()
    {
        var versionArgument = OptionalArgument("version", "0.1.0-dev", "Image version.");
        var command = new Command("plan", "Print the default system image build plan.");
        command.Arguments.Add(versionArgument);
        command.SetAction(parseResult =>
        {
            var version = parseResult.GetValue(versionArgument)!;
            var plan = SystemImageBuildDescriptor.LoadDefaultPlan(Directory.GetCurrentDirectory(), version);
            parseResult.InvocationConfiguration.Output.WriteLine(JsonSerializer.Serialize(plan, JsonOptions));
            return 0;
        });
        return command;
    }
}
