using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace HomeHarbor.Tooling;

public sealed class BuildToolCommands(string root, ICommandRunner? runner = null)
{
    private readonly string _root = BuildKeyDefaults.Apply(root);
    private readonly ICommandRunner _runner = runner ?? new ProcessCommandRunner();
    private readonly RootlessBuildExecutor _rootless = new(runner ?? new ProcessCommandRunner());

    public async Task BuildEfiLoaderAsync(string output, CancellationToken cancellationToken = default)
    {
        var plan = SystemImageBuildDescriptor.LoadDefaultPlan(_root, "0.0.0");
        await BuildEfiLoaderAsync(output, plan.Product, plan.Security, cancellationToken);
    }

    public async Task BuildEfiLoaderAsync(
        string output,
        SystemImageProductPlan product,
        CancellationToken cancellationToken = default)
        => await BuildEfiLoaderAsync(
            output,
            product,
            new SystemImageSecurityPlan(true, SecureBootAssets.IsEnabled(), true, false),
            cancellationToken);

    public async Task BuildEfiLoaderAsync(
        string output,
        SystemImageProductPlan product,
        SystemImageSecurityPlan security,
        CancellationToken cancellationToken = default)
    {
        var fullOutput = Path.GetFullPath(output);
        await RequireToolsAsync(["avbtool", "clang", "lld-link", "make", "openssl"], cancellationToken);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
        var buildDirectory = Path.Combine(_root, ".work", "efi-loader");
        var publicKeyHeader = Path.Combine(buildDirectory, "homeharbor-avb-public-key.h");
        var productHeader = Path.Combine(buildDirectory, "arch-ab-product.h");
        await GenerateEfiAvbPublicKeyHeaderAsync(publicKeyHeader, product, cancellationToken);
        await File.WriteAllTextAsync(
            productHeader,
            RenderProductHeader(product, security),
            Encoding.ASCII,
            cancellationToken);
        var makefile = SystemUtilityAssets.RequireAsset(_root, "boot", "bootloader", "Makefile");
        await RunRequiredAsync(
            "make",
            [
                "-C",
                Path.GetDirectoryName(makefile)!,
                "OUTPUT=" + fullOutput,
                "BUILD_DIR=" + buildDirectory,
                "AVB_PUBLIC_KEY_HEADER=" + publicKeyHeader,
                "PRODUCT_HEADER=" + productHeader,
                "PRODUCT_DISPLAY_NAME=" + product.DisplayName,
                "EFI_DIRECTORY=" + product.EfiDirectory,
                "BOOT_STATE_PATH=" + product.BootStatePath,
                "EFI_VARIABLE_PREFIX=" + product.EfiVariablePrefix,
                "all"
            ],
            cancellationToken);
    }

    public async Task BuildHomeHarborAvbAsync(string output, CancellationToken cancellationToken = default)
    {
        var fullOutput = Path.GetFullPath(output);
        await RequireToolsAsync(["cc"], cancellationToken);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
        await RunRequiredAsync(
            "cc",
            [
                "-O2",
                "-Wall",
                "-Wextra",
                "-o",
                fullOutput,
                SystemUtilityAssets.RequireAsset(_root, "boot", "avb", "homeharbor-avb.c"),
                "-lcrypto"
            ],
            cancellationToken);
        Console.WriteLine("Built " + fullOutput);
    }

    public async Task BuildHomeHarborInitAsync(string output, CancellationToken cancellationToken = default)
    {
        var fullOutput = Path.GetFullPath(output);
        await RequireToolsAsync(["cc"], cancellationToken);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
        await RunRequiredAsync(
            "cc",
            HomeHarborInitHelperBuild.CompileArguments(
                fullOutput,
                SystemUtilityAssets.RequireAsset(_root, "boot", "init", "homeharbor-verity.c")),
            cancellationToken);
        Console.WriteLine("Built " + fullOutput);
    }

    public async Task BuildProductAvbAsync(
        string output,
        SystemImageProductPlan product,
        CancellationToken cancellationToken = default)
    {
        var fullOutput = Path.GetFullPath(output);
        await RequireToolsAsync(["cc"], cancellationToken);
        var source = await WriteProductizedSourceAsync(
            SystemUtilityAssets.RequireAsset(_root, "boot", "avb", "homeharbor-avb.c"),
            product,
            "arch-ab-avb.c",
            cancellationToken);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
        await RunRequiredAsync(
            "cc",
            ["-O2", "-Wall", "-Wextra", "-o", fullOutput, source, "-lcrypto"],
            cancellationToken);
        Console.WriteLine("Built " + fullOutput);
    }

