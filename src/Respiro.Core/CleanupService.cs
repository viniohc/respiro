using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace Respiro.Core;

public sealed class OwnedTemporaryStore
{
    public const string Category = "Temporários comprovados do Respiro";
    internal static readonly byte[] Header = Encoding.UTF8.GetBytes("LimpaPC.TEMP/1\n");
    public string Root { get; }
    public string JournalPath { get; }
    public OwnedTemporaryStore() : this(Path.Combine(RespiroDataPaths.Root, "Scratch"), null) { }
    internal OwnedTemporaryStore(string root, string? journal)
    {
        Root = LockedFile.Canonical(Path.GetFullPath(root));
        JournalPath = journal ?? Path.Combine(Path.GetDirectoryName(Root)!, "cleanup.jsonl");
    }
    public string CreateDiagnosticSnapshot(MaintenanceReport report) => CreateDiagnosticSnapshot(JsonSerializer.Serialize(report));
    // Only Respiro-generated, reproducible diagnostic snapshots belong in this store.
    internal string CreateDiagnosticSnapshot(string generatedJson)
    {
        Directory.CreateDirectory(Root);
        string id = Guid.NewGuid().ToString("N"); string path = Path.Combine(Root, id + ".tmp");
        var payload = Header.Concat(Encoding.UTF8.GetBytes(generatedJson)).ToArray();
        if (payload.Length > 64 * 1024 * 1024) throw new IOException("Snapshot temporário excede 64 MiB.");
        // Bound only new, reproducible snapshots. Existing files and receipts are never rotated or deleted.
        long existingBytes = 0; int count = 0;
        foreach (string existing in Enumerate())
        {
            existingBytes = checked(existingBytes + new FileInfo(existing).Length);
            if (++count >= 100 || existingBytes + payload.Length > 16L * 1024 * 1024)
                throw new IOException("Área de cópias de trabalho atingiu 100 arquivos ou 16 MiB. Revise os temporários próprios pela limpeza manual.");
        }
        if (payload.Length > 16 * 1024 * 1024) throw new IOException("Cópia de trabalho excede a capacidade de 16 MiB.");
        using (var file = LockedFile.Open(path, write: true, create: true)) RandomAccess.Write(file.Handle, payload, 0);
        var receipt = new TemporaryReceipt(1, id, Convert.ToHexString(SHA256.HashData(payload)), payload.Length);
        var data = JsonSerializer.SerializeToUtf8Bytes(receipt);
        using (var file = LockedFile.Open(path + ".receipt.json", write: true, create: true)) RandomAccess.Write(file.Handle, data, 0);
        return path;
    }
    public IEnumerable<string> Enumerate()
    {
        if (!Directory.Exists(Root)) yield break;
        for (string? parent = Root; parent != null; parent = Path.GetDirectoryName(parent))
            if (File.GetAttributes(parent).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Área própria ou ancestral é link/junction; listagem bloqueada.");
        foreach (var f in new DirectoryInfo(Root).EnumerateFiles("*.tmp", SearchOption.TopDirectoryOnly))
            if (!f.Attributes.HasFlag(FileAttributes.ReparsePoint)) yield return f.FullName;
    }
    internal void CheckPath(string path)
    {
        path = LockedFile.Canonical(path);
        if (!string.Equals(Path.GetDirectoryName(path), Root, StringComparison.Ordinal)) throw new IOException("Fora da categoria autorizada; somente arquivos diretos da área do Respiro.");
        if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out _) || Path.GetExtension(path) != ".tmp") throw new IOException("Nome não corresponde a temporário gerado pelo Respiro.");
        if (path.Contains("e2.5", StringComparison.OrdinalIgnoreCase) || path.Contains("e2-5", StringComparison.OrdinalIgnoreCase)) throw new IOException("Artefato sob proteção E2.5.");
    }
    internal TemporaryReceipt VerifyReceipt(string path, LockedFile target, CancellationToken token)
    {
        CheckPath(path);
        // Receipt is also opened without traversing reparse points and with ancestors pinned.
        using var source = LockedFile.Open(path + ".receipt.json");
        var receipt = JsonSerializer.Deserialize<TemporaryReceipt>(source.ReadSmall()) ?? throw new IOException("Comprovante inválido.");
        if (receipt.Schema != 1 || receipt.Id != Path.GetFileNameWithoutExtension(path) || receipt.Bytes != target.Stamp.Bytes) throw new IOException("Comprovante não corresponde ao arquivo.");
        var head = new byte[Header.Length];
        if (RandomAccess.Read(target.Handle, head, 0) != head.Length || !head.SequenceEqual(Header)) throw new IOException("Conteúdo sem assinatura de formato do Respiro.");
        if (target.Hash(token) != receipt.Hash) throw new IOException("Conteúdo diverge do comprovante; preservado.");
        return receipt;
    }
}
internal record TemporaryReceipt(int Schema, string Id, string Hash, long Bytes);
public sealed class CleanupItem
{
    public string Path { get; }
    public long Bytes { get; }
    public string Size => Sizes.Format(Bytes);
    public string OccupiedText => "Não consultado neste modo";
    public string RecoverableText => "Estimativa lógica: " + Size;
    public string OriginalPath => Path;
    public string Source => OwnedTemporaryStore.Category;
    public string Risk => "Remoção permanente da cópia de trabalho.";
    public string Permissions => "Usuário atual; acesso de exclusão obrigatório.";
    public bool Eligible { get; }
    public string Decision { get; }
    internal FileStamp? Stamp { get; }
    internal string? Hash { get; }
    internal string[] Ancestors { get; }
    internal CleanupItem(string path, long bytes, string decision, FileStamp? stamp = null, string? hash = null, string[]? ancestors = null)
    { Path = path; Bytes = bytes; Decision = decision; Stamp = stamp; Hash = hash; Ancestors = ancestors ?? []; Eligible = stamp != null; }
}
public sealed class CleanupPlan
{
    public Guid Id { get; } = Guid.NewGuid();
    public DateTime Created { get; } = DateTime.UtcNow;
    public IReadOnlyList<CleanupItem> Items { get; }
    public long EligibleBytes => Items.Where(i => i.Eligible).Sum(i => i.Bytes);
    public int EligibleCount => Items.Count(i => i.Eligible);
    public string Confirmation => $"APAGAR {EligibleCount}";
    internal CleanupPlan(List<CleanupItem> items) { Items = items.AsReadOnly(); }
}
public record CleanupOutcome(string Path, string Status, long RemovedBytes, string Detail);
public sealed class CleanupService
{
    readonly OwnedTemporaryStore store;
    readonly long journalLimit;
    public CleanupService(OwnedTemporaryStore store) : this(store, 4 * 1024 * 1024) { }
    internal CleanupService(OwnedTemporaryStore store, long journalLimit) { this.store = store; this.journalLimit = journalLimit; }
    CleanupPlan? pending;
    public CleanupPlan Prepare(IEnumerable<string> explicitlySelected, CancellationToken token = default)
    {
        var items = new List<CleanupItem>();
        foreach (var path in explicitlySelected.Distinct(StringComparer.OrdinalIgnoreCase).Take(1001))
        {
            token.ThrowIfCancellationRequested();
            if (items.Count >= 1000) throw new IOException("Selecione até 1.000 arquivos por operação.");
            try
            {
                store.CheckPath(path);
                using var file = LockedFile.Open(path, deletion: true);
                var receipt = store.VerifyReceipt(path, file, token);
                var age = DateTime.UtcNow - DateTime.FromFileTimeUtc(file.Stamp.WriteTime);
                if (age < TimeSpan.FromDays(7)) throw new IOException("Temporário recente: aguardar pelo menos sete dias.");
                items.Add(new(path, file.Stamp.Bytes, "Elegível somente após aprovação desta categoria e confirmação explícita. Remoção permanente, sem Lixeira.", file.Stamp, receipt.Hash, file.Ancestors.ToArray()));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or System.ComponentModel.Win32Exception)
            { items.Add(new(path, 0, "Bloqueado: " + e.Message)); }
        }
        return pending = new(items);
    }
    public IReadOnlyList<CleanupOutcome> Run(CleanupPlan plan, bool simulate, bool categoryApproved = false, string confirmation = "", CancellationToken token = default)
    {
        if (!ReferenceEquals(pending, plan) || DateTime.UtcNow - plan.Created > TimeSpan.FromMinutes(5)) throw new IOException("Prévia expirada ou inválida; gere uma nova.");
        if (!simulate && (!categoryApproved || confirmation != plan.Confirmation || plan.EligibleCount == 0)) throw new IOException("Aprovação de categoria e confirmação exata são obrigatórias.");
        if (!simulate) pending = null; // One-use plan: failed items need a new preview too.
        var outcomes = new List<CleanupOutcome>();
        foreach (var item in plan.Items)
        {
            if (token.IsCancellationRequested) { outcomes.Add(new(item.Path, "Cancelado", 0, "Cancelamento solicitado; itens seguintes não executados.")); break; }
            if (!item.Eligible) { outcomes.Add(new(item.Path, "Bloqueado", 0, item.Decision)); continue; }
            bool removed = false;
            try
            {
                store.CheckPath(item.Path);
                using (var file = LockedFile.Open(item.Path, deletion: !simulate))
                {
                    if (file.Stamp != item.Stamp || !file.Ancestors.SequenceEqual(item.Ancestors)) throw new IOException("Identidade, metadados ou ancestral mudaram após a prévia.");
                    if (store.VerifyReceipt(item.Path, file, token).Hash != item.Hash) throw new IOException("Comprovante mudou após a prévia.");
                    if (!simulate)
                    {
                        Journal("Intent", plan, item, "Revalidado; remoção por handle");
                        file.MarkForDeletion();
                    }
                }
                if (!simulate) removed = true; // Handle closed successfully; no path-based deletion.
                Journal(simulate ? "Simulated" : "Removed", plan, item, "Concluído");
                outcomes.Add(new(item.Path, simulate ? "Simulado" : "Removido", removed ? item.Bytes : 0, simulate ? "Revalidação aprovada; nenhum byte removido." : "Arquivo temporário próprio removido permanentemente; comprovante preservado."));
            }
            catch (OperationCanceledException) { outcomes.Add(new(item.Path, "Cancelado", 0, "Nenhuma remoção deste item.")); break; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or System.ComponentModel.Win32Exception)
            {
                outcomes.Add(new(item.Path, removed ? "Removido; falha no registro final" : "Bloqueado", removed ? item.Bytes : 0, e.Message));
                try { Journal("Failed", plan, item, e.Message); } catch { /* No deletion is permitted without a durable intent record. */ }
            }
        }
        return outcomes.AsReadOnly();
    }
    void Journal(string action, CleanupPlan plan, CleanupItem item, string detail)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(store.JournalPath)!);
        using var file = LockedFile.Open(store.JournalPath, write: true, create: !File.Exists(store.JournalPath));
        if (file.Stamp.Bytes > journalLimit) throw new IOException("Registro de limpeza cheio; exporte e arquive manualmente antes de remover mais itens.");
        var line = JsonSerializer.SerializeToUtf8Bytes(new { At = DateTime.UtcNow, Plan = plan.Id, Action = action, item.Path, item.Bytes, detail });
        RandomAccess.Write(file.Handle, line.Concat(new byte[] { 10 }).ToArray(), file.Stamp.Bytes);
        RandomAccess.FlushToDisk(file.Handle);
    }
}






