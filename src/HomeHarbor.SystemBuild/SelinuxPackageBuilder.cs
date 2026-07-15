namespace HomeHarbor.Tooling;

public sealed class SelinuxPackageBuilder(
    string root,
    ICommandRunner? runner = null)
{
    private const string BuildUser = "builder";
    private const int BuildUserId = 1000;
    internal const int MakepkgMaximumAttempts = 3;
    internal const string CleanBuildPath =
        "/usr/bin/site_perl:/usr/bin/vendor_perl:/usr/bin/core_perl:" +
        "/usr/local/sbin:/usr/local/bin:/usr/bin:/bin";
    internal const string PrivateBuildMountScript = """
        umount /proc
        mount -t proc -o nosuid,nodev,noexec proc /proc
        mkdir -p /dev/pts
        mount -t devpts -o nosuid,noexec,newinstance,ptmxmode=0666,mode=0620,gid=5 devpts /dev/pts
        ln -sfn pts/ptmx /dev/ptmx
        mkdir -p /dev/shm
        mount -t tmpfs -o mode=1777,nosuid,nodev tmpfs /dev/shm
        exec "$@"
        """;

    private readonly string _root = Path.GetFullPath(root);
    private readonly ICommandRunner _runner = runner ?? new ProcessCommandRunner();
    private readonly RootlessBuildExecutor _rootless = new(runner ?? new ProcessCommandRunner());

    public async Task BuildAsync(
        SelinuxPackageBuildPlan plan,
        string workDirectory,
        string packageOutput,
        CancellationToken cancellationToken = default)
    {
        await _rootless.RequireReadyAsync(cancellationToken);
        await RequireToolsAsync(["bsdtar", "makepkg", "pacman", "repo-add"], cancellationToken);

        var work = Path.GetFullPath(workDirectory);
        var output = Path.GetFullPath(packageOutput);
        var buildRoot = Path.Combine(work, "root");
        await DeleteMappedDirectoryAsync(work, cancellationToken);
        _ = Directory.CreateDirectory(buildRoot);
        _ = Directory.CreateDirectory(output);
        PrepareBuildInputs(plan, buildRoot);

        var bootstrapConfig = Path.Combine(work, "bootstrap-pacman.conf");
        await ArchLocalPackageRepositoryBuilder.WriteBootstrapPacmanConfigAsync(bootstrapConfig, cancellationToken);
        var success = false;
        try
        {
            var bootstrap = await _rootless.RunPacstrapAsync(
                buildRoot,
                ["base-devel", "git"],
                new CommandRunOptions(StreamOutput: true, StreamError: true, Timeout: TimeSpan.FromMinutes(30)),
                bootstrapConfig,
                cancellationToken);
            _ = bootstrap.EnsureSuccess("could not create the disposable SELinux package build root");

            await PrepareBuildRootAsync(plan, buildRoot, bootstrapConfig, cancellationToken);
            var sourceInfo = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var recipe in plan.Recipes.Values.OrderBy(recipe => recipe.Name, StringComparer.Ordinal))
            {
                var info = await RunMakepkgAsBuilderAsync(
                    buildRoot,
                    recipe,
                    ["--printsrcinfo"],
                    captureOutput: true,
                    retryTransientGitFetchFailures: false,
                    cancellationToken);
                ValidateDeclaredPackages(recipe, info);
                sourceInfo.Add(recipe.Name, info);
            }

            var localCapabilities = LocalCapabilities(plan, sourceInfo.Values);
            foreach (var recipeName in plan.BuildOrder)
            {
                var recipe = plan.Recipes[recipeName];
                var dependencies = ParseDependencies(sourceInfo[recipeName])
                    .Where(dependency => !localCapabilities.Contains(dependency))
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                if (dependencies.Length > 0)
                {
                    await RunChrootRequiredAsync(
                        buildRoot,
                        "pacman",
                        ["--noconfirm", "-S", "--needed", .. dependencies],
                        cancellationToken,
                        timeout: TimeSpan.FromMinutes(30));
                }

                var makepkgArguments = new List<string>
                {
                    "--force",
                    "--cleanbuild",
                    "--clean",
                    "--log",
                    "--noconfirm",
                    "--nodeps"
                };
                if (recipe.SkipCheck)
                {
                    makepkgArguments.Add("--nocheck");
                }

                _ = await RunMakepkgAsBuilderAsync(
                    buildRoot,
                    recipe,
                    makepkgArguments,
                    captureOutput: false,
                    retryTransientGitFetchFailures: true,
                    cancellationToken);
                var archives = await RequireRecipePackagesAsync(buildRoot, recipe, cancellationToken);
                foreach (var archive in archives.Values)
                {
                    File.Copy(archive, Path.Combine(output, Path.GetFileName(archive)), overwrite: true);
                }

                if (recipe.InstallPackages.Count > 0)
                {
                    var installArchives = recipe.InstallPackages
                        .Select(package => "/packages/" + Path.GetFileName(archives[package]))
                        .ToArray();
                    await RunChrootRequiredAsync(
                        buildRoot,
                        "pacman",
                        ["--noconfirm", "--ask=4", "-U", .. installArchives],
                        cancellationToken,
                        timeout: TimeSpan.FromMinutes(30));
                }
            }

            success = true;
        }
        finally
        {
            if (success || !Env.Flag("HOMEHARBOR_KEEP_FAILED_PACKAGE_ROOT"))
            {
                await DeleteMappedDirectoryAsync(buildRoot, CancellationToken.None);
            }
            else
            {
                Console.Error.WriteLine("Preserved failed SELinux package build root: " + buildRoot);
            }
        }
    }

    internal static IReadOnlySet<string> ParseDependencies(string sourceInfo)
    {
        var dependencies = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in sourceInfo.Split('\n'))
        {
            var separator = line.IndexOf(" = ", StringComparison.Ordinal);
            if (separator < 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            if (!key.StartsWith("depends", StringComparison.Ordinal) &&
                !key.StartsWith("makedepends", StringComparison.Ordinal) &&
                !key.StartsWith("checkdepends", StringComparison.Ordinal))
            {
                continue;
            }

            var dependency = NormalizeCapability(line[(separator + 3)..]);
            if (dependency.Length > 0)
            {
                _ = dependencies.Add(dependency);
            }
        }

        return dependencies;
    }

    internal static IReadOnlySet<string> ParseCapabilities(string sourceInfo)
    {
        var capabilities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in sourceInfo.Split('\n'))
        {
            var separator = line.IndexOf(" = ", StringComparison.Ordinal);
            if (separator < 0)
            {
                continue;
            }

            var key = line[..separator].Trim();
            if (key != "pkgname" && !key.StartsWith("provides", StringComparison.Ordinal))
            {
                continue;
            }

            var capability = NormalizeCapability(line[(separator + 3)..]);
            if (capability.Length > 0)
            {
                _ = capabilities.Add(capability);
            }
        }

        return capabilities;
    }

    private static string NormalizeCapability(string value)
    {
        var result = value.Trim();
        var operatorIndex = result.IndexOfAny(['<', '>', '=']);
        if (operatorIndex >= 0)
        {
            result = result[..operatorIndex];
        }

        return result.Trim();
    }

    private async Task PrepareBuildRootAsync(
        SelinuxPackageBuildPlan plan,
        string buildRoot,
        string bootstrapConfig,
        CancellationToken cancellationToken)
    {
        var sharedKeys = Path.Combine(_root, "packaging", "arch", "selinux", "keys");
        File.Copy(bootstrapConfig, Path.Combine(buildRoot, "etc", "pacman.conf"), overwrite: true);
        await RunChrootRequiredAsync(
            buildRoot,
            "useradd",
            ["--create-home", "--uid", BuildUserId.ToString(System.Globalization.CultureInfo.InvariantCulture), BuildUser],
            cancellationToken);
        await RunChrootRequiredAsync(
            buildRoot,
            "chown",
            ["-R", $"{BuildUser}:{BuildUser}", "/recipes", "/packages", "/sources", "/build", "/logs", "/home/" + BuildUser],
            cancellationToken);

        var signingKeys = plan.Recipes.Values
            .SelectMany(recipe => Directory.Exists(Path.Combine(recipe.Directory, "keys", "pgp"))
                ? Directory.GetFiles(Path.Combine(recipe.Directory, "keys", "pgp"), "*.asc", SearchOption.TopDirectoryOnly)
                    .Select(path => $"/recipes/{recipe.Name}/keys/pgp/{Path.GetFileName(path)}")
                : [])
            .Concat(Directory.Exists(Path.Combine(sharedKeys, "pgp"))
                ? Directory.GetFiles(Path.Combine(sharedKeys, "pgp"), "*.asc", SearchOption.TopDirectoryOnly)
                    .Select(path => "/keys/pgp/" + Path.GetFileName(path))
                : [])
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (signingKeys.Length > 0)
        {
            await RunChrootRequiredAsync(
                buildRoot,
                "runuser",
                [
                    "-u",
                    BuildUser,
                    "--",
                    "env",
                    "-i",
                    "HOME=/home/" + BuildUser,
                    "LANG=C.UTF-8",
                    "LC_ALL=C.UTF-8",
                    "LOGNAME=" + BuildUser,
                    "PATH=" + CleanBuildPath,
                    "SHELL=/bin/bash",
                    "TERM=dumb",
                    "USER=" + BuildUser,
                    "GNUPGHOME=/home/" + BuildUser + "/.gnupg",
                    "gpg",
                    "--batch",
                    "--import",
                    .. signingKeys
                ],
                cancellationToken);
        }
    }

    private void PrepareBuildInputs(SelinuxPackageBuildPlan plan, string buildRoot)
    {
        var recipesRoot = Directory.CreateDirectory(Path.Combine(buildRoot, "recipes"));
        _ = Directory.CreateDirectory(Path.Combine(buildRoot, "packages"));
        _ = Directory.CreateDirectory(Path.Combine(buildRoot, "sources"));
        _ = Directory.CreateDirectory(Path.Combine(buildRoot, "build"));
        _ = Directory.CreateDirectory(Path.Combine(buildRoot, "logs"));
        foreach (var recipe in plan.Recipes.Values)
        {
            FileTreeCopier.CopyDirectory(
                recipe.Directory,
                Path.Combine(recipesRoot.FullName, recipe.Name),
                path => ArchPackageSetProvenance.IsMaintainedSource(recipe.Directory, path));
        }

        var sharedKeys = Path.Combine(_root, "packaging", "arch", "selinux", "keys");
        if (Directory.Exists(sharedKeys))
        {
            FileTreeCopier.CopyDirectory(sharedKeys, Path.Combine(buildRoot, "keys"));
        }
    }

    private async Task<string> RunMakepkgAsBuilderAsync(
        string buildRoot,
        SelinuxPackageRecipePlan recipe,
        IReadOnlyList<string> arguments,
        bool captureOutput,
        bool retryTransientGitFetchFailures,
        CancellationToken cancellationToken)
    {
        Task<CommandResult> RunAsync(CancellationToken token) => _rootless.RunMappedChrootAsync(
            buildRoot,
            "unshare",
            MakepkgNamespaceArguments(recipe.Name, arguments),
            new CommandRunOptions(
                StreamOutput: !captureOutput,
                StreamError: true,
                Timeout: TimeSpan.FromHours(3)),
            token);

        var result = retryTransientGitFetchFailures
            ? await RunWithTransientGitSourceFetchRetryAsync(
                RunAsync,
                recipe.Name,
                delay: null,
                cancellationToken)
            : await RunAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _ = result.EnsureSuccess("failed to build SELinux package recipe " + recipe.Name);
        return result.Stdout;
    }

    internal static async Task<CommandResult> RunWithTransientGitSourceFetchRetryAsync(
        Func<CancellationToken, Task<CommandResult>> run,
        string recipeName,
        Func<TimeSpan, CancellationToken, Task>? delay,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentException.ThrowIfNullOrWhiteSpace(recipeName);
        delay ??= Task.Delay;
        var retryDelays = new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5) };

        for (var attempt = 1; attempt <= MakepkgMaximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await run(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (result.ExitCode == 0 ||
                !IsTransientGitSourceFetchFailure(result) ||
                attempt == MakepkgMaximumAttempts)
            {
                return result;
            }

            var retryDelay = retryDelays[attempt - 1];
            Console.Error.WriteLine(
                $"Transient Git source download failure for SELinux recipe {recipeName}; " +
                $"retrying makepkg in {retryDelay.TotalSeconds:0} seconds " +
                $"({attempt + 1}/{MakepkgMaximumAttempts}).");
            await delay(retryDelay, cancellationToken);
        }

        throw new InvalidOperationException("unreachable makepkg retry state");
    }

    internal static bool IsTransientGitSourceFetchFailure(CommandResult result)
    {
        if (result.ExitCode == 0)
        {
            return false;
        }

        var output = result.Stdout + Environment.NewLine + result.Stderr;
        var failedDuringGitDownload = output.Split('\n').Any(line =>
        {
            var normalized = line.TrimEnd('\r');
            return normalized.StartsWith(
                       "==> ERROR: Failure while downloading ",
                       StringComparison.Ordinal) &&
                   normalized.EndsWith(" git repo", StringComparison.Ordinal);
        });
        if (!failedDuringGitDownload)
        {
            return false;
        }

        string[] transportFailures =
        [
            "Could not resolve host",
            "Failed to connect to",
            "Connection reset by peer",
            "Connection timed out",
            "Operation timed out",
            "Empty reply from server",
            "RPC failed; curl 56",
            "OpenSSL SSL_read:",
            "GnuTLS recv error",
            "fetch-pack: unexpected disconnect",
            "fatal: early EOF",
            "fatal: the remote end hung up unexpectedly"
        ];
        return transportFailures.Any(marker =>
            output.Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    internal static IReadOnlyList<string> MakepkgNamespaceArguments(
        string recipeName,
        IReadOnlyList<string> arguments) =>
        [
            "--mount",
            "--pid",
            "--fork",
            "--kill-child",
            "sh",
            "-ceu",
            PrivateBuildMountScript,
            "sh",
            "runuser",
            "-u",
            BuildUser,
            "--",
            "env",
            "-i",
            "HOME=/home/" + BuildUser,
            "LANG=C.UTF-8",
            "LC_ALL=C.UTF-8",
            "LOGNAME=" + BuildUser,
            "PATH=" + CleanBuildPath,
            "SHELL=/bin/bash",
            "TERM=dumb",
            "USER=" + BuildUser,
            "GIT_TERMINAL_PROMPT=0",
            "PKGDEST=/packages",
            "SRCDEST=/sources",
            "BUILDDIR=/build/" + recipeName,
            "LOGDEST=/logs",
            "makepkg",
            "--dir",
            "/recipes/" + recipeName,
            .. arguments
        ];

    private async Task<IReadOnlyDictionary<string, string>> RequireRecipePackagesAsync(
        string buildRoot,
        SelinuxPackageRecipePlan recipe,
        CancellationToken cancellationToken)
    {
        var packageDirectory = Path.Combine(buildRoot, "packages");
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var archive in Directory.GetFiles(packageDirectory, "*.pkg.tar.*", SearchOption.TopDirectoryOnly)
                     .Where(path => !path.EndsWith(".sig", StringComparison.Ordinal))
                     .Order(StringComparer.Ordinal))
        {
            var packageName = await ReadPackageNameAsync(archive, cancellationToken);
            if (recipe.Packages.Contains(packageName, StringComparer.Ordinal))
            {
                result[packageName] = archive;
            }
        }

        var missing = recipe.Packages.Where(package => !result.ContainsKey(package)).ToArray();
        return missing.Length > 0
            ? throw new InvalidOperationException(
                $"SELinux recipe {recipe.Name} did not produce declared packages: {string.Join(", ", missing)}")
            : result;
    }

    private async Task<string> ReadPackageNameAsync(string archive, CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(
            "bsdtar",
            ["-xOf", archive, ".PKGINFO"],
            cancellationToken: cancellationToken);
        _ = result.EnsureSuccess("could not inspect package archive " + archive);
        var packageName = result.Stdout.Split('\n')
            .Select(line => line.Split('=', 2, StringSplitOptions.TrimEntries))
            .Where(parts => parts.Length == 2 && parts[0] == "pkgname")
            .Select(parts => parts[1])
            .SingleOrDefault();
        return string.IsNullOrWhiteSpace(packageName)
            ? throw new InvalidOperationException("package archive has no pkgname: " + archive)
            : packageName;
    }

    private static void ValidateDeclaredPackages(SelinuxPackageRecipePlan recipe, string sourceInfo)
    {
        var actual = sourceInfo.Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith("pkgname = ", StringComparison.Ordinal))
            .Select(line => line["pkgname = ".Length..].Trim())
            .ToHashSet(StringComparer.Ordinal);
        var expected = recipe.Packages.ToHashSet(StringComparer.Ordinal);
        if (!actual.SetEquals(expected))
        {
            throw new InvalidOperationException(
                $"SELinux recipe {recipe.Name} manifest outputs [{string.Join(", ", expected.Order(StringComparer.Ordinal))}] " +
                $"do not match PKGBUILD outputs [{string.Join(", ", actual.Order(StringComparer.Ordinal))}]");
        }
    }

    private static IReadOnlySet<string> LocalCapabilities(
        SelinuxPackageBuildPlan plan,
        IEnumerable<string> sourceInfo)
    {
        var capabilities = plan.Recipes.Values
            .SelectMany(recipe => recipe.Packages)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var info in sourceInfo)
        {
            capabilities.UnionWith(ParseCapabilities(info));
        }

        return capabilities;
    }

    private async Task RunChrootRequiredAsync(
        string buildRoot,
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        var result = await _rootless.RunMappedChrootAsync(
            buildRoot,
            fileName,
            arguments,
            new CommandRunOptions(StreamOutput: true, StreamError: true, Timeout: timeout),
            cancellationToken);
        _ = result.EnsureSuccess($"SELinux package build root command failed: {fileName}");
    }

    private async Task DeleteMappedDirectoryAsync(string path, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        var fullPath = Path.GetFullPath(path);
        if (!SecurityGuards.IsInsideDirectory(fullPath, Path.Combine(_root, ".work")))
        {
            throw new InvalidOperationException("refusing to remove unmanaged SELinux package work directory: " + fullPath);
        }

        var result = await _rootless.RunMappedRootAsync(
            "rm",
            ["-rf", "--", fullPath],
            new CommandRunOptions(StreamError: true, Timeout: TimeSpan.FromMinutes(5)),
            cancellationToken);
        _ = result.EnsureSuccess("could not remove SELinux package work directory " + fullPath);
    }

    private async Task RequireToolsAsync(IEnumerable<string> tools, CancellationToken cancellationToken)
    {
        foreach (var tool in tools)
        {
            var result = await _runner.RunAsync(
                "sh",
                ["-c", "command -v \"$1\" >/dev/null 2>&1", "sh", tool],
                new CommandRunOptions(ThrowOnStartFailure: false),
                cancellationToken);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException("missing required SELinux package build tool: " + tool);
            }
        }
    }
}