    public async Task BuildProductInitAsync(
        string output,
        SystemImageProductPlan product,
        CancellationToken cancellationToken = default)
    {
        var fullOutput = Path.GetFullPath(output);
        await RequireToolsAsync(["cc"], cancellationToken);
        var source = await WriteProductizedSourceAsync(
            SystemUtilityAssets.RequireAsset(_root, "boot", "init", "homeharbor-verity.c"),
            product,
            "arch-ab-verity.c",
            cancellationToken);
        _ = Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
        await RunRequiredAsync(
            "cc",
            HomeHarborInitHelperBuild.CompileArguments(fullOutput, source),
            cancellationToken);
        Console.WriteLine("Built " + fullOutput);
    }

    internal static string ProductizeText(string input, SystemImageProductPlan product)
        => input
            .Replace(EfiBootVariables.VendorGuid, product.EfiVendorGuid, StringComparison.OrdinalIgnoreCase)
            .Replace("HOMEHARBOR", product.EnvironmentPrefix, StringComparison.Ordinal)
            .Replace("HomeHarbor", product.EfiVariablePrefix, StringComparison.Ordinal)
            .Replace("rd.homeharbor.", "rd." + product.KernelParameterPrefix + ".", StringComparison.Ordinal)
            .Replace("homeharbor.", product.KernelParameterPrefix + ".", StringComparison.Ordinal)
            .Replace("homeharbor", product.PackagePrefix, StringComparison.Ordinal);

    private async Task<string> WriteProductizedSourceAsync(
        string source,
        SystemImageProductPlan product,
        string fileName,
        CancellationToken cancellationToken)
    {
        var outputDirectory = Path.Combine(_root, ".work", "product-sources", product.Id);
        _ = Directory.CreateDirectory(outputDirectory);
        var output = Path.Combine(outputDirectory, fileName);
        await File.WriteAllTextAsync(
            output,
            ProductizeText(await File.ReadAllTextAsync(source, cancellationToken), product),
            cancellationToken);
        return output;
    }

    public string GetSelinuxDependencyInputSha256()
        => SelinuxDependencyPackageSetProvenance.ComputeDependencyInputSha256(_root);

    public async Task BuildSelinuxDependencyPackagesAsync(
        string output,
        string workDirectory,
        CancellationToken cancellationToken = default)
    {
        RequireNormalPackageBuilder("selinux-dependency-build");
        await RequireToolsAsync(
            ["arch-chroot", "bsdtar", "fakeroot", "git", "makepkg", "pacman", "pacstrap", "repo-add", "unshare"],
            cancellationToken);
        await _rootless.RequireReadyAsync(cancellationToken);

        var workRoot = Path.Combine(_root, ".work");
        var artifactsRoot = Path.Combine(_root, "artifacts");
        var work = RequireManagedBuildPath(
            workDirectory,
            "SELinux dependency package work directory",
            workRoot);
        var packageOutput = RequireManagedBuildPath(
            output,
            "SELinux dependency package output",
            workRoot,
            artifactsRoot);
        RequireSeparateDirectories(work, packageOutput, "SELinux dependency package work and output");

        await DeleteMappedBuildPathAsync(work, cancellationToken);
        await DeleteMappedBuildPathAsync(packageOutput, cancellationToken);
        _ = Directory.CreateDirectory(work);
        _ = Directory.CreateDirectory(packageOutput);

        var inputSha256 = GetSelinuxDependencyInputSha256();
        var plan = SelinuxPackageBuildDescriptor.LoadDefaultPlan(_root);
        await new SelinuxPackageBuilder(_root, _runner).BuildAsync(
            plan,
            work,
            packageOutput,
            cancellationToken);
        await SelinuxDependencyPackageSetProvenance.WriteAsync(
            _root,
            packageOutput,
            inputSha256,
            _runner,
            cancellationToken);
        await SelinuxDependencyPackageSetProvenance.VerifyAsync(
            _root,
            packageOutput,
            _runner,
            cancellationToken);
        Console.WriteLine("Built verified SELinux dependency packages: " + packageOutput);
    }

    public async Task VerifySelinuxDependencyPackagesAsync(
        string input,
        CancellationToken cancellationToken = default)
    {
        await RequireToolsAsync(["bsdtar"], cancellationToken);
        await SelinuxDependencyPackageSetProvenance.VerifyAsync(
            _root,
            Path.GetFullPath(input),
            _runner,
            cancellationToken);
        Console.WriteLine("Verified SELinux dependency packages: " + Path.GetFullPath(input));
    }

