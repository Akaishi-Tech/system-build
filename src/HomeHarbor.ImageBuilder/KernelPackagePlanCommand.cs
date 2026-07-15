using System.CommandLine;
using System.Text.Json;
using HomeHarbor.Tooling;

internal static partial class ImageBuilderProgram
{
    private static Command CreateKernelPackagePlanCommand()
    {
        var kernelRootArgument = RequiredArgument("kernel-root", "Kernel manifest root.");
        var versionArgument = RequiredArgument("version", "Package version.");
        var repoRootArgument = OptionalArgument("repo-root", Directory.GetCurrentDirectory(), "Repository root.");
        var command = new Command("kernel-package-plan", "Print the kernel package build plan.");
        command.Arguments.Add(kernelRootArgument);
        command.Arguments.Add(versionArgument);
        command.Arguments.Add(repoRootArgument);
        command.SetAction(parseResult =>
        {
            var kernelPlan = KernelPackageBuildDescriptor.LoadPlan(
                parseResult.GetValue(kernelRootArgument)!,
                parseResult.GetValue(repoRootArgument)!,
                parseResult.GetValue(versionArgument)!);
            parseResult.InvocationConfiguration.Output.WriteLine(JsonSerializer.Serialize(kernelPlan, JsonOptions));
            return 0;
        });
        return command;
    }
}
