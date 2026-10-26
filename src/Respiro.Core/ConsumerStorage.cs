namespace Respiro.Core;

public sealed record QuickCleanupOption(string Name, WindowsCleanupKind? Kind, string Explanation, string Consequences, string OfficialId)
{
    public string Route => Kind is null || !WindowsCleanupCatalog.Categories.Single(c => c.Kind == Kind).SupportsRemoval
        ? "Revisar no Windows" : "Selecionar arquivos no Respiro";
}
public static class QuickCleanupCatalog
{
    public static IReadOnlyList<QuickCleanupOption> Options { get; } = Array.AsReadOnly(new[]
    {
        new QuickCleanupOption("Miniaturas de fotos e vídeos", WindowsCleanupKind.Thumbnails, "Imagens pequenas que o Explorador cria para mostrar suas fotos e vídeos. Os originais são preservados.", "As miniaturas serão recriadas; pastas podem abrir mais devagar na primeira vez.", "temporary"),
        new QuickCleanupOption("Relatórios de falhas antigos", WindowsCleanupKind.ErrorReports, "Somente dumps de falhas com formato verificado e pelo menos 30 dias. Relatórios e anexos desconhecidos ficam protegidos.", "Os arquivos removidos não poderão mais ajudar a investigar essas falhas.", "temporary"),
        new QuickCleanupOption("Temporários do usuário", WindowsCleanupKind.UserTemporary, "Verifica dumps antigos. Estar na pasta Temp, ter extensão .tmp ou ser antigo não basta para remover.", "Apenas evidências de falha comprovadas podem ser removidas; outros temporários exigem revisão no Windows.", "temporary"),
        new QuickCleanupOption("Temporários do Windows", WindowsCleanupKind.WindowsTemporary, "Verificação restrita de dumps antigos do Windows. Áreas sem permissão ficam inconclusivas.", "O Respiro não pede elevação nem altera permissões.", "temporary"),
        new QuickCleanupOption("Itens temporários da Lixeira", WindowsCleanupKind.RecycleBin, "Somente arquivos individuais com origem temporária comprovada. Seus arquivos pessoais não são selecionados.", "Excluir da Lixeira é permanente e exige confirmação individual. Para outros itens, revise a Lixeira do Windows.", "temporary"),
        new QuickCleanupOption("Cache de shaders DirectX", WindowsCleanupKind.DirectXShaders, "O Windows decide quais shaders podem ser descartados. O Respiro não remove esse cache diretamente.", "Jogos podem ter pausas enquanto os shaders são recriados.", "temporary"),
        new QuickCleanupOption("Atualizações e instalações anteriores", null, "Revise os tamanhos e as categorias apresentados pelo Windows. Nenhum tamanho é presumido aqui.", "Remover uma instalação anterior pode impedir voltar à versão anterior.", "updates"),
        new QuickCleanupOption("Temporários de instalação e caches de aplicativos", null, "Revisão pelo Windows; instaladores em andamento e caches sem prova de origem não são descartados pelo Respiro.", "Pode ser necessário baixar arquivos novamente. Downloads pessoais precisam de revisão separada.", "temporary")
    });
}