    public async Task<ArchLocalPackageRepository> ArchPackageAsync(string version, CancellationToken cancellationToken = default)
        => await ArchPackageAsync(
            version,
            SystemImageBuildDescriptor.LoadDefaultPlan(_root, version),
            cancellationToken);

    public async Task<ArchLocalPackageRepository> ArchPackageAsync(
        string version,
        SystemImageBuildPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (!string.Equals(version, plan.Version, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"package version {version} does not match system plan {plan.Version}");
        }
        RequireSafeVersion(version);
        RequireNormalPackageBuilder("arch-package");

        await RequireToolsAsync(
            ["arch-chroot", "bsdtar", "dotnet", "fakeroot", "git", "makepkg", "pacman", "pacstrap", "pnpm", "repo-add", "tar", "unshare"],
            cancellationToken);
        await _rootless.RequireReadyAsync(cancellationToken);

        var workRoot = Path.Combine(_root, ".work");
        var artifactsRoot = Path.Combine(_root, "artifacts");
        var packageWorkSetting = ProductEnvironment(
            plan.Product,
            "PACKAGE_WORK",
            Path.Combine(workRoot, "arch-package", version));
        var packageOutputSetting = ProductEnvironment(
            plan.Product,
            "PACKAGE_OUTPUT",
            Path.Combine(artifactsRoot, "packages", version));
        var packageWork = RequireManagedBuildPath(
            packageWorkSetting.Value!,
            packageWorkSetting.Name,
            workRoot);
        var packageOutput = RequireManagedBuildPath(
            packageOutputSetting.Value!,
            packageOutputSetting.Name,
            workRoot,
            artifactsRoot);
        RequireSeparateDirectories(packageWork, packageOutput, "package work and output");
        var configuredDependencyCache = plan.Security.Selinux
            ? ProductEnvironment(plan.Product, "SELINUX_DEPENDENCY_CACHE", null).Value
            : null;
        var dependencyCache = string.IsNullOrWhiteSpace(configuredDependencyCache)
            ? null
            : Path.GetFullPath(configuredDependencyCache);
        if (dependencyCache is not null)
        {
            RequireSeparateDirectories(
                dependencyCache,
                packageWork,
                "SELinux dependency cache and package work");
            RequireSeparateDirectories(
                dependencyCache,
                packageOutput,
                "SELinux dependency cache and package output");
        }

        var sourceDir = Path.Combine(packageWork, "source");
        var buildDir = Path.Combine(packageWork, "makepkg");
        var sourceTarball = Path.Combine(sourceDir, $"{plan.Product.PackagePrefix}-{version}.tar.gz");

        await DeleteMappedBuildPathAsync(packageWork, cancellationToken);
        await DeleteMappedBuildPathAsync(packageOutput, cancellationToken);
        _ = Directory.CreateDirectory(sourceDir);
        _ = Directory.CreateDirectory(buildDir);
        _ = Directory.CreateDirectory(packageOutput);

        string? selinuxSourceSha256 = null;
        if (plan.Security.Selinux)
        {
            selinuxSourceSha256 = ArchPackageSetProvenance.ComputeSelinuxSourceSha256(_root);
            if (dependencyCache is null)
            {
                var dependencyOutput = Path.Combine(packageWork, "selinux-packages");
                _ = Directory.CreateDirectory(dependencyOutput);
                var dependencyInputSha256 = GetSelinuxDependencyInputSha256();
                await new SelinuxPackageBuilder(_root, _runner).BuildAsync(
                    SelinuxPackageBuildDescriptor.LoadDefaultPlan(_root),
                    Path.Combine(packageWork, "selinux"),
                    dependencyOutput,
                    cancellationToken);
                await SelinuxDependencyPackageSetProvenance.WriteAsync(
                    _root,
                    dependencyOutput,
                    dependencyInputSha256,
                    _runner,
                    cancellationToken);
                dependencyCache = dependencyOutput;
            }

            await SelinuxDependencyPackageSetProvenance.ImportVerifiedAsync(
                _root,
                dependencyCache,
                packageOutput,
                _runner,
                cancellationToken);
        }

        await CreateCleanSourceArchiveAsync(
            sourceDir,
            sourceTarball,
            plan.Product.PackagePrefix,
            version,
            cancellationToken);

        var packagingTarball = Path.Combine(_root, "packaging", "arch", $"{plan.Product.PackagePrefix}-{version}.tar.gz");
        if (File.Exists(packagingTarball))
        {
            File.Delete(packagingTarball);
        }

        var environmentPrefix = plan.Product.EnvironmentPrefix;
        var environment = new Dictionary<string, string>
        {
            ["DOTNET_CLI_HOME"] = Path.Combine(packageWork, "home", ".dotnet"),
            ["HOME"] = Path.Combine(packageWork, "home"),
            [environmentPrefix + "_VERSION"] = version,
            [environmentPrefix + "_CHANNEL"] = ProductEnvironment(plan.Product, "CHANNEL", ReleaseChannel.Dev).Value!,
            [environmentPrefix + "_SOURCE_TARBALL"] = sourceTarball,
            ["LOGNAME"] = Environment.UserName,
            ["NUGET_PACKAGES"] = Path.Combine(packageWork, "nuget-packages"),
            ["PKGDEST"] = packageOutput,
            ["BUILDDIR"] = buildDir,
            ["USER"] = Environment.UserName,
            ["XDG_CACHE_HOME"] = Path.Combine(packageWork, "home", ".cache")
        };
        _ = Directory.CreateDirectory(environment["HOME"]);
        await RunRequiredAsync(
            "makepkg",
            ["--force", "--cleanbuild", "--clean", "--nodeps"],
            cancellationToken,
            workingDirectory: Path.Combine(_root, "packaging", "arch"),
            environment: environment);

        await BuildProductKernelPackagesAsync(plan, packageOutput, cancellationToken);

        await ArchPackageArchiveValidator.ValidatePackagesAsync(
            packageOutput,
            version,
            [plan.Product.ControlPackage, plan.Product.RecoveryPackage, plan.Product.InstallerPackage],
            _runner,
            cancellationToken);

        foreach (var package in Directory.GetFiles(packageOutput, "*.pkg.tar.*", SearchOption.TopDirectoryOnly).Order(StringComparer.Ordinal))
        {
            Console.WriteLine(package);
        }

        if (plan.Security.Selinux && string.Equals(plan.Product.Id, "homeharbor", StringComparison.Ordinal))
        {
            await ArchPackageSetProvenance.WriteAsync(
                _root,
                version,
                packageOutput,
                selinuxSourceSha256!,
                cancellationToken);
        }
        else
        {
            await ArchProductPackageSetProvenance.WriteAsync(
                _root,
                plan,
                packageOutput,
                cancellationToken);
        }

        return await ArchLocalPackageRepositoryBuilder.CreateAsync(
            packageOutput,
            Path.Combine(packageWork, "repository"),
            _runner,
            plan.Product.LocalRepository,
            cancellationToken);
    }

