namespace HomeHarbor.Tooling;

internal interface IErofsImageTool
{
    Task BuildAsync(
        string output,
        string source,
        string? fileContexts,
        string mountPoint,
        IEnumerable<string> options,
        CancellationToken cancellationToken = default);
}

internal static class ErofsImageTool
{
    public static async Task<IErofsImageTool> CreateAsync(
        SystemImageSecurityPlan security,
        string packageDirectory,
        string toolRoot,
        ICommandRunner runner,
        CancellationToken cancellationToken = default)
    {
        if (security.Selinux)
        {
            return new SelinuxAdapter(
                await SelinuxErofsTool.CreateAsync(
                    packageDirectory,
                    toolRoot,
                    runner,
                    cancellationToken));
        }

        var probe = await runner.RunAsync(
            "mkfs.erofs",
            ["--help"],
            new CommandRunOptions(ThrowOnStartFailure: false),
            cancellationToken);
        _ = probe.EnsureSuccess("mkfs.erofs is required for the no-SELinux image profile");
        return new StandardErofsImageTool(runner);
    }

    private sealed class SelinuxAdapter(SelinuxErofsTool tool) : IErofsImageTool
    {
        public async Task BuildAsync(
            string output,
            string source,
            string? fileContexts,
            string mountPoint,
            IEnumerable<string> options,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(fileContexts))
            {
                throw new InvalidOperationException("SELinux EROFS images require file contexts");
            }

            await tool.BuildAsync(
                output,
                source,
                fileContexts,
                mountPoint,
                options,
                cancellationToken);
        }
    }
}

internal sealed class StandardErofsImageTool(ICommandRunner runner) : IErofsImageTool
{
    private readonly RootlessBuildExecutor _rootless = new(runner);

    public async Task BuildAsync(
        string output,
        string source,
        string? fileContexts,
        string mountPoint,
        IEnumerable<string> options,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(fileContexts))
        {
            throw new InvalidOperationException("the no-SELinux EROFS profile must not receive file contexts");
        }

        if (string.IsNullOrWhiteSpace(mountPoint) || mountPoint[0] != '/')
        {
            throw new InvalidOperationException("EROFS mount point must be an absolute image path");
        }

        var result = await _rootless.RunMappedRootAsync(
            "mkfs.erofs",
            [.. options, output, source],
            new CommandRunOptions(StreamOutput: true, StreamError: true),
            cancellationToken);
        _ = result.EnsureSuccess("mkfs.erofs failed");
    }
}
