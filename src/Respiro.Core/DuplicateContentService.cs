using System.Diagnostics;
using System.Security.Cryptography;
namespace Respiro.Core;

public sealed record DuplicateComparisonLimits(int MaximumFiles = 1500, long MaximumFileBytes = 8L * 1024 * 1024 * 1024,
    long MaximumReadBytes = 16L * 1024 * 1024 * 1024)
{
    public void Validate()
    {
        if (MaximumFiles < 2 || MaximumFiles > 1500 || MaximumFileBytes < 1 || MaximumFileBytes > 8L * 1024 * 1024 * 1024 ||
            MaximumReadBytes < 1 || MaximumReadBytes > 64L * 1024 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(MaximumFiles), "Limites de comparação inválidos.");
    }
}
public sealed record DuplicateProgress(int ProcessedFiles, int TotalFiles, long ReadBytes, string Path);
public sealed record DuplicateFileResult(string Path, long Bytes, string State, string? SHA256, string Group, string Detail)
{
    public string Size => Sizes.Format(Bytes);
}
public sealed record DuplicateComparisonReport(IReadOnlyList<DuplicateFileResult> Files, long ReadBytes, bool Cancelled, bool Limited)
{
    public DateTime ComparedAt { get; } = DateTime.UtcNow;
    public int ConfirmedFiles => Files.Count(f => f.Group.Length > 0);
    public string Summary => $"{ConfirmedFiles:N0} arquivos em grupos de mesmo conteúdo (SHA-256); " +
        $"{Sizes.Format(ReadBytes)} lidos. {(Cancelled ? "Cancelado; resultado parcial. " : "")}" +
        $"{(Limited ? "Limites atingidos; há arquivos não comparados. " : "")}" +
        "Confirmação vale no momento da leitura. Não autoriza exclusão nem representa espaço recuperável.";
}
/// <summary>Read-only, sequential SHA-256. Bounded input, one 64 KiB buffer, no recursion and no deletion.</summary>
public sealed class DuplicateContentService
{
    internal static void RequireReadablePersonalPath(string path)
    {
        LockedFile.Canonical(path);
        string[] protectedParts = [".git", ".codex", ".claude", "dev.multicodex.app", ".sdk", ".cargo", ".rustup",
            ".gradle", ".tmp", "output", "codexHub", "node_modules", "target", "sdk", "sdks", "docker", "wsl", "worktrees", ".worktrees", ".ssh", ".aws"];
        if (path.Split('\\').Any(p => protectedParts.Contains(p, StringComparer.OrdinalIgnoreCase) || p.StartsWith("MultiCodex", StringComparison.OrdinalIgnoreCase) || p.StartsWith("mxc-", StringComparison.OrdinalIgnoreCase)) ||
            path.Contains("e2.5", StringComparison.OrdinalIgnoreCase) || path.Contains("e2-5", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Área protegida de projeto, agente ou sistema; não comparada.");
        string name = Path.GetFileName(path);
        if (name.StartsWith(".env", StringComparison.OrdinalIgnoreCase) ||
            new[] { "credential", "secret", "private", "token", "auth" }.Any(p => name.Contains(p, StringComparison.OrdinalIgnoreCase)) ||
            new[] { ".db", ".sqlite", ".sqlite3", ".mdb", ".accdb", ".kdbx", ".key", ".pem", ".pfx", ".vhd", ".vhdx", ".vmdk", ".qcow2", ".dmp" }
                .Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            throw new IOException("Possível banco, credencial ou imagem de sistema; não comparado.");
    }
    public DuplicateComparisonReport Compare(IEnumerable<FileRow> source, DuplicateComparisonLimits? limits = null,
        IProgress<DuplicateProgress>? progress = null, CancellationToken token = default)
    {
        limits ??= new(); limits.Validate();
        var input = source.Take(limits.MaximumFiles + 1).ToArray();
        bool limited = input.Length > limits.MaximumFiles, cancelled = false;
        var files = input.Take(limits.MaximumFiles).DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToArray();
        var peers = files.Where(f => f.Bytes > 0).GroupBy(f => f.Bytes).Where(g => g.Count() > 1)
            .SelectMany(g => g).Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var rows = files.Select(f => new DuplicateFileResult(f.Path, f.Bytes, "Não comparado", null, "",
            "Sem outro arquivo do mesmo tamanho nesta lista; conteúdo não lido.")).ToArray();
        var buffer = new byte[65536]; long bytesRead = 0; int processed = 0;
        var clock = Stopwatch.StartNew(); long last = -1000;
        for (int index = 0; index < files.Length; index++)
        {
            var candidate = files[index];
            if (token.IsCancellationRequested) { cancelled = true; break; }
            if (!peers.Contains(candidate.Path)) { processed++; continue; }
            try
            {
                RequireReadablePersonalPath(candidate.Path);
                if (candidate.Bytes > limits.MaximumFileBytes) { limited = true; throw new IOException("Limite de tamanho por arquivo; conteúdo não lido."); }
                if (candidate.Bytes > limits.MaximumReadBytes - bytesRead) { limited = true; throw new IOException("Limite total de leitura; conteúdo não lido."); }
                using var file = LockedFile.Open(candidate.Path);
                file.RequireUnnamedStreamOnly();
                if (file.Stamp.Bytes != candidate.Bytes || file.Stamp.WriteTime != candidate.Modified.ToUniversalTime().ToFileTimeUtc())
                    throw new IOException("Arquivo mudou desde a análise; analise novamente.");
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long offset = 0;
                while (offset < file.Stamp.Bytes)
                {
                    token.ThrowIfCancellationRequested();
                    int count = RandomAccess.Read(file.Handle, buffer.AsSpan(0, (int)Math.Min(buffer.Length, file.Stamp.Bytes - offset)), offset);
                    if (count == 0) throw new IOException("Leitura incompleta.");
                    hash.AppendData(buffer, 0, count); offset += count; bytesRead += count;
                    if (clock.ElapsedMilliseconds - last >= 200)
                    { progress?.Report(new(processed, files.Length, bytesRead, candidate.Path)); last = clock.ElapsedMilliseconds; }
                }
                token.ThrowIfCancellationRequested();
                file.RequireUnchanged();
                rows[index] = rows[index] with { SHA256 = Convert.ToHexString(hash.GetHashAndReset()), Detail = "SHA-256 do conteúdo principal lido integralmente; arquivo e fluxos revalidados." };
            }
            catch (OperationCanceledException) { cancelled = true; break; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
            { rows[index] = rows[index] with { Detail = ex.Message }; }
            processed++;
            progress?.Report(new(processed, files.Length, bytesRead, candidate.Path));
        }
        int group = 0;
        foreach (var matches in rows.Select((row, index) => (row, index)).Where(x => x.row.SHA256 != null)
            .GroupBy(x => (x.row.Bytes, x.row.SHA256)).Where(g => g.Count() > 1))
        {
            string id = "Grupo " + ++group;
            foreach (var (_, index) in matches)
                rows[index] = rows[index] with { State = "Mesmo conteúdo (SHA-256)", Group = id };
        }
        for (int i = 0; i < rows.Length; i++)
        {
            var row = rows[i];
            if (row.SHA256 != null && row.Group.Length == 0)
                rows[i] = row with { State = rows.Any(r => r.Bytes == row.Bytes && r.SHA256 != null && r.SHA256 != row.SHA256)
                    ? "Conteúdo diferente" : "Comparação inconclusiva" };
            else if (cancelled && row.SHA256 == null && peers.Contains(row.Path) && row.Detail.StartsWith("Sem outro"))
                rows[i] = row with { Detail = "Comparação cancelada; conteúdo não confirmado." };
        }
        return new(Array.AsReadOnly(rows), bytesRead, cancelled, limited);
    }
}
