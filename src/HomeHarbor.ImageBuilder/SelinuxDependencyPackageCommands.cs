using System.CommandLine;
using HomeHarbor.Tooling;

internal static partial class ImageBuilderProgram
{
    private static Command CreateSelinuxDependencyKeyCommand()
    {
        var repoRootArgument = OptionalArgument("repo-root", Directory.GetCurrentDirectory(), "Repository root.");
        var command = new Command(
            "selinux-dependency-key",
            "Print the version-independent SELinux dependency package input SHA-256.");
        command.Arguments.Add(repoRootArgument);
        command.SetAction(parseResult =>
        {
            Console.WriteLine(new BuildToolCommands(parseResult.GetValue(repoRootArgument)!)
                .GetSelinuxDependencyInputSha256());
            return 0;
        });
        return command;
    }

    private static Command CreateSelinuxDependencyBuildCommand()
    {
        var outputArgument = RequiredArgument("output", "Dependency package cache output directory.");
        var workArgument = OptionalArgument(
            "work",
            Path.Combine(Directory.GetCurrentDirectory(), ".work", "selinux-dependencies"),
            "Disposable dependency package build directory.");
        var repoRootArgument = OptionalArgument("repo-root", Directory.GetCurrentDirectory(), "Repository root.");
        var command = new Command(
            "selinux-dependency-build",
            "Build and verify the version-independent SELinux dependency package set.");
        command.Arguments.Add(outputArgument);
        command.Arguments.Add(workArgument);
        command.Arguments.Add(repoRootArgument);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            await new BuildToolCommands(parseResult.GetValue(repoRootArgument)!)
                .BuildSelinuxDependencyPackagesAsync(
                    parseResult.GetValue(outputArgument)!,
                    parseResult.GetValue(workArgument)!,
                    cancellationToken);
            return 0;
        });
        return command;
    }

    private static Command CreateSelinuxDependencyVerifyCommand()
    {
        var inputArgument = RequiredArgument("input", "Dependency package cache input directory.");
        var repoRootArgument = OptionalArgument("repo-root", Directory.GetCurrentDirectory(), "Repository root.");
        var command = new Command(
            "selinux-dependency-verify",
            "Verify a cached SELinux dependency package set against current inputs.");
        command.Arguments.Add(inputArgument);
        command.Arguments.Add(repoRootArgument);
        command.SetAction(async (parseResult, cancellationToken) =>
        {
            await new BuildToolCommands(parseResult.GetValue(repoRootArgument)!)
                .VerifySelinuxDependencyPackagesAsync(
                    parseResult.GetValue(inputArgument)!,
                    cancellationToken);
            return 0;
        });
        return command;
    }
}