public sealed record QuickCleanupAnalysis(WindowsCleanupDiscovery Discovery, IReadOnlyDictionary<string, long> Verified,
    int EvaluatedFiles, bool Partial)
{
    public long EstimatedRecoverable => Verified.Values.Sum();
    public string Amount => Sizes.Format(EstimatedRecoverable) + " estimados / revalidados em simulação";
    public string State => Partial ? "Parcial — há itens não verificados" : Verified.Count > 0 ? "Disponível para revisão" : "Nenhum item elegível verificado";
}
/// <summary>Discovery and simulation only. Never returns an executable plan or approves deletion.</summary>
public sealed class QuickCleanupAnalyzer
{
    readonly WindowsCleanupService service;
    public QuickCleanupAnalyzer() : this(new WindowsCleanupService()) { }
    internal QuickCleanupAnalyzer(WindowsCleanupService service) => this.service = service;
    public QuickCleanupAnalysis Analyze(WindowsCleanupKind kind, IProgress<string>? progress = null, CancellationToken token = default)
    {
        if (!QuickCleanupCatalog.Options.Any(o => o.Kind == kind) ||
            !WindowsCleanupCatalog.Categories.Single(c => c.Kind == kind).SupportsRemoval)
            throw new IOException("Categoria disponível somente para orientação ou na seção avançada.");
        var discovery = service.Discover(kind, progress, token);
        token.ThrowIfCancellationRequested();
        // Name only narrows work; Prepare still proves eligibility, content, origin and use.
        bool Candidate(WindowsCleanupFile f) => kind switch
        {
            WindowsCleanupKind.Thumbnails => Path.GetFileName(f.Path).StartsWith("thumbcache_", StringComparison.OrdinalIgnoreCase),
            WindowsCleanupKind.RecycleBin => Path.GetFileName(f.Path).StartsWith("$R", StringComparison.Ordinal),
            _ => new[] { ".dmp", ".mdmp" }.Contains(Path.GetExtension(f.Path), StringComparer.OrdinalIgnoreCase)
        };
        var paths = discovery.Files.Where(Candidate).Take(1000).Select(f => f.Path).ToArray();
        var plan = service.Prepare(kind, paths, token, progress);
        var simulation = service.Run(plan, true, token: token);
        var verified = simulation.Items.Where(i => i.Status == "Simulado").Select(i => i.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var allocations = plan.Items.Where(i => i.Eligible && verified.Contains(i.Path))
            .GroupBy(i => i.Path, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Max(i => i.EstimatedRecoverable), StringComparer.OrdinalIgnoreCase);
        bool partial = discovery.Cancelled || discovery.Truncated || discovery.Issues > 0 ||
            discovery.Files.Count > paths.Length || simulation.Items.Any(i => i.Status != "Simulado");
        return new(discovery, new System.Collections.ObjectModel.ReadOnlyDictionary<string, long>(allocations), paths.Length, partial);
    }
    public static long Total(IEnumerable<QuickCleanupAnalysis> analyses) =>
        analyses.SelectMany(a => a.Verified).GroupBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Sum(g => g.Max(p => p.Value));
}

public enum PersonalFileKind { All, Videos, Downloads, Installers, Archives, PossibleDuplicates, OldModification }
public static class PersonalFileExplorer
{
    // Metadata filters only: neither similarity nor age grants permission to delete.
    public static IReadOnlyList<FileRow> Filter(IEnumerable<FileRow> source, PersonalFileKind kind, long minimumBytes, DateTime now, string? downloads = null)
    {
        var files = source.Where(f => f.Bytes >= minimumBytes).ToArray();
        bool Extension(FileRow f, params string[] values) => values.Contains(Path.GetExtension(f.Path), StringComparer.OrdinalIgnoreCase);
        var duplicates = files.Where(f => f.Bytes > 0).GroupBy(f => (Path.GetFileName(f.Path).ToUpperInvariant(), f.Bytes))
            .Where(g => g.Count() > 1).SelectMany(g => g).Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string? root = downloads is null ? null : Path.GetFullPath(downloads).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return files.Where(f => kind switch
        {
            PersonalFileKind.Videos => Extension(f, ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".m4v"),
            PersonalFileKind.Downloads => root != null && Path.GetFullPath(f.Path).StartsWith(root, StringComparison.OrdinalIgnoreCase),
            PersonalFileKind.Installers => Extension(f, ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".exe"),
            PersonalFileKind.Archives => Extension(f, ".zip", ".7z", ".rar", ".tar", ".gz", ".iso"),
            PersonalFileKind.PossibleDuplicates => duplicates.Contains(f.Path),
            PersonalFileKind.OldModification => f.Modified.ToUniversalTime() <= now.ToUniversalTime().AddDays(-180),
            _ => true
        }).OrderByDescending(f => f.Bytes).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToArray();
    }
}