    public async Task GenerateEfiAvbPublicKeyHeaderAsync(string output, CancellationToken cancellationToken = default)
        => await GenerateEfiAvbPublicKeyHeaderAsync(
            output,
            SystemImageBuildDescriptor.LoadDefaultPlan(_root, "0.0.0").Product,
            cancellationToken);

    public async Task GenerateEfiAvbPublicKeyHeaderAsync(
        string output,
        SystemImageProductPlan product,
        CancellationToken cancellationToken = default)
    {
        await RequireToolsAsync(["avbtool", "openssl"], cancellationToken);
        var fullOutput = Path.GetFullPath(output);
        var work = Path.Combine(Path.GetTempPath(), "homeharbor-avb-public-key-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(work);
        try
        {
            var encodedKey = Path.Combine(work, "homeharbor-avb-public-key.avbpub");
            await WriteEncodedAvbPublicKeyAsync(encodedKey, product, cancellationToken);

            _ = Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
            var encoded = await File.ReadAllBytesAsync(encodedKey, cancellationToken);
            _ = RequireSelectorSupportedAvbPublicKey(encoded);
            await File.WriteAllTextAsync(fullOutput, RenderAvbPublicKeyHeader(encoded), Encoding.ASCII, cancellationToken);
        }
        finally
        {
            DeleteIfExists(work);
        }
    }

    public async Task GenerateAvbPublicKeyPemAsync(
        string output,
        SystemImageProductPlan product,
        CancellationToken cancellationToken = default)
    {
        await RequireToolsAsync(["avbtool", "openssl"], cancellationToken);
        var fullOutput = Path.GetFullPath(output);
        var work = Path.Combine(Path.GetTempPath(), "homeharbor-avb-public-key-pem-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(work);
        try
        {
            var encodedKey = Path.Combine(work, "homeharbor-avb-public-key.avbpub");
            await WriteEncodedAvbPublicKeyAsync(encodedKey, product, cancellationToken);
            var encoded = await File.ReadAllBytesAsync(encodedKey, cancellationToken);
            var pem = RenderAvbPublicKeyPem(encoded);
            _ = Directory.CreateDirectory(Path.GetDirectoryName(fullOutput)!);
            await File.WriteAllTextAsync(fullOutput, pem, Encoding.ASCII, cancellationToken);
        }
        finally
        {
            DeleteIfExists(work);
        }
    }

    private async Task WriteEncodedAvbPublicKeyAsync(
        string output,
        SystemImageProductPlan product,
        CancellationToken cancellationToken)
    {
        var publicKey = ProductBuildEnvironment.Optional(product, "AVB_PUBLIC_KEY");
        if (!string.IsNullOrWhiteSpace(publicKey))
        {
            RequireFile(publicKey, "AVB public key does not point to a readable file");
            if (IsEncodedAvbPublicKey(publicKey))
            {
                File.Copy(publicKey, output, overwrite: true);
            }
            else
            {
                await RunRequiredAsync("avbtool", ["extract_public_key", "--key", publicKey, "--output", output], cancellationToken);
            }

            return;
        }

        var avbPrivateKey = ProductBuildEnvironment.Optional(product, "AVB_PRIVATE_KEY");
        if (string.IsNullOrWhiteSpace(avbPrivateKey) &&
            string.Equals(product.Id, "homeharbor", StringComparison.Ordinal))
        {
            // Schema-one compatibility only. New product profiles must
            // use an AVB-specific key and never couple it to Secure Boot.
            avbPrivateKey = Env.Optional("HOMEHARBOR_SECURE_BOOT_KEY");
        }
        if (!string.IsNullOrWhiteSpace(avbPrivateKey))
        {
            RequireFile(avbPrivateKey, "AVB private key does not point to a readable file");
            await RunRequiredAsync("avbtool", ["extract_public_key", "--key", avbPrivateKey, "--output", output], cancellationToken);
            return;
        }

        throw new InvalidOperationException(
            $"no AVB public key source found; set ARCH_AB_AVB_PUBLIC_KEY, {product.EnvironmentPrefix}_AVB_PUBLIC_KEY, " +
            $"ARCH_AB_AVB_PRIVATE_KEY, or {product.EnvironmentPrefix}_AVB_PRIVATE_KEY");
    }

    private static string RenderAvbPublicKeyHeader(byte[] data)
    {
        var builder = new StringBuilder();
        _ = builder.AppendLine("#include <stdint.h>");
        _ = builder.AppendLine(CultureInfo.InvariantCulture, $"#define HOMEHARBOR_TRUSTED_AVB_PUBLIC_KEY_SIZE {data.Length}U");
        _ = builder.AppendLine("static const uint8_t HomeHarborTrustedAvbPublicKey[HOMEHARBOR_TRUSTED_AVB_PUBLIC_KEY_SIZE] = {");
        for (var i = 0; i < data.Length; i += 12)
        {
            var chunk = data.Skip(i).Take(12).Select(value => "0x" + value.ToString("x2", CultureInfo.InvariantCulture));
            _ = builder.Append("    ");
            _ = builder.Append(string.Join(", ", chunk));
            if (i + 12 < data.Length)
            {
                _ = builder.Append(',');
            }

            _ = builder.AppendLine();
        }

        _ = builder.AppendLine("};");
        return builder.ToString();
    }

    internal static string RenderAvbPublicKeyPem(byte[] data)
    {
        var bits = RequireSelectorSupportedAvbPublicKey(data);
        var modulusBytes = bits / 8;
        using var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters
        {
            Modulus = data.AsSpan(8, modulusBytes).ToArray(),
            Exponent = [0x01, 0x00, 0x01]
        });
        return rsa.ExportSubjectPublicKeyInfoPem() + "\n";
    }

    internal static string RenderProductHeader(
        SystemImageProductPlan product,
        SystemImageSecurityPlan security)
    {
        static string Wide(string value, string label)
        {
            if (value.Any(character => character is < ' ' or > '~' || character is '"'))
            {
                throw new InvalidOperationException(label + " must contain printable ASCII without quotes");
            }

            return "L\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal) + "\"";
        }

        static string EfiGuidInitializer(string value)
        {
            if (!Guid.TryParse(value, out var guid))
            {
                throw new InvalidOperationException("product efiVendorGuid must be a GUID");
            }

            var segments = guid.ToString("D").ToUpperInvariant().Split('-');
            var data4 = segments[3] + segments[4];
            var bytes = Enumerable.Range(0, 8)
                .Select(index => string.Concat("0x".AsSpan(), data4.AsSpan(index * 2, 2)));
            return "{0x" + segments[0] + ", 0x" + segments[1] + ", 0x" + segments[2] +
                ", {" + string.Join(", ", bytes) + "}}";
        }

        var efiPath = "\\" + product.EfiDirectory.Replace('/', '\\') + "\\";
        var bootStatePath = "\\" + product.BootStatePath.Replace('/', '\\');
        return $"""
            #include <stdint.h>
            #define ARCH_AB_PRODUCT_NAME {Wide(product.DisplayName, "product display name")}
            #define ARCH_AB_BOOT_STATE_PATH {Wide(bootStatePath, "product boot state path")}
            #define ARCH_AB_CACHE_PATH {Wide(efiPath + "current.efi", "product EFI directory")}
            #define ARCH_AB_BOOT_NEXT_NAME {Wide(product.EfiVariablePrefix + "BootNext", "EFI variable prefix")}
            #define ARCH_AB_BOOT_CURRENT_NAME {Wide(product.EfiVariablePrefix + "BootCurrent", "EFI variable prefix")}
            #define ARCH_AB_DATA_PASSPHRASE_NAME {Wide(product.EfiVariablePrefix + "DataPassphrase", "EFI variable prefix")}
            #define ARCH_AB_DATA_UNLOCK_MODE_NAME {Wide(product.EfiVariablePrefix + "DataUnlockMode", "EFI variable prefix")}
            #define ARCH_AB_VBMETA_WARNING_DISABLED_NAME {Wide(product.EfiVariablePrefix + "VbmetaPreflightWarningDisabled", "EFI variable prefix")}
            #define ARCH_AB_SECURE_BOOT_WARNING_DISABLED_NAME {Wide(product.EfiVariablePrefix + "SecureBootWarningDisabled", "EFI variable prefix")}
            #define ARCH_AB_EFI_VENDOR_GUID {EfiGuidInitializer(product.EfiVendorGuid)}
            #define ARCH_AB_WARN_SECURE_BOOT {(security.SecureBoot ? 1 : 0)}
            #define ARCH_AB_AVB_FAIL_CLOSED {(security.AvbFailClosed ? 1 : 0)}
            """ + "\n";
    }

    private static bool IsEncodedAvbPublicKey(string path)
    {
        var data = File.ReadAllBytes(path);
        if (data.Length < 8)
        {
            return false;
        }

        var bits = (data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3];
        return bits is 2048 or 4096 && data.Length == 8 + bits / 8 * 2;
    }

    internal static int RequireSelectorSupportedAvbPublicKey(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        if (data.Length < 8)
        {
            throw new InvalidOperationException("encoded AVB public key is truncated");
        }

        var bits = (data[0] << 24) | (data[1] << 16) | (data[2] << 8) | data[3];
        if (bits is not (2048 or 4096))
        {
            throw new InvalidOperationException(
                $"HomeHarborBoot supports only RSA-2048 and RSA-4096 AVB keys; got RSA-{bits}");
        }

        var expectedBytes = checked(8 + bits / 8 * 2);
        if (data.Length != expectedBytes)
        {
            throw new InvalidOperationException(
                $"encoded RSA-{bits} AVB public key has an unexpected size: {data.Length} != {expectedBytes}");
        }

        return bits;
    }

    private static void RequireSafeVersion(string version)
    {
        if (!SecurityGuards.IsSafeVersion(version))
        {
            throw new InvalidOperationException("version must contain only letters, numbers, dot, underscore, and dash: " + version);
        }
    }

    private static void RequireNormalPackageBuilder(string command)
    {
        if (OperatingSystem.IsLinux() && Environment.UserName == "root")
        {
            throw new InvalidOperationException(
                command + " must run as a normal user; makepkg and fakeroot must not be driven from a rootful builder.");
        }
    }

    internal static void RequireSeparateDirectories(string first, string second, string label)
    {
        if (SecurityGuards.IsInsideDirectory(first, second) ||
            SecurityGuards.IsInsideDirectory(second, first))
        {
            throw new InvalidOperationException(label + " must be separate directories");
        }
    }

    internal static string RequireManagedBuildPath(string path, string label, params string[] allowedRoots)
    {
        var fullPath = Path.GetFullPath(path);
        var matchingRoot = allowedRoots
            .Select(Path.GetFullPath)
            .FirstOrDefault(root => SecurityGuards.IsInsideDirectory(fullPath, root) &&
                !string.Equals(fullPath, root, StringComparison.Ordinal)) ?? throw new InvalidOperationException(label + " must be a child of a managed .work or artifacts directory: " + fullPath);
        var current = fullPath;
        while (SecurityGuards.IsInsideDirectory(current, matchingRoot))
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                var attributes = File.GetAttributes(current);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new InvalidOperationException(label + " must not traverse a symbolic link: " + current);
                }
            }

