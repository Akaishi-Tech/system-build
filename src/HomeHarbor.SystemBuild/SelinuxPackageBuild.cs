using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace HomeHarbor.Tooling;

public sealed record SelinuxPackageBuildPlan(
    string UpstreamRepository,
    string UpstreamRevision,
    IReadOnlyDictionary<string, SelinuxPackageRecipePlan> Recipes,
    IReadOnlyList<string> BuildOrder);

public sealed record SelinuxPackageRecipePlan(
    string Name,
    string Directory,
    IReadOnlyList<string> Packages,
    IReadOnlyList<string> InstallPackages,
    bool SkipCheck);

public sealed partial class SelinuxPackageBuildDescriptor
{
    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._+-]*$")]
    private static partial Regex SafeNamePattern();

    [GeneratedRegex("^[0-9a-f]{40}$")]
    private static partial Regex GitRevisionPattern();

    public int SchemaVersion { get; set; }

    public string? UpstreamRepository { get; set; }

    public string? UpstreamRevision { get; set; }

    public List<SelinuxPackageRecipeDescriptor> Recipes { get; set; } = [];

    public List<string> BuildOrder { get; set; } = [];

    public static string DefaultManifestPath(string root)
        => Path.Combine(Path.GetFullPath(root), "packaging", "arch", "selinux", "manifest.yml");

    public static SelinuxPackageBuildPlan LoadDefaultPlan(string root)
        => LoadPlan(DefaultManifestPath(root), root);

    public static SelinuxPackageBuildPlan LoadPlan(string manifestPath, string root)
    {
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException("SELinux package manifest not found", manifestPath);
        }

        var descriptor = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .Build()
            .Deserialize<SelinuxPackageBuildDescriptor>(File.ReadAllText(manifestPath))
            ?? throw new InvalidOperationException("SELinux package manifest is empty: " + manifestPath);
        return descriptor.ToPlan(Path.GetFullPath(root));
    }

    public SelinuxPackageBuildPlan ToPlan(string root)
    {
        if (SchemaVersion != 1)
        {
            throw new InvalidOperationException("SELinux package manifest requires schemaVersion=1");
        }

        var upstreamRepository = RequireHttpsUrl(UpstreamRepository, "SELinux package upstream repository");
        var upstreamRevision = RequireNonEmpty(UpstreamRevision, "SELinux package upstream revision");
        if (!GitRevisionPattern().IsMatch(upstreamRevision))
        {
            throw new InvalidOperationException("SELinux package upstream revision must be a full lowercase Git commit");
        }

        if (Recipes.Count == 0)
        {
            throw new InvalidOperationException("SELinux package manifest must contain recipes");
        }

        var recipes = new Dictionary<string, SelinuxPackageRecipePlan>(StringComparer.Ordinal);
        var packageOwners = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var descriptor in Recipes)
        {
            var recipe = descriptor.ToPlan(root);
            if (!recipes.TryAdd(recipe.Name, recipe))
            {
                throw new InvalidOperationException("duplicate SELinux package recipe: " + recipe.Name);
            }

            foreach (var package in recipe.Packages)
            {
                if (!packageOwners.TryAdd(package, recipe.Name))
                {
                    throw new InvalidOperationException(
                        $"SELinux package {package} is produced by both {packageOwners[package]} and {recipe.Name}");
                }
            }
        }

        if (BuildOrder.Count == 0)
        {
            throw new InvalidOperationException("SELinux package manifest buildOrder must not be empty");
        }

        var buildOrder = BuildOrder.Select((name, index) =>
        {
            var safeName = RequireName(name, $"SELinux buildOrder item {index + 1}");
            return recipes.ContainsKey(safeName)
                ? safeName
                : throw new InvalidOperationException("SELinux buildOrder references unknown recipe: " + safeName);
        }).ToList();
        var missing = recipes.Keys.Where(name => !buildOrder.Contains(name, StringComparer.Ordinal)).Order(StringComparer.Ordinal).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException("SELinux buildOrder omits recipes: " + string.Join(", ", missing));
        }

        return new SelinuxPackageBuildPlan(upstreamRepository, upstreamRevision, recipes, buildOrder);
    }

    internal static string RequireName(string? value, string label)
    {
        var result = RequireNonEmpty(value, label);
        return SafeNamePattern().IsMatch(result)
            ? result
            : throw new InvalidOperationException(label + " must be a safe package name");
    }

    private static string RequireHttpsUrl(string? value, string label)
    {
        var result = RequireNonEmpty(value, label);
        return Uri.TryCreate(result, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps
            ? result
            : throw new InvalidOperationException(label + " must be an HTTPS URL");
    }

    private static string RequireNonEmpty(string? value, string label)
        => string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException(label + " is required")
            : value.Trim();
}

public sealed class SelinuxPackageRecipeDescriptor
{
    public string? Name { get; set; }

    public string? Path { get; set; }

    public List<string> Packages { get; set; } = [];

    public List<string> InstallPackages { get; set; } = [];

    public bool SkipCheck { get; set; }

    public SelinuxPackageRecipePlan ToPlan(string root)
    {
        var name = SelinuxPackageBuildDescriptor.RequireName(Name, "SELinux recipe name");
        var relativePath = RequireRelativePath(Path, name + " recipe path");
        var directory = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relativePath));
        var recipeRoot = System.IO.Path.Combine(root, "packaging", "arch", "selinux");
        if (!SecurityGuards.IsInsideDirectory(directory, recipeRoot) || !Directory.Exists(directory))
        {
            throw new InvalidOperationException($"SELinux recipe {name} must be an existing directory inside {recipeRoot}");
        }

        if (!File.Exists(System.IO.Path.Combine(directory, "PKGBUILD")))
        {
            throw new InvalidOperationException("SELinux recipe is missing PKGBUILD: " + name);
        }

        var packages = Packages.Select((package, index) =>
            SelinuxPackageBuildDescriptor.RequireName(package, $"{name} package {index + 1}")).ToList();
        if (packages.Count == 0 || packages.Count != packages.Distinct(StringComparer.Ordinal).Count())
        {
            throw new InvalidOperationException($"SELinux recipe {name} must declare unique package outputs");
        }

        var installPackages = InstallPackages.Select((package, index) =>
            SelinuxPackageBuildDescriptor.RequireName(package, $"{name} install package {index + 1}")).ToList();
        foreach (var package in installPackages)
        {
            if (!packages.Contains(package, StringComparer.Ordinal))
            {
                throw new InvalidOperationException($"SELinux recipe {name} cannot install undeclared output {package}");
            }
        }

        return new SelinuxPackageRecipePlan(name, directory, packages, installPackages, SkipCheck);
    }

    private static string RequireRelativePath(string? value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(label + " is required");
        }

        var path = value.Trim();
        return System.IO.Path.IsPathRooted(path) ||
               path == "." ||
               path == ".." ||
               path.StartsWith("../", StringComparison.Ordinal) ||
               path.Contains("/../", StringComparison.Ordinal) ||
               path.EndsWith("/..", StringComparison.Ordinal) ||
               path.Contains('\\')
            ? throw new InvalidOperationException(label + " must be a safe repository-relative path")
            : path;
    }
}
