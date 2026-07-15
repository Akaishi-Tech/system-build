using System.CommandLine;
using System.CommandLine.Invocation;
using System.Runtime.CompilerServices;
using System.Text.Json;
using HomeHarbor.Tooling;

[assembly: InternalsVisibleTo("HomeHarbor.Tests")]

try
{
    return await ImageBuilderProgram.RunAsync(args, CancellationToken.None);
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex.Message);
    return ex is ArgumentException ? 2 : 1;
}

internal static partial class ImageBuilderProgram
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        var parseResult = CreateRootCommand().Parse(args);
        var exitCode = await parseResult.InvokeAsync(
            new InvocationConfiguration { EnableDefaultExceptionHandler = false },
            cancellationToken);
        return parseResult.Errors.Count > 0 && exitCode != 0 ? 2 : exitCode;
    }

    private static Argument<string> RequiredArgument(string name, string description)
        => new(name) { Description = description };

    private static Argument<string> OptionalArgument(string name, string defaultValue, string description)
        => new(name)
        {
            Arity = ArgumentArity.ZeroOrOne,
            DefaultValueFactory = _ => defaultValue,
            Description = description
        };

    private static void AddKernelChannelValidator(Argument<string> argument)
    {
        argument.Validators.Add(result =>
        {
            try
            {
                _ = KernelChannel.Require(result.GetValueOrDefault<string>(), "kernel package build channel");
            }
            catch (InvalidOperationException ex)
            {
                result.AddError(ex.Message);
            }
        });
    }
}