            if (string.Equals(current, matchingRoot, StringComparison.Ordinal))
            {
                break;
            }

            current = Path.GetDirectoryName(current)
                ?? throw new InvalidOperationException(label + " has no managed parent directory");
        }

        return fullPath;
    }

    private static void RequireFile(string path, string message)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(message + ": " + path, path);
        }
    }

    private async Task CreateCleanSourceArchiveAsync(
        string sourceDirectory,
        string sourceTarball,
        string packagePrefix,
        string version,
        CancellationToken cancellationToken)
    {
        var listed = await _runner.RunAsync(
            "git",
            ["ls-files", "--cached", "--others", "--exclude-standard", "-z"],
            new CommandRunOptions(WorkingDirectory: _root, StreamError: true),
            cancellationToken);
        _ = listed.EnsureSuccess("could not enumerate clean product source inputs");

        var sourcePaths = SelectCleanSourcePaths(
            _root,
            listed.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries));
        if (sourcePaths.Count == 0)
        {
            throw new InvalidOperationException("clean product source input set is empty");
        }

        var stageName = packagePrefix + "-" + version;
        var stage = Path.Combine(sourceDirectory, stageName);
        CopyTrackedTree(_root, stage, sourcePaths);
        await CopyInitializedSubmodulesAsync(stage, cancellationToken);
        await RunRequiredAsync(
            "tar",
            ["-C", sourceDirectory, "-czf", sourceTarball, stageName],
            cancellationToken);
    }

    private static void CopyTrackedTree(
        string sourceRoot,
        string destinationRoot,
        IReadOnlyCollection<string> sourcePaths)
    {
        var included = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sourcePath in sourcePaths)
        {
            var current = sourcePath;
            while (!string.Equals(current, sourceRoot, StringComparison.Ordinal))
            {
                _ = included.Add(current);
                current = Path.GetDirectoryName(current)
                    ?? throw new InvalidOperationException("source input has no repository parent: " + sourcePath);
            }
        }

        FileTreeCopier.CopyDirectory(
            sourceRoot,
            destinationRoot,
            path => included.Contains(Path.GetFullPath(path)));
    }

    private async Task CopyInitializedSubmodulesAsync(string stage, CancellationToken cancellationToken)
    {
        var status = await _runner.RunAsync(
            "git",
            ["submodule", "status", "--recursive"],
            new CommandRunOptions(WorkingDirectory: _root, StreamError: true),
            cancellationToken);
        _ = status.EnsureSuccess("could not inspect source submodules");
        var invalidStatus = status.Stdout
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.Length == 0 || line[0] != ' ');
        if (invalidStatus is not null)
        {
            throw new InvalidOperationException(
                "all recursive source submodules must be initialized at their pinned revisions: " +
                invalidStatus.Trim());
        }

        var listedSubmodules = await _runner.RunAsync(
            "git",
            ["submodule", "foreach", "--recursive", "--quiet", "printf '%s\\0' \"$displaypath\""],
            new CommandRunOptions(WorkingDirectory: _root, StreamError: true),
            cancellationToken);
        _ = listedSubmodules.EnsureSuccess("could not enumerate source submodules");
        foreach (var relativePath in listedSubmodules.Stdout
                     .Split('\0', StringSplitOptions.RemoveEmptyEntries)
                     .Order(StringComparer.Ordinal))
        {
            if (Path.IsPathRooted(relativePath) ||
                relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
                    .Contains("..", StringComparer.Ordinal))
            {
                throw new InvalidOperationException("git returned an unsafe submodule path: " + relativePath);
            }

            var submoduleRoot = Path.GetFullPath(Path.Combine(_root, relativePath));
            if (!SecurityGuards.IsInsideDirectory(submoduleRoot, _root) || !Directory.Exists(submoduleRoot))
            {
                throw new DirectoryNotFoundException("source submodule is unavailable: " + submoduleRoot);
            }

            var listed = await _runner.RunAsync(
                "git",
                ["ls-files", "--cached", "-z"],
                new CommandRunOptions(WorkingDirectory: submoduleRoot, StreamError: true),
                cancellationToken);
            _ = listed.EnsureSuccess("could not enumerate source submodule " + relativePath);
            var sourcePaths = SelectCleanSourcePaths(
                submoduleRoot,
                listed.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries));
            if (sourcePaths.Count == 0)
            {
                throw new InvalidOperationException("source submodule input set is empty: " + relativePath);
            }

            CopyTrackedTree(
                submoduleRoot,
                Path.Combine(stage, relativePath),
                sourcePaths);
        }
    }

    internal static IReadOnlyList<string> SelectCleanSourcePaths(string root, IEnumerable<string> relativePaths)
    {
        var repositoryRoot = Path.GetFullPath(root);
        var selinuxRoot = Path.Combine(repositoryRoot, "packaging", "arch", "selinux");
        var result = new List<string>();
        foreach (var relativePath in relativePaths)
        {
            if (string.IsNullOrWhiteSpace(relativePath) ||
                Path.IsPathRooted(relativePath) ||
                relativePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries).Contains("..", StringComparer.Ordinal))
            {
                throw new InvalidOperationException("git returned an unsafe source input path: " + relativePath);
            }

            var fullPath = Path.GetFullPath(Path.Combine(repositoryRoot, relativePath));
            if (!SecurityGuards.IsInsideDirectory(fullPath, repositoryRoot))
            {
                throw new InvalidOperationException("source input escapes the repository: " + relativePath);
            }

            if (SecurityGuards.IsInsideDirectory(fullPath, selinuxRoot) &&
                !ArchPackageSetProvenance.IsMaintainedSource(selinuxRoot, fullPath))
            {
                continue;
            }

            if (!File.Exists(fullPath) &&
                !Directory.Exists(fullPath) &&
                FileTreeCopier.ReadSymbolicLink(fullPath) is null)
            {
                throw new FileNotFoundException("git source input does not exist", fullPath);
            }

            result.Add(fullPath);
        }

        return result.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static (string Name, string? Value) ProductEnvironment(
        SystemImageProductPlan product,
        string suffix,
        string? defaultValue)
    {
        var genericName = "ARCH_AB_" + suffix;
        var productName = product.EnvironmentPrefix + "_" + suffix;
        var generic = Environment.GetEnvironmentVariable(genericName);
        if (!string.IsNullOrWhiteSpace(generic))
        {
            return (genericName, generic);
        }

        var productValue = Environment.GetEnvironmentVariable(productName);
        return !string.IsNullOrWhiteSpace(productValue)
            ? (productName, productValue)
            : (productName, defaultValue);
    }

    private async Task BuildProductKernelPackagesAsync(
        SystemImageBuildPlan plan,
        string packageOutput,
        CancellationToken cancellationToken)
    {
        var kernelRoot = Path.Combine(_root, "system", plan.Architecture, "kernel");
        if (!Directory.Exists(kernelRoot))
        {
            if (string.Equals(plan.Product.Id, "homeharbor", StringComparison.Ordinal))
            {
                return;
            }
            throw new DirectoryNotFoundException("product kernel manifest root not found: " + kernelRoot);
        }

        var kernelPlan = KernelPackageBuildDescriptor.LoadPlan(kernelRoot, _root, plan.Version);
        var channel = kernelPlan.Channels.SingleOrDefault(item => string.Equals(item.Name, plan.KernelChannel, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"system kernelChannel {plan.KernelChannel} has no kernel manifest");
        if (!plan.Packages.Rootfs.Contains(channel.Kernel.Package, StringComparer.Ordinal) ||
            !plan.Packages.Recovery.Contains(channel.Kernel.Package, StringComparer.Ordinal))
        {
            throw new InvalidOperationException(
                $"kernel package {channel.Kernel.Package} must be present in rootfs and recovery package plans");
        }

        var archBuilds = channel.ArtifactBuilds.Where(build => build.Type == "arch-pkgbuild").ToArray();
        if (archBuilds.Length == 0)
        {
            return;
        }
        if (archBuilds.Length != 1)
        {
            throw new InvalidOperationException($"kernel channel {channel.Name} must declare at most one arch-pkgbuild");
        }

        _ = await new KernelPackageBuilder(_root, plan.Version, channel, _runner)
            .BuildArchPkgbuildAsync(packageOutput, cancellationToken);
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
                throw new InvalidOperationException("missing required tool: " + tool);
            }
        }
    }

    private async Task RunRequiredAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var options = new CommandRunOptions(
            WorkingDirectory: workingDirectory,
            StreamOutput: true,
            StreamError: true,
            EnvironmentOverride: environment);
        if (environment is not null)
        {
            options = RootlessBuildExecutor.IsolatedOptions(options);
        }

        var result = await _runner.RunAsync(
            fileName,
            arguments,
            options,
            cancellationToken);
        _ = result.EnsureSuccess();
    }

    private static void DeleteIfExists(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private async Task DeleteMappedBuildPathAsync(string path, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(path) && !File.Exists(path))
        {
            return;
        }

        var result = await _rootless.RunMappedRootAsync(
            "rm",
            ["-rf", "--", Path.GetFullPath(path)],
            new CommandRunOptions(StreamError: true, Timeout: TimeSpan.FromMinutes(5)),
            cancellationToken);
        _ = result.EnsureSuccess("could not remove mapped package build path " + path);
    }
}
