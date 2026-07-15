using System.CommandLine;
using System.Text.Json;
using HomeHarbor.Tooling;

internal static partial class ImageBuilderProgram
{
    private static RootCommand CreateRootCommand()
    {
        var root = new RootCommand("Build HomeHarbor appliance artifacts.");
        root.SetAction(parseResult =>
        {
            var plan = SystemImageBuildDescriptor.LoadDefaultPlan(Directory.GetCurrentDirectory(), "0.1.0-dev");
            parseResult.InvocationConfiguration.Output.WriteLine(JsonSerializer.Serialize(plan, JsonOptions));
            return 0;
        });

        root.Subcommands.Add(CreatePlanCommand());
        root.Subcommands.Add(CreateArchPackageCommand());
        root.Subcommands.Add(CreateSelinuxDependencyKeyCommand());
        root.Subcommands.Add(CreateSelinuxDependencyBuildCommand());
        root.Subcommands.Add(CreateSelinuxDependencyVerifyCommand());
        root.Subcommands.Add(CreateBuildEfiLoaderCommand());
        root.Subcommands.Add(CreateBuildHomeHarborAvbCommand());
        root.Subcommands.Add(CreateBuildHomeHarborInitCommand());
        root.Subcommands.Add(CreateGenerateEfiAvbPublicKeyCommand());
        root.Subcommands.Add(CreateSystemPlanCommand());
        root.Subcommands.Add(CreateSystemBuildCommand());
        root.Subcommands.Add(CreateReleaseBuildCommand());
        root.Subcommands.Add(CreateKernelPackagePlanCommand());
        root.Subcommands.Add(CreateKernelPackageBuildCommand());
        root.Subcommands.Add(CreateSystemAppPayloadCommand());
        return root;
    }
}
