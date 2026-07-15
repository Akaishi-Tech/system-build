namespace HomeHarbor.Tooling;

internal static class ProductBuildEnvironment
{
    internal static string? Optional(SystemImageProductPlan product, string suffix)
    {
        var generic = Environment.GetEnvironmentVariable("ARCH_AB_" + suffix);
        if (!string.IsNullOrWhiteSpace(generic))
        {
            return generic.Trim();
        }

        var productValue = Environment.GetEnvironmentVariable(product.EnvironmentPrefix + "_" + suffix);
        return string.IsNullOrWhiteSpace(productValue) ? null : productValue.Trim();
    }

    internal static string String(SystemImageProductPlan product, string suffix, string defaultValue)
        => Optional(product, suffix) ?? defaultValue;

    internal static bool Flag(SystemImageProductPlan product, string suffix)
    {
        var value = Optional(product, suffix);
        return value is not null && value.Trim().ToLowerInvariant() is "1" or "true" or "yes" or "on";
    }
}
