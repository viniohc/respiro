using System.Security.Principal;
using Microsoft.Win32;
namespace Respiro.Core;
internal sealed class WindowsCleanupEnvironment
{
    internal Dictionary<WindowsCleanupKind, string[]> Roots { get; }
    internal string Journal { get; }
    internal bool Fixture { get; }
    internal TimeProvider Clock { get; }
    internal bool Administrator { get; }
    internal WindowsCleanupEnvironment(Dictionary<WindowsCleanupKind, string[]> roots, string journal, bool fixture = false, TimeProvider? clock = null, bool? administrator = null)
    { Roots = roots; Journal = journal; Fixture = fixture; Clock = clock ?? TimeProvider.System; Administrator = administrator ?? new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator); }
    internal static WindowsCleanupEnvironment Production()
    {
        string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows), local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string volume = Path.GetPathRoot(win)!, sid = WindowsIdentity.GetCurrent().User!.Value;
        return new(new()
        {
            [WindowsCleanupKind.NpmCache] = [Path.Combine(local, "npm-cache", "_cacache", "content-v2", "sha512")],
            [WindowsCleanupKind.UserTemporary] = [Path.GetTempPath().TrimEnd('\\')],
            [WindowsCleanupKind.WindowsTemporary] = [Path.Combine(win, "Temp")],
            [WindowsCleanupKind.Thumbnails] = [Path.Combine(local, "Microsoft", "Windows", "Explorer")],
            [WindowsCleanupKind.DirectXShaders] = [Path.Combine(local, "D3DSCache")],
            [WindowsCleanupKind.ErrorReports] = [Path.Combine(local, "CrashDumps"), Path.Combine(win, "Minidump"), Path.Combine(local, "Microsoft", "Windows", "WER", "ReportArchive"), Path.Combine(common, "Microsoft", "Windows", "WER", "ReportArchive")],
            [WindowsCleanupKind.DeliveryOptimization] = [Path.Combine(win, "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache")],
            [WindowsCleanupKind.KnownLogs] = [Path.Combine(win, "Logs")],
            [WindowsCleanupKind.RecycleBin] = [Path.Combine(volume, "$Recycle.Bin", sid)]
        }, Path.Combine(RespiroDataPaths.Root, "windows-cleanup.jsonl"));
    }
    internal void RequireScope(WindowsCleanupKind kind, string path)
    {
        path = LockedFile.Canonical(path); ProtectPath(path);
        if (!Fixture && RespiroDataPaths.IsTestArtifact(path)) throw new IOException("Fixture de teste excluída da limpeza de produção.");
        if (!Roots[kind].Any(root => path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.Ordinal))) throw new IOException("Fora do escopo exato do adaptador.");
        if (kind is WindowsCleanupKind.Thumbnails or WindowsCleanupKind.RecycleBin && !Roots[kind].Contains(Path.GetDirectoryName(path), StringComparer.Ordinal)) throw new IOException("Somente arquivos diretos desta categoria; diretórios completos não são removidos.");
    }
    internal static void ProtectPath(string path)
    {
        var parts = path.Split('\\');
        var forbidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".git", ".ssh", ".aws", "Downloads", "Documents", "Desktop", "Pictures", "Videos", "Music", "node_modules", "target", "sdk", "sdks", "avd", ".android", ".gradle", "docker", "wsl", "worktrees", ".worktrees" };
        if (parts.Any(forbidden.Contains) || path.Contains("e2.5", StringComparison.OrdinalIgnoreCase) || path.Contains("e2-5", StringComparison.OrdinalIgnoreCase)) throw new IOException("Área pessoal, de desenvolvimento, credenciais ou E2.5 protegida.");
        string name = Path.GetFileName(path);
        if (name.StartsWith(".env", StringComparison.OrdinalIgnoreCase) || new[] { "credential", "token", "auth", "secret", "private" }.Any(s => name.Contains(s, StringComparison.OrdinalIgnoreCase))) throw new IOException("Possível credencial ou configuração: preservado.");
    }
    internal void RequireOriginalTemporary(string original)
    {
        if (!Fixture && RespiroDataPaths.IsTestArtifact(original)) throw new IOException("Fixture de teste na origem: item da Lixeira preservado.");
        ProtectPath(original); LockedFile.Canonical(original);
        var allowed = Roots[WindowsCleanupKind.UserTemporary].Concat(Roots[WindowsCleanupKind.WindowsTemporary]);
        if (!allowed.Any(r => original.StartsWith(r.TrimEnd('\\') + "\\", StringComparison.Ordinal))) throw new IOException("Origem original fora das áreas temporárias aprovadas; dados pessoais e Downloads preservados.");
        var protectedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".kdbx", ".kdb", ".accdb", ".dbf", ".rdb", ".cfg", ".conf", ".cer", ".crt", ".asc", ".ipynb", ".vue", ".svelte", ".hpp", ".cxx", ".csproj", ".vcxproj", ".fs", ".fsx", ".lock" };
        if (protectedExtensions.Contains(Path.GetExtension(original))) throw new IOException("Código, banco, configuração ou credencial na origem: preservado.");
        var c = new Classifier(new WindowsFileSystem()).Classify(new(original, false, 0, 0, DateTime.UtcNow));
        if (c.Nature == Nature.Protegido) throw new IOException("Formato de origem protegido: " + c.Reason);
        // A missing ancestor cannot prove absence of a Git/protection marker.
        for (var p = Path.GetDirectoryName(original); p != null; p = Path.GetDirectoryName(p))
        {
            if (!Directory.Exists(p)) throw new IOException("Ancestral original ausente: dependências/proteções inconclusivas.");
            if (File.GetAttributes(p).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Origem original passa por reparse point.");
            foreach (var marker in new[] { ".git", "AGENTS.md", ".limpapc-protect", ".no-clean", "E2.5", "E2-5" }) if (File.Exists(Path.Combine(p, marker)) || Directory.Exists(Path.Combine(p, marker))) throw new IOException("Proteção na origem original: " + marker);
        }
    }
    internal void RequireThumbnailRegistration()
    {
        if (Fixture) return;
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\VolumeCaches\Thumbnail Cache");
        if (!string.Equals(key?.GetValue("") as string, "{889900c3-59f3-4c2f-ae21-a409ea01e605}", StringComparison.OrdinalIgnoreCase)) throw new IOException("Provedor de miniaturas do Windows não reconhecido.");
    }
}




