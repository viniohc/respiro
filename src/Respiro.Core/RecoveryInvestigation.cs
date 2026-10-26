using System.Diagnostics;
using System.Text;
using System.Text.Json;
namespace Respiro.Core;
public enum RecoveryClass { Official = 1, Regenerable = 2, Review = 3, Protected = 4 }
public sealed record RecoveryScope(string Path, string Origin, RecoveryClass Class, string Reason, string Impact, string Procedure);
public sealed class RecoveryGroup
{
    public RecoveryScope Scope { get; init; } = null!;
    public string Path => Scope.Path;
    public string Origin => Scope.Origin;
    public string Classification => Scope.Class switch { RecoveryClass.Official=>"1 · Mecanismo oficial disponível", RecoveryClass.Regenerable=>"2 · Regenerável após verificações", RecoveryClass.Review=>"3 · Revisão individual", _=>"4 · Protegido / inconclusivo" };
    public long LogicalBytes { get; set; }
    public long PhysicalBytes { get; set; }
    public long ConfirmedBytes { get; set; }
    public long ProtectedFileBytes { get; set; }
    public long ProtectedFileCount { get; set; }
    public long Files { get; set; }
    public long UnknownAllocation { get; set; }
    public long Issues { get; set; }
    public long SkippedLinks { get; set; }
    public long SharedFiles { get; set; }
    public DateTime? LastWriteUtc { get; set; }
    public long? GrowthBytes { get; set; }
    public DateTime? GrowthBaselineUtc { get; set; }
    public double? GrowthObservedHours { get; set; }
    public string ProcessEvidence { get; set; } = "Inconclusivo: processos ainda não consultados.";
    public string Checks { get; set; } = "Sem autorização de remoção; revisão individual necessária.";
    public bool Partial { get; set; }
    public long ProtectedBytes => Scope.Class==RecoveryClass.Protected ? PhysicalBytes : Math.Min(Math.Max(0,PhysicalBytes-ConfirmedBytes),ProtectedFileBytes);
    public long PendingReviewBytes => Math.Max(0,PhysicalBytes-ConfirmedBytes-ProtectedBytes);
    public string Physical => (Partial||UnknownAllocation>0?"≥ ":"")+Sizes.Format(PhysicalBytes);
    public string Confirmed => Sizes.Format(ConfirmedBytes);
    public string Pending => Sizes.Format(PendingReviewBytes);
    public string Protected => Sizes.Format(ProtectedBytes);
    public string LastActivity => LastWriteUtc?.ToLocalTime().ToString("dd/MM/yyyy HH:mm")??"Não disponível";
    public string Growth => GrowthBytes is long n ? (n>=0?"+":"−")+Sizes.Format(Math.Abs(n))+(GrowthObservedHours is double hours ? $" em {hours:N1} h observadas" : " desde a última medição comparável") : "Primeira medição ou escopo parcial; sem tendência comprovada";
}
public sealed class RecoveryReport
{
    public DateTime Started { get; init; }=DateTime.Now;
    public DateTime Finished { get; set; }
    public List<RecoveryGroup> Groups { get; set; }=[];
    [System.Text.Json.Serialization.JsonObjectCreationHandling(System.Text.Json.Serialization.JsonObjectCreationHandling.Populate)]
    public List<string> Notices { get; }=[];
    public long ConfirmedBytes => Groups.Sum(g=>g.ConfirmedBytes);
    public long IdentifiedPhysicalBytes => Groups.Sum(g=>g.PhysicalBytes);
    public long PendingReviewBytes => Groups.Sum(g=>g.PendingReviewBytes);
    public long ProtectedBytes => Groups.Sum(g=>g.ProtectedBytes);
    public long ProtectedFileCount => Groups.Sum(g=>g.ProtectedFileCount);
    public long VolumeUsed { get; set; }
    public long VolumeFree { get; set; }
    public bool Cancelled { get; set; }
    public bool RealDeletionExecuted => false;
    public bool PublicNpmProbeAllowed { get; set; }
    public int PublicHeadRequests { get; set; }
    public DockerStorageDiagnostic? DockerStorage { get; set; }
    public WindowsCleanupResult? NpmSimulation { get; set; }
    public List<WindowsCleanupItem> NpmPreview { get; }=[];
    public long PeakWorkingSet { get; set; }
}
public sealed class RecoveryInvestigationService(IReadOnlyFileSystem fs)
{
    const int IdentityLimit=500000;
    static IEnumerable<DirectoryInfo> SafeDirectories(string path,int limit)
    {
        for(string? p=Path.GetFullPath(path);p!=null;p=Path.GetDirectoryName(p))if(File.GetAttributes(p).HasFlag(FileAttributes.ReparsePoint))throw new IOException("Diretório/ancestral é link; descoberta bloqueada.");
        return new DirectoryInfo(path).EnumerateDirectories().Take(limit);
    }
    public static IReadOnlyList<RecoveryScope> DefaultScopes(bool wholeDisk=true)
    {
        string user=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), local=Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), win=Environment.GetFolderPath(Environment.SpecialFolder.Windows), volume=Path.GetPathRoot(win)!;
        var scopes=new List<RecoveryScope>();
        void Add(string path,string origin,RecoveryClass c,string reason,string impact,string procedure){path=Path.GetPathRoot(path)==path?path:path.TrimEnd('\\');var entry=new RecoveryScope(path,origin,c,reason,impact,procedure);int index=scopes.FindIndex(s=>s.Path.Equals(path,StringComparison.OrdinalIgnoreCase));if(index<0)scopes.Add(entry);else scopes[index]=entry;}
        if(wholeDisk)
        {
            Add(volume,"Arquivos da raiz do Windows",RecoveryClass.Protected,"Paginação e arquivos de sistema não são temporários descartáveis.","Remoção pode afetar o funcionamento do Windows.","Somente diagnóstico; consultar Manutenção do Windows.");
            foreach(var dir in SafeDirectories(volume,1000))
                Add(dir.FullName,Path.GetFileName(dir.FullName),RecoveryClass.Protected,"Pasta geral sem adaptador de limpeza específico; links e áreas sem acesso não são contornados.","Pode conter programas ou dados únicos.","Investigar pela árvore de armazenamento; não apagar a pasta inteira.");
        }
        if(wholeDisk)
        {
            void ProtectedScope(string p,string name)=>Add(p,name,RecoveryClass.Protected,"Dados pessoais, programas ou projetos sem adaptador de descarte validado.","Pode perder dados únicos, código, credenciais ou configuração.","Investigar individualmente; nenhum conteúdo deste escopo é automaticamente removível.");
            foreach(var dir in SafeDirectories(user,1000))ProtectedScope(dir.FullName,"Usuário / "+dir.Name);
            foreach(var part in new[]{"Local","LocalLow","Roaming"})
            {
                string p=Path.Combine(user,"AppData",part);ProtectedScope(p,"AppData / "+part);
                try{foreach(var dir in SafeDirectories(p,500))ProtectedScope(dir.FullName,part+" / "+dir.Name);}catch(IOException){}catch(UnauthorizedAccessException){}
            }
            foreach(var part in new[]{"dev\\personal","dev\\work"})
            {
                string p=Path.Combine(user,part);ProtectedScope(p,"Projetos / "+part);
                try{foreach(var dir in SafeDirectories(p,300))ProtectedScope(dir.FullName,"Projeto / "+dir.Name);}catch(IOException){}catch(UnauthorizedAccessException){}
            }
            ProtectedScope(Path.Combine(user,"Documents","ChatGPT","multicodex-worktrees"),"MultiCodex / worktrees preservadas");
            ProtectedScope(Path.Combine(user,".codex","worktrees"),"Codex / worktrees preservadas");
        }
        Add(Path.GetTempPath().TrimEnd('\\'),"Temporários do usuário — demais arquivos",RecoveryClass.Review,"Temp contém projetos, perfis de navegador e dados de agentes; nome e idade não provam descarte.","Perda de sessões, evidências e dados únicos.","Revisar por produtor; usar ferramentas Windows somente para categorias explicitamente aprovadas.");
        try { foreach(var dir in SafeDirectories(Path.GetTempPath(),2000))
            {
                Add(dir.FullName,"Temp / "+dir.Name,RecoveryClass.Review,"Origem ainda não comprovada; última gravação não prova inatividade.","Pode conter código, perfis e estado de agentes.","Revisar conteúdo e produtor; nunca apagar apenas pelo nome.");
                if(dir.Attributes.HasFlag(FileAttributes.ReparsePoint))continue;
                foreach(var suffix in new[]{"src-tauri\\target","target"})
                {
                    string target=Path.Combine(dir.FullName,suffix);
                    if(File.Exists(Path.Combine(target,"CACHEDIR.TAG")))Add(target,"Rust/Tauri / Temp / "+dir.Name,RecoveryClass.Review,"Tag Cargo encontrada; repositório, manifesto, catálogo de artefatos e dependências ainda precisam ser confirmados.","Perda de build e evidências; recompilação necessária.","Revisar checks do repositório/processos/target compartilhado; cargo clean --dry-run somente após resolver bloqueios, sem limpar o diretório inteiro.");
                }
            } }catch(IOException){}catch(UnauthorizedAccessException){}
        foreach (var agent in new[] { ".codex", ".claude" })
            Add(Path.Combine(user, agent), "Agentes / " + agent, RecoveryClass.Protected,
                "Histórico, bancos, sessões, configurações e credenciais; somente metadados.",
                "Remoção pode perder trabalho e evidências.", "Revisar retenção no produtor; nenhum arquivo autorizado.");
        Add(RespiroDataPaths.Root, "Respiro / registros e preferências", RecoveryClass.Protected,
            "Registros de operações e comprovantes precisam ser preservados.", "Perda de auditoria e preferências.",
            "Histórico mostra também registros legados; arquivamento é manual.");
        Add(Path.Combine(RespiroDataPaths.Root, "Scratch"), "Respiro / cópias de trabalho", RecoveryClass.Review,
            "Somente cabeçalho de compatibilidade, recibo e hash podem comprovar a cópia.",
            "Remoção permanente da cópia de diagnóstico.", "Limpeza → Temporários comprovados do Respiro → listar → selecionar → simular; revalidação obrigatória.");
        string project=Path.Combine(user,"Documents","ChatGPT","codexHub");
        Add(project,"MultiCodex / fontes e estado",RecoveryClass.Protected,"Código, dados, configurações e histórico do projeto; preservação obrigatória.","Perda de trabalho e estado do projeto.","Não limpar; investigar somente artefatos específicos com prova de regeneração e respeito à E2.5.");
        foreach(var pair in new[]{(".tmp","MultiCodex / .tmp"),("output","MultiCodex / output"),("src-tauri\\target","MultiCodex / build Rust/Tauri"),("node_modules","MultiCodex / dependências Node")})
            Add(Path.Combine(project,pair.Item1),pair.Item2,RecoveryClass.Protected,"Estado Git, worktrees, uso, dependências e restrições E2.5 precisam de validação específica; nenhum artefato é liberado automaticamente.","Risco de perder evidências, estado de desenvolvimento ou artefatos em uso.","Bloqueado nesta missão. Exige análise do responsável e separação de evidências protegidas antes de qualquer proposta.");
        Add(Path.Combine(local,"Microsoft","Windows","Explorer"),"Windows / miniaturas verificadas",RecoveryClass.Regenerable,"Caches centrais CMMM reconhecidos; arquivos em uso e variantes desconhecidas permanecem bloqueados.","Miniaturas serão recriadas.","Limpeza → Cache de miniaturas → prévia → simular; confirmar somente os arquivos elegíveis.");
        Add(Path.Combine(local,"npm-cache","_cacache","content-v2","sha512"),"npm / downloads públicos SHA-512",RecoveryClass.Regenerable,"Somente bytes com origem, índice vigente, checksum, SHA-512 e uso revalidados podem ser confirmados.","Requer rede nas próximas instalações; prejudica uso offline.","Limpeza → Cache npm → selecionar arquivos → prévia → simular; remoção somente após aprovação futura.");
        Add(Path.Combine(user,".gradle","caches"),"Gradle / caches compartilhados",RecoveryClass.Official,"Gradle mantém retenção e locks próprios; não há purge de pasta pelo Respiro.","Recompilação/download; caches compartilhados por projetos ativos.","Revisar versão e política de retenção do Gradle; deixar o mecanismo de manutenção oficial atuar. Não executar gradlew de projeto apenas para limpar.");
        Add(Path.Combine(user,".cargo","registry","cache"),"Cargo / pacotes baixados",RecoveryClass.Official,"Downloads compartilhados; manutenção oficial depende da versão do Cargo e histórico de uso.","Downloads futuros e trabalho offline podem ser afetados.","Consultar a coleta automática de cache suportada pela versão do Cargo. Recursos manuais dependem da versão e podem ser experimentais. Não iniciar builds para provocar limpeza nem apagar .cargo completo.");
        Add(Path.Combine(user,".cargo","registry","src"),"Cargo / fontes de dependências",RecoveryClass.Review,"Podem conter modificações locais; fontes não são liberadas por serem cache.","Possíveis alterações únicas em dependências.","Comparar com pacotes originais e revisar individualmente; não remover nesta versão.");
        foreach(var pair in new[]{(".android\\avd","Android / AVDs"),(".rustup","Rust / SDKs e toolchains"),(".gradle\\wrapper","Gradle / distribuições"),("AppData\\Local\\Android\\Sdk","Android / SDK"),(".docker","Docker / estado e configuração")})
            Add(Path.Combine(user,pair.Item1),pair.Item2,RecoveryClass.Protected,"SDK, emulador, distribuição ou estado persistente; diagnóstico não autoriza exclusão.","Pode impedir desenvolvimento ou perder estado.","Inventariar ferramentas/AVDs; revisão individual no gerenciador oficial, sem exclusão automática.");
        Add(Path.Combine(local,"Docker","wsl"),"Docker / discos e volumes WSL",RecoveryClass.Protected,"Discos virtuais persistentes contêm camadas, containers, volumes e possíveis bancos. Nenhuma exclusão autorizada.","Perda de dados e ambientes; excluir o VHDX destrói o armazenamento Docker.","Consultar diagnóstico do daemon local. Não excluir, montar ou compactar o VHDX nesta missão.");
        Add(Path.Combine(local,"wsl"),"WSL / discos de distribuições",RecoveryClass.Protected,"Discos de distribuições Linux persistentes; finalidade e dados internos exigem análise específica.","Perda de sistemas Linux, código e bancos.","Preservar o volume; identificar a distribuição e obter autorização específica antes de qualquer manutenção interna.");
        Add(Path.Combine(win,"Temp"),"Windows / temporários",RecoveryClass.Official,"O Windows conhece o ciclo de vida de instalação/serviços; exclusão genérica é bloqueada.","Interferência em instalações ou diagnóstico.","Abrir Armazenamento do Windows, revisar Arquivos temporários. Downloads e Lixeira não devem ser selecionados por padrão.");
        foreach(var name in new[]{"Windows.old","$WINDOWS.~BT","$Windows.~WS","ESD"})Add(Path.Combine(volume,name),"Windows / "+name,RecoveryClass.Official,"Resíduos de atualização/reinstalação exigem mecanismo oficial e revisão do estado.","Pode impedir rollback ou reinstalação local.","Armazenamento do Windows → Arquivos temporários / Recomendações de limpeza; revisar antes de confirmar.");
        return scopes;
    }
    public RecoveryReport Measure(IEnumerable<RecoveryScope> definitions,IProgress<string>? progress=null,CancellationToken token=default)
    {
        var scopes=definitions.GroupBy(s=>Path.GetFullPath(s.Path).TrimEnd('\\'),StringComparer.OrdinalIgnoreCase).Select(g=>g.First()).OrderBy(s=>s.Path,StringComparer.OrdinalIgnoreCase).ToArray();
        var excluded=scopes.Select(s=>Path.GetFullPath(s.Path).TrimEnd('\\')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ids=new HashSet<string>(StringComparer.Ordinal);var r=new RecoveryReport();var timer=Stopwatch.StartNew();long last=0;
        foreach(var scope in scopes)
        {
            var g=new RecoveryGroup{Scope=scope};r.Groups.Add(g);var stack=new Stack<IEnumerator<DiskEntry>>();
            try
            {
                token.ThrowIfCancellationRequested();
                for(string? p=scope.Path;p!=null;p=Path.GetDirectoryName(p))if(fs.Stat(p.Length==2?p+"\\":p).Attributes.HasFlag(FileAttributes.ReparsePoint))throw new IOException("Raiz/ancestral é link: não percorrido.");
                stack.Push(fs.Enumerate(scope.Path.Length==2?scope.Path+"\\":scope.Path).GetEnumerator());
                while(stack.Count>0)
                {
                    token.ThrowIfCancellationRequested();var it=stack.Peek();DiskEntry e;
                    try{if(!it.MoveNext()){stack.Pop().Dispose();continue;}e=it.Current;}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){g.Issues++;g.Partial=true;stack.Pop().Dispose();continue;}
                    if(e.Attributes.HasFlag(FileAttributes.ReparsePoint)){g.SkippedLinks++;g.Partial=true;continue;}
                    if(e.Directory){if(excluded.Contains(e.Path.TrimEnd('\\')))continue;stack.Push(fs.Enumerate(e.Path).GetEnumerator());continue;}
                    g.Files++;Allocation a;try{a=fs.Allocated(e.Path);}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){a=new(null,null);g.Issues++;g.Partial=true;}
                    bool protect=scope.Class==RecoveryClass.Protected||Classifier.KnownProtected(e.Path)||a.Links>1;if(protect)g.ProtectedFileCount++;
                    if(a.Links>1){g.SharedFiles++;if(a.Identity==null||ids.Count>=IdentityLimit){g.UnknownAllocation++;g.Partial=true;g.LogicalBytes+=e.Length;continue;}if(!ids.Add(a.Identity))continue;}
                    g.LogicalBytes+=e.Length;if(a.Bytes is long physical){g.PhysicalBytes+=physical;if(protect)g.ProtectedFileBytes+=physical;}else g.UnknownAllocation++;
                    if(g.LastWriteUtc==null||e.Modified>g.LastWriteUtc)g.LastWriteUtc=e.Modified;
                    if(timer.ElapsedMilliseconds-last>250){progress?.Report($"{scope.Origin}: {g.Files:N0} arquivos, {Sizes.Format(g.PhysicalBytes)} físicos conhecidos · {e.Path}");last=timer.ElapsedMilliseconds;}
                }
            }
            catch(FileNotFoundException){}catch(DirectoryNotFoundException){}
            catch(OperationCanceledException){g.Partial=true;r.Cancelled=true;break;}
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){g.Partial=true;g.Issues++;r.Notices.Add(scope.Path+": "+ex.Message);}
            finally{while(stack.Count>0)stack.Pop().Dispose();}
        }
        r.Groups=r.Groups.Where(g=>g.Files>0||g.Issues>0||g.PhysicalBytes>0||g.SkippedLinks>0).OrderByDescending(g=>g.PhysicalBytes).ToList();
        r.Notices.Add("Grupos são disjuntos: subescopos são excluídos dos pais. Hard links descontados globalmente por identidade; links/áreas inacessíveis não são percorridos. Valores parciais são limites inferiores.");
        r.Notices.Add("Última atividade é a maior data de gravação observada; não comprova ausência de leituras nem uso futuro. Nenhuma limpeza real foi executada.");
        r.Finished=DateTime.Now;return r;
    }
    public async Task<RecoveryReport> Investigate(bool wholeDisk=true,IProgress<string>? progress=null,CancellationToken token=default,bool allowPublicNpmProbe=false)
    {
        var report=Measure(DefaultScopes(wholeDisk),progress,token);report.PublicNpmProbeAllowed=allowPublicNpmProbe;if(report.Cancelled)return report;
        progress?.Report("Consultando consumidores e Git sem executar scripts dos projetos.");
        await RecoveryEvidence.Enrich(report,token);
        progress?.Report("Consultando estatísticas Docker locais — somente leitura, sem iniciar serviços");
        report.DockerStorage=await DockerStorageProbe.Collect(token);report.Notices.Add("Docker: "+report.DockerStorage.State+" Cache interno não compartilhado/fora de uso: "+(report.DockerStorage.GuestUnusedBuildCacheBytes is long guest?Sizes.Format(guest):"inconclusivo")+". Recuperação física confirmada no C: por esse diagnóstico: 0 B.");
        var docker=report.Groups.FirstOrDefault(g=>g.Origin=="Docker / discos e volumes WSL");if(docker!=null)docker.Checks=report.DockerStorage.State+" "+report.DockerStorage.Procedure;
        var npmGroup=report.Groups.FirstOrDefault(g=>g.Scope.Origin=="npm / downloads públicos SHA-512");
        if(npmGroup!=null)
        {
            var service=new WindowsCleanupService(allowPublicNpmProbe);var discovery=service.Discover(WindowsCleanupKind.NpmCache,progress,token);long verified=0;var outcomes=new List<CleanupOutcome>();
            foreach(var batch in discovery.Files.Select(f=>f.Path).Chunk(1000))
            {
                token.ThrowIfCancellationRequested();progress?.Report($"Verificando origem e integridade npm: {report.NpmPreview.Count:N0} candidatos; {Sizes.Format(verified)} confirmados em simulação.");
                var plan=service.Prepare(WindowsCleanupKind.NpmCache,batch,token,progress);report.NpmPreview.AddRange(plan.Items);var result=service.Run(plan,true,token:token);outcomes.AddRange(result.Items);
                var valid=result.Items.Where(i=>i.Status=="Simulado").Select(i=>i.Path).ToHashSet(StringComparer.Ordinal);
                verified+=plan.Items.Where(i=>i.Eligible&&valid.Contains(i.Path)).Sum(i=>i.EstimatedRecoverable);
            }
            // A mixed/partial filesystem measurement cannot support an exact confirmed subtotal.
            npmGroup.ConfirmedBytes=Math.Min(verified,npmGroup.PhysicalBytes);npmGroup.ProtectedFileBytes=npmGroup.PhysicalBytes-npmGroup.ConfirmedBytes;npmGroup.ProtectedFileCount=Math.Max(0,npmGroup.Files-outcomes.Count(i=>i.Status=="Simulado"));
            npmGroup.Checks=$"{outcomes.Count(i=>i.Status=="Simulado")} conteúdos íntegros revalidados; {outcomes.Count(i=>i.Status!="Simulado")} bloqueados. Listagem {(discovery.Truncated?"limitada; potencial é parcial":"concluída")}. Sem remoção real.";
            report.NpmSimulation=new("Simulada — sem remoção real",outcomes,0,true);report.PublicHeadRequests=service.PublicNpmRequests;
        }
        var thumbs=report.Groups.FirstOrDefault(g=>g.Origin=="Windows / miniaturas verificadas");
        if(thumbs!=null)
        {
            var service=new WindowsCleanupService();var discovery=service.Discover(WindowsCleanupKind.Thumbnails,progress,token);
            var plan=service.Prepare(WindowsCleanupKind.Thumbnails,discovery.Files.Take(1000).Select(f=>f.Path),token);var result=service.Run(plan,true,token:token);
            var valid=result.Items.Where(i=>i.Status=="Simulado").Select(i=>i.Path).ToHashSet(StringComparer.Ordinal);
            thumbs.ConfirmedBytes=Math.Min(thumbs.PhysicalBytes,plan.Items.Where(i=>i.Eligible&&valid.Contains(i.Path)).Sum(i=>i.EstimatedRecoverable));
            thumbs.ProtectedFileBytes=thumbs.PhysicalBytes-thumbs.ConfirmedBytes;thumbs.ProtectedFileCount=Math.Max(0,thumbs.Files-valid.Count);
            thumbs.Checks=$"{valid.Count} miniaturas revalidadas em simulação. Nenhuma remoção.";
        }
        var drive=new DriveInfo(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!);report.VolumeFree=drive.AvailableFreeSpace;report.VolumeUsed=drive.TotalSize-drive.TotalFreeSpace;
        try{new RecoveryHistory().Record(report);}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){report.Notices.Add("Histórico inconclusivo: "+ex.Message);}
        report.Finished=DateTime.Now;report.PeakWorkingSet=Process.GetCurrentProcess().PeakWorkingSet64;return report;
    }
}