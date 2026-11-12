using System.Runtime.CompilerServices;

namespace IdempotentApi.UnitTests.Support;

/// <summary>
/// Approval files under <c>Golden/</c>. Set <c>UPDATE_GOLDEN=1</c> to rewrite them, then review the diff like code:
/// error bodies are part of the public contract clients parse.
/// </summary>
internal static class Golden
{
    public static void Verify(string name, string actual, [CallerFilePath] string callerPath = "")
    {
        var sourceDir = Path.Combine(Path.GetDirectoryName(callerPath)!, "Golden");
        var sourceFile = Path.Combine(sourceDir, name);
        actual = actual.ReplaceLineEndings("\n");

        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(sourceDir);
            File.WriteAllText(sourceFile, actual);
            return;
        }

        var outputFile = Path.Combine(AppContext.BaseDirectory, "Golden", name);
        File.Exists(outputFile).Should().BeTrue($"golden file {name} must exist (run with UPDATE_GOLDEN=1 to create it)");
        actual.Should().Be(File.ReadAllText(outputFile).ReplaceLineEndings("\n"), $"output must match Golden/{name}");
    }
}
