using System.CommandLine;
using HomeHarbor.Tooling;

internal static partial class ImageBuilderProgram
{
    private static Command CreateKernelPackageBuildCommand()
    {
        var kernelRootArgument = RequiredArgument("kernel-root", "Kernel manifest root.");
        var versionArgument = RequiredArgument("version", "Package version.");
        var kernelChannelArgument = RequiredArgument("kernel-channel", "Kernel channel.");
        AddKernelChannelValidator(kernelChannelArgument);
        var repoRootArgument = OptionalArgument("repo-root", Directory.GetCurrentDirectory(), "Repository root.");
        var command = new Command("kernel-package-build", "Build missing kernel package artifacts.");
        command.Arguments.Add(kernelRootArgument);
        command.Arguments.Add(versionArgument);
        command.Arguments.Add(kernelChannelArgument);
        command.Arguments.Add(repoRootArgument);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            var kernelChannel = KernelChannel.Require(parseResult.GetValue(kernelChannelArgument), "kernel package build channel");
            var kernelPlan = KernelPackageBuildDescriptor.LoadPlan(
                parseResult.GetValue(kernelRootArgument)!,
                parseResult.GetValue(repoRootArgument)!,
                parseResult.GetValue(versionArgument)!);
            var channelPlan = kernelPlan.Channels.Single(channel => channel.Name == kernelChannel);
            await new KernelPackageBuilder(parseResult.GetValue(repoRootArgument)!, parseResult.GetValue(versionArgument)!, channelPlan)
                .BuildMissingAsync(cancellationToken);
            return 0;
        });
        return command;
    }
}
