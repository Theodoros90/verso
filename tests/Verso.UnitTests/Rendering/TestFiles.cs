using System.Text.Json;
using Verso.Rendering;
using Verso.Rendering.Layout;

namespace Verso.UnitTests.Rendering;

internal static class TestFiles
{
    public static readonly PageSetup A4 = PageSetup.From("A4", "portrait", 18, 16, 18, 16);

    public static string Fixture(string relative) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", relative));

    public static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public static TemplateLayout ParseValid(string json)
    {
        var (layout, errors) = LayoutParser.Parse(json);
        Assert.True(layout is not null, "Layout should be valid but had errors: " + string.Join("; ", errors.Select(e => $"{e.Path}: {e.Message}")));
        return layout!;
    }

    /// <summary>
    /// Compares with Rendering/Golden/{name}.html in the source tree. To accept an intended change:
    /// <c>$env:VERSO_UPDATE_GOLDEN = "1"; dotnet test --filter Golden</c>, then review the diff.
    /// </summary>
    public static void AssertGolden(string name, string actual)
    {
        actual = actual.ReplaceLineEndings("\n");
        var path = Path.Combine(RepoRoot(), "tests", "Verso.UnitTests", "Rendering", "Golden", name + ".html");
        if (Environment.GetEnvironmentVariable("VERSO_UPDATE_GOLDEN") == "1")
        {
            File.WriteAllText(path, actual);
        }

        Assert.True(File.Exists(path), $"Missing golden file {path}. Generate it with VERSO_UPDATE_GOLDEN=1.");
        Assert.Equal(File.ReadAllText(path).ReplaceLineEndings("\n"), actual);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Verso.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Repository root (Verso.slnx) not found.");
    }
}
