using KeelMatrix.CliContract.Core;
using Xunit;

namespace KeelMatrix.CliContract.Tests;

public sealed class NormalizationTests
{
    [Fact]
    public void OpenCliCanonicalizesFixture()
    {
        var input = File.ReadAllText(Fixture("opencli", "phase0.json"));
        var manifest = Normalizer.Normalize("opencli", input);
        var deploy = manifest.Root.Subcommands.Single(c => c.Path == "root / deploy");

        Assert.Equal("1.0.0-alpha.14", manifest.SourceVersion);
        Assert.Equal("--region", deploy.Options.Single().Name);
        Assert.Equal(["-r"], deploy.Options.Single().Aliases);
        Assert.Equal(["eu", "us"], deploy.Options.Single().AllowedValues);
        Assert.Equal("us", deploy.Options.Single().DefaultValue!.GetValue<string>());
        Assert.Contains("🚀", deploy.Summary);
        Assert.Contains(manifest.Root.Subcommands, command => command.Path == "root / empty");
    }

    [Fact]
    public void OpenCliSourceOrderAndLineEndingsDoNotChangeBytes()
    {
        var first = File.ReadAllText(Fixture("opencli", "phase0.json"));
        var second = File.ReadAllText(Fixture("opencli", "phase0-reordered.json"));
        Assert.Equal(Normalizer.Serialize(Normalizer.Normalize("opencli", first)), Normalizer.Serialize(Normalizer.Normalize("opencli", second.Replace("\n", "\r\n"))));

        var yaml = File.ReadAllText(Fixture("opencli", "phase0.yaml"));
        Assert.Equal(Normalizer.Serialize(Normalizer.Normalize("opencli", first)), Normalizer.Serialize(Normalizer.Normalize("opencli", yaml)));
    }

    [Fact]
    public void DotnetSchemaNormalizesNestedCommandsAndOptions()
    {
        var input = File.ReadAllText(Fixture("dotnet", "synthetic-minimal.json"));
        var manifest = Normalizer.Normalize("dotnet", input);
        var command = manifest.Root.Subcommands.Single(c => c.Path == "root / deploy").Subcommands.Single(c => c.Path == "root / deploy / region");

        Assert.Equal("10.0.401", manifest.SourceVersion);
        Assert.Equal("--format", command.Options.Single().Name);
        Assert.Equal(["-f"], command.Options.Single().Aliases);
        Assert.Equal(1, command.Options.Single().ArityMinimum);
    }

    [Fact]
    public void UnsupportedOpenCliVersionFailsClosed()
    {
        var input = File.ReadAllText(Fixture("opencli", "phase0.json"));
        var changed = input.Replace(Normalizer.OpenCliVersion, "1.0.0-alpha.13", StringComparison.Ordinal);
        var error = Assert.Throws<NormalizationException>(() => Normalizer.Normalize("opencli", changed));
        Assert.Equal("UNSUPPORTED_OPENCLI_VERSION", error.Code);
    }

    [Fact]
    public void LargeInputIsRejectedWithoutEchoingContent()
    {
        var secret = new string('x', 101);
        var input = "{\"opencliVersion\":\"1.0.0-alpha.14\",\"info\":{\"title\":\"t\",\"binary\":\"b\",\"version\":\"1\"},\"commands\":{\"b\":{\"description\":\"" + secret + "\"}}}";
        var error = Assert.Throws<NormalizationException>(() => Normalizer.Normalize("opencli", input, new NormalizationLimits(MaxStringLength: 100)));
        Assert.Equal("STRING_TOO_LARGE", error.Code);
        Assert.DoesNotContain(secret, error.Message, StringComparison.Ordinal);
    }

    private static string Fixture(params string[] parts)
    {
        return Path.GetFullPath(Path.Combine([AppContext.BaseDirectory, "..", "..", "..", "..", "..", "fixtures", .. parts]));
    }
}
