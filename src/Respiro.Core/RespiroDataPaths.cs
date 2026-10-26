namespace Respiro.Core;

/// <summary>Resolve once per process. Never move journals, receipts or settings on startup.</summary>
public static class RespiroDataPaths
{
    static readonly Lazy<string> root = new(() => Resolve(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)));
    public static string Root => root.Value;
    internal static string Resolve(string local)
    {
        var current = Path.Combine(local, "Respiro");
        var legacy = Path.Combine(local, "LimpaPC");
        return Directory.Exists(legacy) && !Directory.Exists(current) ? legacy : current;
    }
    public static IReadOnlyList<string> ExistingRoots =>
        new[] { Root, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LimpaPC") }
        .Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists).ToArray();

    internal static bool IsTestArtifact(string path) => path.Split('\\', '/').Any(part =>
        part.StartsWith("Respiro.Missao", StringComparison.OrdinalIgnoreCase) ||
        part.StartsWith("LimpaPC.Missao", StringComparison.OrdinalIgnoreCase));
}
