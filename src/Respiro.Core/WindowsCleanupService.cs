using System.Runtime.InteropServices;
using System.Text.Json;
namespace Respiro.Core;
public sealed class WindowsCleanupService
{
    const long MaximumFileBytes = 2L * 1024 * 1024 * 1024;
    readonly WindowsCleanupEnvironment environment;
    WindowsCleanupPlan? pending;
    NpmCacheProof? npm; readonly NpmPublicProof? publicNpmProof;
    public WindowsCleanupService() : this(WindowsCleanupEnvironment.Production()) { }
    public WindowsCleanupService(bool allowPublicNpmProbe) : this(WindowsCleanupEnvironment.Production(),allowPublicNpmProbe ? new NpmPublicProof() : null) { }
    internal WindowsCleanupService(WindowsCleanupEnvironment environment,NpmPublicProof? publicNpmProof=null) { this.environment = environment; this.publicNpmProof=publicNpmProof; }
    public int PublicNpmRequests => publicNpmProof?.RequestCount ?? 0;
    public IReadOnlyList<WindowsCleanupCategory> Categories => WindowsCleanupCatalog.Categories;
    WindowsCleanupCategory Category(WindowsCleanupKind kind) => Categories.Single(c => c.Kind == kind);
    public WindowsCleanupDiscovery Discover(WindowsCleanupKind kind, IProgress<string>? progress = null, CancellationToken token = default)
    {
        var category = Category(kind); var retained = new List<WindowsCleanupFile>(); var generic = new PriorityQueue<WindowsCleanupFile, long>();
        var notices = new List<string>(); long files = 0, bytes = 0, occupied = 0, issues = 0; bool truncated = false, cancelled = false;
        var fs = new WindowsFileSystem(); var clock = System.Diagnostics.Stopwatch.StartNew(); long last = 0;
        void Issue(string p, string reason) { issues++; if (notices.Count < 100) notices.Add(p + ": " + reason); }
        foreach (var root in environment.Roots[kind])
        {
            var stack = new Stack<IEnumerator<DiskEntry>>();
            try
            {
                token.ThrowIfCancellationRequested();
                for (string? p = root; p != null; p = Path.GetDirectoryName(p)) if (File.GetAttributes(p).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Reparse point na raiz ou ancestral; não percorrido.");
                WindowsCleanupEnvironment.ProtectPath(root);
                stack.Push(fs.Enumerate(root).GetEnumerator());
                while (stack.Count > 0)
                {

                    token.ThrowIfCancellationRequested(); var iterator = stack.Peek(); DiskEntry e;
                    try { if (!iterator.MoveNext()) { stack.Pop().Dispose(); continue; } e = iterator.Current; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Issue(root, ex.Message); stack.Pop().Dispose(); continue; }
                    if (e.Attributes.HasFlag(FileAttributes.ReparsePoint)) { Issue(e.Path, "Link/junction/placeholder ignorado."); continue; }
                    if (e.Directory && !environment.Fixture && RespiroDataPaths.IsTestArtifact(e.Path)) continue;
                    if (e.Directory)
                    {
                        if (kind == WindowsCleanupKind.RecycleBin) { if (Path.GetFileName(e.Path).StartsWith("$R")) retained.Add(new(kind, e.Path, 0, null, category.Source, "Pasta reciclada bloqueada; não há exclusão de diretório.")); if (retained.Count > 8000) { truncated = true; retained.RemoveAt(retained.Count - 1); } continue; }
                        if (kind == WindowsCleanupKind.Thumbnails) continue;
                        try { stack.Push(fs.Enumerate(e.Path).GetEnumerator()); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Issue(e.Path, ex.Message); } continue;
                    }
                    string name = Path.GetFileName(e.Path);
                    if (kind == WindowsCleanupKind.RecycleBin && !name.StartsWith("$R", StringComparison.Ordinal)) continue;
                    files++; bytes += e.Length;
                    Allocation a; try { a = fs.Allocated(e.Path); } catch { a = new(null, null); }
                    if (a.Bytes is long n) occupied += n;
                    bool recognizedName = kind == WindowsCleanupKind.NpmCache || kind == WindowsCleanupKind.RecycleBin || kind == WindowsCleanupKind.Thumbnails && name.StartsWith("thumbcache_", StringComparison.Ordinal) || Path.GetExtension(name).Equals(".dmp", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(name).Equals(".mdmp", StringComparison.OrdinalIgnoreCase);
                    var row = new WindowsCleanupFile(kind, e.Path, e.Length, a.Bytes, category.Source, category.SupportsRemoval && recognizedName ? "Candidato à verificação de origem, formato, proteção e uso; não autorizado." : "Bloqueado: " + category.Restriction);
                    if (recognizedName && retained.Count < 8000) retained.Add(row);
                    else { if (recognizedName) truncated = true; generic.Enqueue(row, e.Length); if (generic.Count > 300) generic.Dequeue(); }
                    if (clock.ElapsedMilliseconds - last > 250) { progress?.Report($"{category.Name}: {files:N0} arquivos, {Sizes.Format(bytes)} observados · {e.Path}"); last = clock.ElapsedMilliseconds; }
                }
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (OperationCanceledException) { cancelled = true; break; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Issue(root, ex.Message); }
            finally { while (stack.Count > 0) stack.Pop().Dispose(); }
        }
        retained.AddRange(generic.UnorderedItems.Select(x => x.Element));
        if (kind == WindowsCleanupKind.RecycleBin)
        {
            var query = new BinInfo { Size = (uint)Marshal.SizeOf<BinInfo>() };
            int hr = SHQueryRecycleBin(Path.GetPathRoot(environment.Roots[kind][0]), ref query);
            notices.Add(hr == 0 ? $"Shell do Windows: {Sizes.Format(query.Bytes)} em {query.Count} itens na Lixeira deste volume. Inclui pastas e itens protegidos; NÃO é espaço autorizado." : $"Consulta Shell inconclusiva: 0x{hr:X8}.");
        }
        notices.Add("Nada foi selecionado. Até 8.000 candidatos e 300 outros arquivos são exibidos; diretórios não são excluídos. Total observado não é potencial aprovado.");
        return new(category, retained.OrderByDescending(f => f.Bytes).ToArray(), files, bytes, occupied, issues, truncated, cancelled, notices.AsReadOnly());
    }
    public WindowsCleanupPlan Prepare(WindowsCleanupKind kind, IEnumerable<string> explicitlySelected, CancellationToken token = default,IProgress<string>? progress=null)
    {
        var category = Category(kind); var items = new List<WindowsCleanupItem>();var clock=System.Diagnostics.Stopwatch.StartNew();long last=0;
        foreach (var path in explicitlySelected.Distinct(StringComparer.Ordinal).Take(1001))
        {
            token.ThrowIfCancellationRequested();if(clock.ElapsedMilliseconds-last>250){progress?.Report($"{category.Name}: verificando item {items.Count+1:N0} · {path}");last=clock.ElapsedMilliseconds;} if (items.Count == 1000) throw new IOException("Selecione até 1.000 arquivos por operação.");
            long bytes = 0; long? occupied = null;
            try
            {
                RequireCategory(category); environment.RequireScope(kind, path);
                using var file = LockedFile.Open(path, deletion: true); bytes = file.Stamp.Bytes;
                file.RequireUnnamedStreamOnly(); occupied = file.AllocationSize();
                var proof = Validate(kind, path, file, token);
                string hash = file.Hash(token, MaximumFileBytes);
                items.Add(new(path, bytes, occupied, proof.Original, proof.Source, "Elegível mediante seleção e confirmação desta categoria; formato, identidade, fluxos NTFS, alocação e uso verificados.", category.Risk, Permission(category), file.Stamp, file.Ancestors.ToArray(), hash, proof.Companion, proof.CompanionStamp, proof.CompanionHash));
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
            { items.Add(new(path, bytes, occupied, "", category.Source, "Bloqueado: " + e.Message, category.Risk, Permission(category))); }
        }
        return pending = new(category, items, environment.Clock.GetUtcNow().UtcDateTime);
    }
    internal Action<int>? BeforeItemForTest;
    internal Action<string>? BeforeAuditForTest;
    public WindowsCleanupResult Run(WindowsCleanupPlan plan, bool simulate, bool categoryApproved = false, string confirmation = "", CancellationToken token = default, IReadOnlySet<string>? recycleConfirmedPaths = null)
    {
        if (!ReferenceEquals(plan, pending) || environment.Clock.GetUtcNow().UtcDateTime - plan.Created > TimeSpan.FromMinutes(5)) throw new IOException("Prévia expirada, inválida ou já usada.");
        if (!simulate && (!categoryApproved || confirmation != plan.Confirmation || plan.EligibleCount == 0)) throw new IOException("Aprovação individual da categoria e confirmação exata são obrigatórias.");
        if (!simulate && plan.Category.Kind == WindowsCleanupKind.RecycleBin && (recycleConfirmedPaths == null || plan.Items.Where(i => i.Eligible).Any(i => !recycleConfirmedPaths.Contains(i.Path)))) throw new IOException("Cada item da Lixeira exige confirmação individual.");
        if (!simulate) { pending = null; publicNpmProof?.BeginRealExecution(); }
        long? freeBefore = simulate ? null : Available(plan);
        var outcomes = new List<CleanupOutcome>(); long released = 0; int index = 0; bool cancelled = false;
        foreach (var item in plan.Items)
        {
            BeforeItemForTest?.Invoke(index++);
            if (token.IsCancellationRequested) { cancelled = true; outcomes.Add(new(item.Path, "Não executada", 0, "Cancelamento antes deste item e dos seguintes.")); break; }
            if (!item.Eligible) { outcomes.Add(new(item.Path, "Não executada", 0, item.Decision)); if (!simulate) TryAudit("Blocked", plan, item, item.Decision); continue; }
            bool removed = false;
            try
            {
                RequireCategory(plan.Category); environment.RequireScope(plan.Category.Kind, item.Path);
                using (var file = LockedFile.Open(item.Path, deletion: !simulate))
                {
                    if (file.Stamp != item.Stamp || !file.Ancestors.SequenceEqual(item.Ancestors)) throw new IOException("Arquivo ou ancestrais mudaram após a prévia.");
                    file.RequireUnnamedStreamOnly(); if (file.AllocationSize() != item.Occupied) throw new IOException("Ocupação mudou; gere nova prévia.");
                    var proof = Validate(plan.Category.Kind, item.Path, file, token);
                    if (proof.Original != item.OriginalPath || proof.Companion != item.CompanionPath || proof.CompanionStamp != item.CompanionStamp || proof.CompanionHash != item.CompanionHash || file.Hash(token, MaximumFileBytes) != item.Hash) throw new IOException("Conteúdo, origem ou metadados mudaram após a prévia.");
                    file.RequireUnnamedStreamOnly();
                    using var companion = item.CompanionPath != null ? LockedFile.Open(item.CompanionPath, deletion: !simulate && plan.Category.Kind == WindowsCleanupKind.RecycleBin) : null;
                    if (companion != null && (companion.Stamp != item.CompanionStamp || companion.Hash(token) != item.CompanionHash)) throw new IOException("Metadados da Lixeira mudaram na revalidação final.");
                    companion?.RequireUnnamedStreamOnly();

                    token.ThrowIfCancellationRequested();
                    if (!simulate)
                    {
                        Audit("Intent", plan, item, "Revalidado; commit pelo mesmo handle; sem alteração de permissões.");
                        file.MarkForDeletion(); removed = true;
                        if (companion != null && plan.Category.Kind == WindowsCleanupKind.RecycleBin) companion.MarkForDeletion();
                    }
                }
                if (!simulate) Audit("Removed", plan, item, "Concluído");
                if (removed) released += item.EstimatedRecoverable;
                outcomes.Add(new(item.Path, simulate ? "Simulado" : "Concluída", removed ? item.Bytes : 0, simulate ? "Verificações repetidas; nenhum arquivo removido." : "Arquivo selecionado removido; alocação anterior é apenas estimativa, não diferença garantida no espaço livre."));
            }
            catch (OperationCanceledException) { cancelled = true; outcomes.Add(new(item.Path, "Não executada", 0, "Cancelado antes do commit.")); break; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or System.ComponentModel.Win32Exception)
            {
                if (removed) released += item.EstimatedRecoverable;
                outcomes.Add(new(item.Path, removed ? "Parcial" : "Não executada", removed ? item.Bytes : 0, e.Message)); if (!simulate) TryAudit(removed ? "Partial" : "Failed", plan, item, e.Message);
            }
        }
        if (simulate && plan.Items.Count>0)
        {
            try { Audit("SimulationSummary",plan,plan.Items[0],$"Itens={outcomes.Count}; verificados={outcomes.Count(o=>o.Status=="Simulado")}; cancelada={cancelled}; falhas="+string.Join(" | ",outcomes.Where(o=>o.Status!="Simulado").Take(3).Select(o=>o.Path+": "+o.Detail))); }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
            { for(int i=0;i<outcomes.Count;i++) if(outcomes[i].Status=="Simulado") outcomes[i]=new(outcomes[i].Path,"Não executada",0,"Verificação sem registro durável; recuperável não confirmado: "+ex.Message); }
        }
        int success = outcomes.Count(o => o.Status is "Concluída" or "Simulado");
        string state = simulate ? success == outcomes.Count && !cancelled && success > 0 ? "Simulada" : "Simulação parcial / não executada" : success == plan.Items.Count && success > 0 && !cancelled ? "Concluída" : success > 0 || released > 0 || outcomes.Any(o => o.Status == "Parcial") ? "Parcial" : "Não executada";
        return new(state + (cancelled ? " — cancelada" : ""), outcomes.AsReadOnly(), released, simulate) { FreeBefore = freeBefore, FreeAfter = simulate ? null : Available(plan) };
    }
    static long? Available(WindowsCleanupPlan plan) { try { return plan.Items.Count>0 ? new DriveInfo(Path.GetPathRoot(plan.Items[0].Path)!).AvailableFreeSpace : null; } catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; } }
    void RequireCategory(WindowsCleanupCategory category)
    {
        if (!category.SupportsRemoval) throw new IOException(category.Restriction);
        if (category.Administrator && !environment.Administrator) throw new IOException("Categoria administrativa: requer token administrativo já existente. Nenhuma elevação automática.");
    }
    static string Permission(WindowsCleanupCategory category) => category.Administrator ? "Administrador já elevado; sem elevação automática." : "Usuário atual; acesso de leitura e exclusão deve ser concedido pelo Windows. Áreas do sistema podem ser negadas.";
    sealed record Proof(string Original, string Source, string? Companion = null, FileStamp? CompanionStamp = null, string? CompanionHash = null);
    Proof Validate(WindowsCleanupKind kind, string path, LockedFile file, CancellationToken token)
    {
        DateTime now = environment.Clock.GetUtcNow().UtcDateTime;
        if (kind == WindowsCleanupKind.NpmCache)
        {
            npm ??= new NpmCacheProof(environment.Roots[kind][0],publicNpmProof);
            var reference = npm.Find(path, token);
            using var index = LockedFile.Open(reference.Index); index.RequireUnnamedStreamOnly();
            var current = npm.Read(index, reference.Index);
            if (current == null || current != reference) throw new IOException("Entrada npm mudou ou foi invalidada; gere nova prévia.");
            npm.Verify(reference, file, token);
            return new(reference.Origin, reference.Packument ? "Metadados públicos npm sem escopo: nome, versões e tags coerentes; índice e SHA-512 íntegros. Nenhum script executado." : "Tarball gzip npm: SHA-512, tamanho e índice íntegros; pacotes com escopo exigem checksum público por HEAD anônimo.", reference.Index, index.Stamp, index.Hash(token));
        }
        if (kind == WindowsCleanupKind.Thumbnails)
        { environment.RequireThumbnailRegistration(); WindowsArtifactFormats.Thumbnail(file, path, token); return new("", "Cache central do Explorer; CMMM v32 e entradas estruturais validadas."); }
        if (kind == WindowsCleanupKind.RecycleBin)
        {
            string name = Path.GetFileName(path);
            if (!name.StartsWith("$R", StringComparison.Ordinal) || name.Length <= 2) throw new IOException("Não é um payload individual da Lixeira.");
            string metadataPath = Path.Combine(Path.GetDirectoryName(path)!, "$I" + name[2..]);
            using var metadata = LockedFile.Open(metadataPath); metadata.RequireUnnamedStreamOnly();
            var recycled = WindowsArtifactFormats.RecycleMetadata(metadata, now);
            if (recycled.Bytes != file.Stamp.Bytes) throw new IOException("Tamanho do payload diverge dos metadados Windows.");
            environment.RequireOriginalTemporary(recycled.Original); WindowsArtifactFormats.RejectProtectedContent(file);
            return new(recycled.Original, "Metadados $I v2 do Windows + payload $R compatível; origem temporária individual, fora de áreas protegidas.", metadataPath, metadata.Stamp, metadata.Hash(token));
        }
        string ext = Path.GetExtension(path);
        if (!(ext.Equals(".dmp", StringComparison.OrdinalIgnoreCase) || ext.Equals(".mdmp", StringComparison.OrdinalIgnoreCase))) throw new IOException("Sem adaptador de formato validado para este arquivo. Nome, extensão ou idade isolados não autorizam limpeza.");
        WindowsArtifactFormats.Minidump(file, now, token);
        return new("", "MINIDUMP_HEADER e diretório de streams validados; SystemInfoStream presente; datas interna e de modificação com 30 dias ou mais.");
    }
    void TryAudit(string action, WindowsCleanupPlan plan, WindowsCleanupItem item, string detail) { try { Audit(action, plan, item, detail); } catch { } }
    void Audit(string action, WindowsCleanupPlan plan, WindowsCleanupItem item, string detail)
    {
        BeforeAuditForTest?.Invoke(action);
        Directory.CreateDirectory(Path.GetDirectoryName(environment.Journal)!);
        using var log = LockedFile.Open(environment.Journal, write: true, create: !File.Exists(environment.Journal));
        if (log.Stamp.Bytes > 4 * 1024 * 1024) throw new IOException("Registro de limpeza Windows cheio; arquive manualmente antes de continuar.");
        var line = JsonSerializer.SerializeToUtf8Bytes(new { At = environment.Clock.GetUtcNow(), plan.Id, Category = plan.Category.Name, Action = action, item.Path, item.OriginalPath, item.Bytes, item.Occupied, Detail = detail });
        RandomAccess.Write(log.Handle, line.Concat(new byte[] { 10 }).ToArray(), log.Stamp.Bytes); RandomAccess.FlushToDisk(log.Handle);
    }
    [StructLayout(LayoutKind.Sequential, Pack = 8)] struct BinInfo { public uint Size; public long Bytes, Count; }
    [DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, EntryPoint = "SHQueryRecycleBinW")] static extern int SHQueryRecycleBin(string? root, ref BinInfo info);
}



