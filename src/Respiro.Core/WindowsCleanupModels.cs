namespace Respiro.Core;
public enum WindowsCleanupKind { UserTemporary, WindowsTemporary, Thumbnails, DirectXShaders, ErrorReports, DeliveryOptimization, KnownLogs, RecycleBin, NpmCache }
public record WindowsCleanupCategory(WindowsCleanupKind Kind, string Name, bool SupportsRemoval, bool Administrator, string Source, string Risk, string Restriction)
{ public override string ToString() => Name + (Administrator ? " — administrativo" : ""); }
public record WindowsCleanupFile(WindowsCleanupKind Kind, string Path, long Bytes, long? Occupied, string Source, string Status)
{ public string Size => Sizes.Format(Bytes); public string OccupiedText => Occupied is long n ? Sizes.Format(n) : "Inconclusivo"; }
public record WindowsCleanupDiscovery(WindowsCleanupCategory Category, IReadOnlyList<WindowsCleanupFile> Files, long SeenFiles, long SeenBytes, long KnownOccupied, long Issues, bool Truncated, bool Cancelled, IReadOnlyList<string> Notices);
public sealed class WindowsCleanupItem
{
    public string Path { get; }
    public string OriginalPath { get; }
    public long Bytes { get; }
    public long? Occupied { get; }
    public string Size => Sizes.Format(Bytes);
    public string OccupiedText => Occupied is long n ? Sizes.Format(n) : "Inconclusivo";
    public long EstimatedRecoverable => Eligible ? Occupied ?? 0 : 0;
    public string RecoverableText => Sizes.Format(EstimatedRecoverable);
    public bool Eligible => Stamp != null;
    public string Source { get; }
    public string Decision { get; }
    public string Risk { get; }
    public string Permissions { get; }
    internal FileStamp? Stamp { get; }
    internal string[] Ancestors { get; }
    internal string? Hash { get; }
    internal string? CompanionPath { get; }
    internal FileStamp? CompanionStamp { get; }
    internal string? CompanionHash { get; }
    internal WindowsCleanupItem(string path, long bytes, long? occupied, string original, string source, string decision, string risk, string permissions, FileStamp? stamp = null, string[]? ancestors = null, string? hash = null, string? companion = null, FileStamp? companionStamp = null, string? companionHash = null)
    { Path = path; Bytes = bytes; Occupied = occupied; OriginalPath = original; Source = source; Decision = decision; Risk = risk; Permissions = permissions; Stamp = stamp; Ancestors = ancestors ?? []; Hash = hash; CompanionPath = companion; CompanionStamp = companionStamp; CompanionHash = companionHash; }
}
public sealed class WindowsCleanupPlan
{
    public Guid Id { get; } = Guid.NewGuid();
    public DateTime Created { get; }
    public WindowsCleanupCategory Category { get; }
    public IReadOnlyList<WindowsCleanupItem> Items { get; }
    public int EligibleCount => Items.Count(i => i.Eligible);
    public long EstimatedRecoverable => Items.Sum(i => i.EstimatedRecoverable);
    public string Confirmation => Category.Kind == WindowsCleanupKind.RecycleBin ? $"ESVAZIAR {EligibleCount}" : $"LIMPAR {EligibleCount}";
    internal WindowsCleanupPlan(WindowsCleanupCategory category, List<WindowsCleanupItem> items, DateTime created) { Category = category; Items = items.AsReadOnly(); Created = created; }
}
public record WindowsCleanupResult(string State, IReadOnlyList<CleanupOutcome> Items, long EstimatedReleasedBytes, bool Simulated)
{
    public long? FreeBefore { get; init; }
    public long? FreeAfter { get; init; }
    public long? ObservedFreeDelta => FreeBefore is long before && FreeAfter is long after ? after-before : null;
    public string FreeDeltaText => ObservedFreeDelta is long n ? (n>=0?"+":"−")+Sizes.Format(Math.Abs(n))+" livres no volume (inclui atividade de outros processos)" : "Não medido — simulação ou volume inconclusivo";
}
public static class WindowsCleanupCatalog
{
    public static IReadOnlyList<WindowsCleanupCategory> Categories { get; } = Array.AsReadOnly(new[]
    {
        new WindowsCleanupCategory(WindowsCleanupKind.NpmCache, "Cache npm — downloads públicos verificados", true, false, "cacache content-v2/sha512 + index-v5 com checksum e origem pública", "Exige rede para downloads futuros; instalações offline podem falhar. Índices, npmrc, node_modules e pacotes locais não são removidos.", "Somente downloads com SHA-512 e índice vigente: tarballs gzip ou metadados packument públicos sem escopo com estrutura verificada. Tarballs com escopo exigem prova HTTPS anônima opcional. Configurações, pacotes privados e conteúdo desconhecido permanecem bloqueados."),
        new WindowsCleanupCategory(WindowsCleanupKind.UserTemporary, "Temporários do usuário", true, false, "Pasta temporária do usuário + estrutura MINIDUMP", "Perda de evidências de falha; cópias de memória podem conter dados sensíveis.", "Somente minidumps estruturais com 30 dias ou mais. Arquivos .tmp genéricos permanecem bloqueados."),
        new WindowsCleanupCategory(WindowsCleanupKind.WindowsTemporary, "Temporários do Windows", true, true, "Windows\\Temp + estrutura MINIDUMP", "Perda de diagnóstico de falhas de serviços.", "Somente minidumps antigos validados; exige token administrativo já existente. Sem elevação."),
        new WindowsCleanupCategory(WindowsCleanupKind.Thumbnails, "Cache de miniaturas", true, false, "Cache central do Explorer + formato CMMM", "Miniaturas serão recriadas; pode haver trabalho adicional ao abrir pastas.", "Somente caches de dados CMMM versão 32, estrutura válida e sem uso. SQLite, índices e outros bancos são bloqueados."),
        new WindowsCleanupCategory(WindowsCleanupKind.DirectXShaders, "Shaders DirectX", false, false, "D3DSCache / mecanismo gerenciado pelo Windows", "Recompilação pode causar pausas em jogos.", "Bloqueado: a API de limpeza por categoria não fornece inventário congelado com revalidação individual; não remover .dxcache diretamente."),
        new WindowsCleanupCategory(WindowsCleanupKind.ErrorReports, "Relatórios de erro e dumps antigos", true, false, "CrashDumps / WER ReportArchive / Windows Minidump", "Perda permanente de evidências de falha.", "Minidumps estruturais antigos; relatórios WER e anexos sem prova suficiente permanecem bloqueados. Áreas do sistema podem exigir administrador."),
        new WindowsCleanupCategory(WindowsCleanupKind.DeliveryOptimization, "Otimização de Entrega", false, true, "Get-DeliveryOptimizationPerfSnap / Delete-DeliveryOptimizationCache", "Novos downloads poderão ser necessários.", "Bloqueado: o comando oficial limpa o cache do serviço sem lista individual verificável; nunca usar Force ou IncludePinnedFiles."),
        new WindowsCleanupCategory(WindowsCleanupKind.KnownLogs, "Logs temporários conhecidos", false, true, "Windows Logs / Setup Log Files", "Perda de evidências de atualização e instalação.", "Bloqueado: idade e nome não provam finalização nem ausência de dependências. Nenhum .log/.etl genérico é apagado."),
        new WindowsCleanupCategory(WindowsCleanupKind.RecycleBin, "Lixeira — arquivos individuais", true, false, "Lixeira do SID atual / metadados Windows $I versão 2", "Remoção permanente: o item deixa de poder ser restaurado pela Lixeira.", "Somente arquivos individuais originalmente temporários, com par de metadados válido. Pastas inteiras, dados pessoais, Downloads, projetos e origens inconclusivas são bloqueados.")
    });
}

