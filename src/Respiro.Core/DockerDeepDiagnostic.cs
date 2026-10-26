using System.Diagnostics;
using System.IO.Pipes;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
namespace Respiro.Core;

public sealed record DockerDisk(string Path,long LogicalBytes,long? PhysicalBytes,string Role,DateTime LastWriteUtc,bool Stable)
{
    public string Physical=>PhysicalBytes is long n?Sizes.Format(n):"Inconclusivo";
    public string Logical=>Sizes.Format(LogicalBytes);
    public string State=>Stable?"Metadados estáveis; conteúdo protegido":"Medição parcial / arquivo mudou";
}
public sealed record WslDistribution(string Name,string State,int? Version,string? BasePath)
{ public string StateText=>State switch {"Stopped"=>"Parado","Running"=>"Em execução","Installing"=>"Instalando",_=>State}; }
public sealed record DockerResource(string Kind,string Name,string State,long? Bytes,string Classification,string Dependencies,string Reason,string Procedure)
{
    public string Size=>Bytes is long n?Sizes.Format(n):"Inconclusivo";
    public string StateText=>State switch {"running"=>"Em execução","exited"=>"Parado","created"=>"Criado, não iniciado","paused"=>"Pausado","restarting"=>"Reiniciando","dead"=>"Falha",_=>State};
    public string Approval=>"Nenhuma operação autorizada; somente diagnóstico";
}
public sealed class DockerDeepReport
{
    public DateTimeOffset Started {get;set;}=DateTimeOffset.Now;
    public DateTimeOffset Finished {get;set;}
    public List<DockerDisk> Disks {get;set;}=[];
    public List<WslDistribution> Distributions {get;set;}=[];
    public List<DockerResource> Resources {get;set;}=[];
    public List<string> Notices {get;set;}=[];
    public List<string> ProjectHints {get;set;}=[];
    public RecoveryReport? FolderInvestigation {get;set;}
    public List<ArtifactProfile> ArtifactProfiles {get;set;}=[];
    public List<StorageAlert> Alerts {get;set;}=[];
    public bool DaemonAvailable {get;set;}
    public string DaemonState {get;set;}="Não consultado";
    public string Endpoint {get;set;}="Nenhum endpoint acessível";
    public bool InventoryComplete {get;set;}
    public long? GuestUnusedBuildCacheBytes {get;set;}
    public long? GuestFreeBytes=>null;
    public long HostRecoverableConfirmedBytes=>0;
    public bool RealOperationExecuted=>false;
    public long FreeOnC {get;set;}
    public long PeakWorkingSet {get;set;}
    public long DockerKnownPhysicalBytes=>Disks.Where(d=>d.Role.StartsWith("Docker")).Sum(d=>d.PhysicalBytes??0);
}

// Official Engine API, fixed local named pipes and GET allowlist only. Never consults contexts/auth/configs.
internal sealed class LocalDockerReadApi : IDisposable
{
    internal static readonly string[] Routes=["/version","/system/df","/containers/json?all=1&size=1"];
    readonly HttpClient client;
    internal LocalDockerReadApi(string pipe)
    {
        if(pipe is not ("docker_engine" or "dockerDesktopLinuxEngine"))throw new ArgumentException("Pipe não autorizada");
        var handler=new SocketsHttpHandler{AllowAutoRedirect=false,UseCookies=false,UseProxy=false,Credentials=null};
        handler.ConnectCallback=async(_,ct)=>{var p=new NamedPipeClientStream(".",pipe,PipeDirection.InOut,PipeOptions.Asynchronous);try{await p.ConnectAsync(ct);return p;}catch{p.Dispose();throw;}};
        client=new(handler){Timeout=TimeSpan.FromSeconds(25)};
    }
    internal async Task<JsonDocument> Get(string route,CancellationToken token)
    {
        if(!Routes.Contains(route,StringComparer.Ordinal))throw new InvalidOperationException("Rota não autorizada");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(route=="/version"?4:20));
        using var response=await client.GetAsync("http://localhost"+route,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
        if(response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)throw new UnauthorizedAccessException("API local negou acesso; nenhuma alternativa de conexão será tentada");
        if(response.StatusCode!=HttpStatusCode.OK)throw new IOException("API retornou HTTP "+(int)response.StatusCode);
        using var stream=await response.Content.ReadAsStreamAsync(timeout.Token);
        using var memory=new MemoryStream();var buffer=new byte[16384];int n;
        while((n=await stream.ReadAsync(buffer,timeout.Token))>0){if(memory.Length+n>4*1024*1024)throw new IOException("Resposta excedeu limite de 4 MiB; inventário inconclusivo");memory.Write(buffer,0,n);}
        return JsonDocument.Parse(memory.ToArray(),new JsonDocumentOptions{MaxDepth=48});
    }
    public void Dispose()=>client.Dispose();
}

public sealed class DockerDeepDiagnostic
{
    const int ItemLimit=500;
    static string Text(JsonElement e,string key,int max=240)
    {
        if(e.ValueKind!=JsonValueKind.Object||!e.TryGetProperty(key,out var v)||v.ValueKind!=JsonValueKind.String)return "Não informado";
        return Clean(v.GetString()!,max);
    }
    static string Clean(string s,int max=240)=>new(s.Take(max).Where(c=>!char.IsControl(c)).ToArray());
    static long? Number(JsonElement e,string key)=>e.ValueKind==JsonValueKind.Object&&e.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.Number&&v.TryGetInt64(out long n)&&n>=0?n:null;
    static bool? Flag(JsonElement e,string key)=>e.ValueKind==JsonValueKind.Object&&e.TryGetProperty(key,out var v)&&v.ValueKind is JsonValueKind.True or JsonValueKind.False?v.GetBoolean():null;
    static IEnumerable<JsonElement> Items(JsonElement root,string key,DockerDeepReport report)
    {
        if(root.ValueKind!=JsonValueKind.Object||!root.TryGetProperty(key,out var a)||a.ValueKind!=JsonValueKind.Array){report.InventoryComplete=false;report.Notices.Add(key+": ausente; não significa zero.");return [];}
        if(a.GetArrayLength()>ItemLimit){report.InventoryComplete=false;report.Notices.Add(key+": lista limitada a 500 registros.");}
        return a.EnumerateArray().Take(ItemLimit).ToArray();
    }
    internal static void ParseUsage(JsonElement root,DockerDeepReport report)
    {
        report.InventoryComplete=true;
        var seen=new HashSet<string>(StringComparer.Ordinal);
        foreach(var i in Items(root,"Images",report))
        {
            string id=Text(i,"Id");if(id=="Não informado"||!seen.Add("i"+id)){report.InventoryComplete=false;continue;}
            string name=id;if(i.TryGetProperty("RepoTags",out var tags)&&tags.ValueKind==JsonValueKind.Array)name=string.Join(", ",tags.EnumerateArray().Where(x=>x.ValueKind==JsonValueKind.String).Take(3).Select(x=>Clean(x.GetString()!)));
            bool unused=Number(i,"Containers")==0;
            report.Resources.Add(new("Imagem",string.IsNullOrEmpty(name)?id:name,unused?"Sem container referenciado nesta consulta":"Em uso ou referências inconclusivas",Number(i,"Size"),unused?"Possivelmente não utilizada":"Recurso ativo / inconclusivo","Camadas podem ser compartilhadas; Compose/projetos podem voltar a usá-la",$"ID {id}; tamanho compartilhado: {Number(i,"SharedSize")?.ToString()??"inconclusivo"}. Não somar tamanhos lógicos de imagens.","Revisar tags e projetos antes de autorizar remoção individual; nenhuma remoção implementada."));
        }
        foreach(var v in Items(root,"Volumes",report))
        {
            string name=Text(v,"Name");if(name=="Não informado"||!seen.Add("v"+name)){report.InventoryComplete=false;continue;}
            JsonElement usage=default;v.TryGetProperty("UsageData",out usage);long? refs=Number(usage,"RefCount");
            report.Resources.Add(new("Volume",name,refs==0?"Sem referências atuais; dados continuam essenciais":$"Referências: {refs?.ToString()??"inconclusivas"}",Number(usage,"Size"),"Dados persistentes protegidos",Compose(v)+"; bancos PostgreSQL/Redis/Laravel podem depender deste volume","RefCount=0 não prova descarte. Conteúdo não foi aberto.","Identificar proprietário e backup/restauração; exclusão de volumes não implementada."));
        }
        long cache=0;bool known=true;int count=0;
        foreach(var c in Items(root,"BuildCache",report))
        {
            string id=Text(c,"ID");long? bytes=Number(c,"Size");bool? used=Flag(c,"InUse"),shared=Flag(c,"Shared");
            if(id=="Não informado"||!seen.Add("b"+id)){known=false;continue;}
            bool candidate=used==false&&shared==false&&bytes!=null;
            if(candidate&&bytes<=long.MaxValue-cache){cache+=bytes!.Value;count++;}else if(candidate)known=false;
            if(bytes==null||used==null||shared==null)known=false;
            report.Resources.Add(new("Cache BuildKit",id,candidate?"Fora de uso e não compartilhado declarado pelo daemon":"Ativo, compartilhado ou inconclusivo",bytes,candidate?"Cache potencialmente regenerável":"Recurso ativo / inconclusivo","Pode ser necessário para builds offline; não comprova que todos os builders foram inventariados",$"Tipo {Text(c,"Type")}; último uso {Text(c,"LastUsedAt")}; uso {Number(c,"UsageCount")?.ToString()??"inconclusivo"}. Descrições/comandos não são registrados.","Revisar builder e política de retenção; eventual prune específico exige aprovação futura. Não equivale a bytes recuperáveis no C:."));
        }
        // Missing BuildCache cannot be represented as a measured zero.
        bool present=root.ValueKind==JsonValueKind.Object&&root.TryGetProperty("BuildCache",out var arr)&&arr.ValueKind==JsonValueKind.Array;
        report.GuestUnusedBuildCacheBytes=present?cache:null;
        if(!known){report.InventoryComplete=false;report.Notices.Add("Cache com registros inconclusivos: soma conhecida é apenas limite inferior, não inventário completo.");}
        report.Notices.Add($"{count} registros conhecidos de cache fora de uso/não compartilhado. Não somar com volumes/imagens para inferir NTFS; camada, compressão e espaço livre do ext4 podem divergir.");
    }
    static string Compose(JsonElement e)
    {
        if(e.ValueKind!=JsonValueKind.Object||!e.TryGetProperty("Labels",out var labels)||labels.ValueKind!=JsonValueKind.Object)return "Projeto Compose não informado";
        // Allowlist: never export arbitrary labels, environment, commands, logs or credentials.
        return "Compose: "+Text(labels,"com.docker.compose.project")+" / serviço "+Text(labels,"com.docker.compose.service");
    }
    internal static void ParseContainers(JsonElement root,DockerDeepReport report)
    {
        if(root.ValueKind!=JsonValueKind.Array){report.InventoryComplete=false;report.Notices.Add("Inventário de containers inconclusivo.");return;}
        if(root.GetArrayLength()>ItemLimit){report.InventoryComplete=false;report.Notices.Add("Containers limitados a 500 registros.");}
        var seen=new HashSet<string>(StringComparer.Ordinal);
        foreach(var c in root.EnumerateArray().Take(ItemLimit))
        {
            if(c.ValueKind!=JsonValueKind.Object){report.InventoryComplete=false;continue;}
            string id=Text(c,"Id");if(id=="Não informado"||!seen.Add(id)){report.InventoryComplete=false;continue;}
            string name=id;if(c.TryGetProperty("Names",out var names)&&names.ValueKind==JsonValueKind.Array)name=string.Join(", ",names.EnumerateArray().Where(x=>x.ValueKind==JsonValueKind.String).Take(3).Select(x=>Clean(x.GetString()!)));
            string state=Text(c,"State"),image=Text(c,"Image");bool active=state is "running" or "restarting" or "paused";
            var mounts=new List<string>();if(c.TryGetProperty("Mounts",out var a)&&a.ValueKind==JsonValueKind.Array)foreach(var m in a.EnumerateArray().Take(16))mounts.Add(Text(m,"Type")+":"+Text(m,"Name")+" → "+Text(m,"Destination"));
            var service=Regex.Match(image,"(^|[/_:.-])(postgres|redis|mysql|mariadb|sail)([/_:.-]|$)",RegexOptions.IgnoreCase);
            string kind=service.Groups[2].Value.ToLowerInvariant() switch {"postgres"=>"PostgreSQL","redis"=>"Redis","sail"=>"Laravel Sail","mysql"=>"MySQL","mariadb"=>"MariaDB",_=>"serviço"};
            string hint=service.Success?"Possível dependência "+kind+" pela imagem; confirmar projeto, camada gravável e volumes. Conteúdo protegido":"Dependências de projetos exigem revisão";
            report.Resources.Add(new("Container",name,state,Number(c,"SizeRw"),active?"Recurso ativo":state is "exited" or "created"?"Parado com estado potencialmente importante":"Estado inconclusivo",Compose(c)+"; imagem "+image+"; "+string.Join("; ",mounts),hint+". Tamanho é camada gravável; exclui volumes. Comandos/Env não foram coletados.","Preservar camada gravável e volumes; nenhum stop, rm ou prune implementado."));
        }
    }
    internal static async Task ReadEngine(DockerDeepReport report,Func<string,CancellationToken,Task<JsonDocument>> get,CancellationToken token)
    {
        using(var version=await get("/version",token))
        {
            if(version.RootElement.ValueKind!=JsonValueKind.Object||Text(version.RootElement,"ApiVersion")=="Não informado")throw new IOException("Resposta de versão não verificável");
            report.DaemonAvailable=true;report.DaemonState="Daemon acessível; API "+Text(version.RootElement,"ApiVersion")+". Consultas oficiais GET somente leitura.";
        }
        using(var df=await get("/system/df",token))ParseUsage(df.RootElement,report);
        using(var containers=await get("/containers/json?all=1&size=1",token))ParseContainers(containers.RootElement,report);
    }
    internal static bool IsAccessDenied(Exception ex)
    {
        for(Exception? e=ex;e!=null;e=e.InnerException)if(e is UnauthorizedAccessException||e.HResult==unchecked((int)0x80070005)||e is System.ComponentModel.Win32Exception w&&w.NativeErrorCode==5)return true;
        return false;
    }
    public async Task<DockerDeepReport> Collect(IProgress<string>? progress=null,CancellationToken token=default)
    {
        var report=new DockerDeepReport();
        report.Notices.Add("Nenhuma limpeza, inicialização/parada de Docker/WSL, montagem, compactação ou leitura de bancos/credenciais. Todos os valores de recuperação no C: permanecem não confirmados (0 B).");
        progress?.Report("Inventariando distribuições WSL sem iniciá-las");
        report.Distributions=await WslInventory.Collect(report.Notices,token);
        var roots=new List<(string Path,string Role)>{(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Docker","wsl"),"Docker"),(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"wsl"),"WSL")};
        roots.Add((Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"DockerDesktop","vm-data"),"Docker / Hyper-V"));
        roots.Add((Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Docker","vm-data"),"Docker / VMM"));
        roots.AddRange(report.Distributions.Where(d=>d.BasePath!=null).Select(d=>(d.BasePath!,d.Name.StartsWith("docker-desktop",StringComparison.OrdinalIgnoreCase)?"Docker / "+d.Name:"WSL / "+d.Name)));
        report.Disks=FindDisks(new WindowsFileSystem(),roots,report.Notices,token);
        report.Disks=report.Disks.Select(d=>{var distribution=report.Distributions.FirstOrDefault(w=>w.BasePath!=null&&d.Path.StartsWith(w.BasePath.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase));return distribution==null?d:d with {Role=(distribution.Name.StartsWith("docker-desktop",StringComparison.OrdinalIgnoreCase)?"Docker / ":"WSL / ")+distribution.Name};}).ToList();
        foreach(string pipe in new[]{"docker_engine","dockerDesktopLinuxEngine"})
        {
            token.ThrowIfCancellationRequested();progress?.Report("Consultando somente API Docker local: "+pipe);
            using var api=new LocalDockerReadApi(pipe);
            try{await ReadEngine(report,api.Get,token);report.Endpoint="npipe://./pipe/"+pipe;break;}
            catch(OperationCanceledException)when(!token.IsCancellationRequested){report.DaemonState="Daemon não respondeu dentro do prazo; não foi iniciado.";if(report.DaemonAvailable){report.InventoryComplete=false;break;}}
            catch(Exception ex)when(ex is IOException or HttpRequestException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {
                report.InventoryComplete=false;if(report.DaemonAvailable)report.Endpoint="npipe://./pipe/"+pipe;report.DaemonState=report.DaemonAvailable?"Daemon acessível, inventário parcial/inconclusivo":"Daemon indisponível ou sem acesso; nenhuma elevação/inicialização tentada";
                report.Notices.Add(pipe+": "+(ex is UnauthorizedAccessException?"acesso negado; nenhuma alternativa tentada":"consulta inconclusiva ("+ex.GetType().Name+")"));
                if(IsAccessDenied(ex)||report.DaemonAvailable)break;
            }
        }
        progress?.Report("Identificando projetos por metadados; sem ler Compose, .env ou bancos");
        report.ProjectHints=ProjectMarkers.Find(token);
        progress?.Report("Medindo builds de auditoria, temporários de agentes e saídas protegidas");
        var scopes=RecoveryInvestigationService.DefaultScopes(false).Where(s=>s.Origin is "MultiCodex / .tmp" or "MultiCodex / output" or "Docker / discos e volumes WSL" or "WSL / discos de distribuições" || s.Origin.StartsWith("Rust/Tauri / Temp /",StringComparison.Ordinal) || (s.Origin.StartsWith("Temp /",StringComparison.Ordinal)&&Regex.IsMatch(s.Origin,"claude|codex",RegexOptions.IgnoreCase))).ToList();
        string codex=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".codex");if(Directory.Exists(codex))scopes.Add(new(codex,"Codex / estado local (somente metadados)",RecoveryClass.Protected,"Sessões, credenciais e configurações são protegidas; nenhum conteúdo aberto.","Perda de estado e configurações","Revisar contratos de retenção dos agentes; nenhuma exclusão."));
        report.FolderInvestigation=new RecoveryInvestigationService(new WindowsFileSystem()).Measure(scopes,progress,token);
        report.FolderInvestigation.Finished=DateTime.Now;
        await RecoveryEvidence.Enrich(report.FolderInvestigation,token,inspectCommandLines:false);
        foreach(var group in report.FolderInvestigation.Groups.Where(g=>!g.Origin.StartsWith("Docker")&&!g.Origin.StartsWith("WSL")&&g.Files>0).OrderByDescending(g=>g.PhysicalBytes).Take(6))
        {
            progress?.Report("Detalhando metadados de artefatos: "+group.Path);
            report.ArtifactProfiles.Add(ArtifactMetadataProfiler.Inspect(new WindowsFileSystem(),group.Path,token,excluded:report.FolderInvestigation.Groups.Where(g=>g.Path!=group.Path).Select(g=>g.Path.TrimEnd('\\')).ToHashSet(StringComparer.OrdinalIgnoreCase)));
        }
        try
        {
            var policy=StorageAlertSettings.Load();report.Alerts=StorageAlertEvaluator.Evaluate(report.FolderInvestigation,policy,new DriveInfo("C:\\").AvailableFreeSpace,DateTimeOffset.Now);
            new RecoveryHistory().Record(report.FolderInvestigation);
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or JsonException){report.Notices.Add("Histórico/alertas inconclusivos; limite ou acesso preservado: "+ex.GetType().Name);}
        report.FreeOnC=new DriveInfo("C:\\").AvailableFreeSpace;report.Finished=DateTimeOffset.Now;report.PeakWorkingSet=Process.GetCurrentProcess().PeakWorkingSet64;
        report.Notices.Add("Espaço livre interno do ext4 não foi medido: requer consulta específica no ambiente já ativo. VHDX não foi montado nem aberto. Outros builders/contextos e locais customizados podem não estar cobertos.");
        return report;
    }
    internal static List<DockerDisk> FindDisks(IReadOnlyFileSystem fs,IEnumerable<(string Path,string Role)> roots,List<string> notices,CancellationToken token)
    {
        var output=new List<DockerDisk>();var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);var identities=new HashSet<string>(StringComparer.Ordinal);int entries=0;
        foreach(var root in roots)
        {
            try
            {
                if(!fs.Exists(root.Path))continue;
                CheckAncestors(fs,root.Path);var stack=new Stack<(string Path,int Depth)>();stack.Push((root.Path,0));
                while(stack.TryPop(out var dir))
                {
                    token.ThrowIfCancellationRequested();CheckAncestors(fs,dir.Path);if(!paths.Add(dir.Path))continue;
                    foreach(var e in fs.Enumerate(dir.Path))
                    {
                        token.ThrowIfCancellationRequested();if(++entries>10000){notices.Add("Descoberta de discos limitada a 10.000 entradas; locais adicionais inconclusivos.");return output;}
                        if(e.Attributes.HasFlag(FileAttributes.ReparsePoint)){notices.Add("Link ignorado: "+e.Path);continue;}
                        if(e.Directory){if(dir.Depth<8)stack.Push((e.Path,dir.Depth+1));else notices.Add("Limite de profundidade: "+e.Path);continue;}
                        if(!new[]{".vhdx",".vhd"}.Contains(Path.GetExtension(e.Path),StringComparer.OrdinalIgnoreCase))continue;
                        var a=fs.Allocated(e.Path);if(a.Identity!=null&&!identities.Add(a.Identity))continue;
                        var after=fs.Stat(e.Path);bool stable=e.Length==after.Length&&e.Modified==after.Modified&&!after.Attributes.HasFlag(FileAttributes.ReparsePoint);
                        output.Add(new(e.Path,e.Length,stable?a.Bytes:null,root.Role,e.Modified,stable));
                    }
                }
            }
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){notices.Add("Discos parcialmente inacessíveis: "+root.Path+" ("+ex.GetType().Name+")");}
        }
        return output.OrderByDescending(d=>d.PhysicalBytes??-1).ToList();
    }
    internal static void CheckAncestors(IReadOnlyFileSystem fs,string path)
    {
        if(!Path.IsPathFullyQualified(path)||path.StartsWith("\\\\",StringComparison.Ordinal)||path.Contains("..",StringComparison.Ordinal))throw new IOException("Somente caminho local absoluto");
        for(string? p=Path.GetFullPath(path);p!=null;p=Path.GetDirectoryName(p))if(fs.Stat(p).Attributes.HasFlag(FileAttributes.ReparsePoint))throw new IOException("Ancestral é link");
    }
    public static void Save(DockerDeepReport report,string path)
    {
        var bytes=JsonSerializer.SerializeToUtf8Bytes(report,new JsonSerializerOptions{WriteIndented=true});if(bytes.Length>2*1024*1024)throw new IOException("Relatório excede 2 MiB; exportação bloqueada, sem sobrescrever.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);using var file=LockedFile.Open(path,write:true,create:!File.Exists(path));RandomAccess.Write(file.Handle,bytes,0);RandomAccess.SetLength(file.Handle,bytes.Length);RandomAccess.FlushToDisk(file.Handle);
    }
}

internal static class WslInventory
{
    static int running;
    internal static async Task<List<WslDistribution>> Collect(List<string> notices,CancellationToken token)
    {
        var list=new List<WslDistribution>();
        foreach(var view in new[]{RegistryView.Registry64,RegistryView.Registry32})try
        {
            using var hive=RegistryKey.OpenBaseKey(RegistryHive.CurrentUser,view);using var key=hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Lxss");
            if(key!=null)foreach(string id in key.GetSubKeyNames().Take(100)){using var sub=key.OpenSubKey(id);if(sub?.GetValue("DistributionName") is string name&&!list.Any(d=>d.Name==name)){string? path=sub.GetValue("BasePath") as string;if(path!=null&&path.StartsWith(@"\\?\",StringComparison.Ordinal))path=path[4..];list.Add(new(name,"Registro; estado não consultado",sub.GetValue("Version") as int?,path));}}
        }catch(Exception ex)when(ex is UnauthorizedAccessException or System.Security.SecurityException or IOException){notices.Add("Registro WSL inacessível; nenhuma permissão alterada.");}
        string exe=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),"System32","wsl.exe");
        if(!File.Exists(exe)||Interlocked.CompareExchange(ref running,1,0)!=0){notices.Add("WSL indisponível ou consulta anterior pendente.");return list;}
        Process? process=null;
        try
        {
            var si=new ProcessStartInfo(exe){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.Unicode};si.ArgumentList.Add("--list");si.ArgumentList.Add("--verbose");
            process=Process.Start(si)!;var stdout=Drain(process.StandardOutput);var stderr=Drain(process.StandardError);
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token);timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try{await process.WaitForExitAsync(timeout.Token);}catch(OperationCanceledException){var p=process;process=null;_ = Release(p,stdout,stderr);if(token.IsCancellationRequested)throw;notices.Add("Consulta WSL excedeu prazo; processo não foi encerrado.");return list;}
            string text=await stdout;await stderr;if(process.ExitCode==0)Merge(text,list);else notices.Add("wsl --list --verbose não concluiu; nenhum ambiente iniciado.");
        }
        catch(Exception ex)when(ex is IOException or System.ComponentModel.Win32Exception){notices.Add("Inventário WSL inconclusivo.");}
        finally{if(process!=null){process.Dispose();Interlocked.Exchange(ref running,0);}}
        return list;
    }
    static async Task Release(Process p,Task<string> stdout,Task<string> stderr){try{await p.WaitForExitAsync();await stdout;await stderr;}catch{}finally{p.Dispose();Interlocked.Exchange(ref running,0);}}
    static async Task<string> Drain(StreamReader reader){var s=new StringBuilder();var b=new char[1024];int n;while((n=await reader.ReadAsync(b))>0){int keep=Math.Min(n,16384-s.Length);if(keep>0)s.Append(b,0,keep);}return s.ToString();}
    internal static void Merge(string output,List<WslDistribution> list)
    {
        foreach(string line in output.Replace("\0","").Split('\n'))
        {
            var m=Regex.Match(line,@"^\s*\*?\s*(.+?)\s{2,}(.+?)\s+([12])\s*$");if(!m.Success)continue;
            string name=m.Groups[1].Value.Trim();var old=list.FirstOrDefault(d=>d.Name==name);if(old!=null)list.Remove(old);list.Add(new(name,m.Groups[2].Value.Trim(),int.Parse(m.Groups[3].Value),old?.BasePath));
        }
    }
}

internal static class ProjectMarkers
{
    internal static List<string> Find(CancellationToken token)
    {
        string user=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);var found=new List<string>();int seen=0;
        foreach(string root in new[]{Path.Combine(user,"dev"),Path.Combine(user,"Documents","ChatGPT")})
        {
            if(!Directory.Exists(root))continue;var stack=new Stack<(string Path,int Depth)>();stack.Push((root,0));
            while(stack.TryPop(out var dir)&&seen++<3000&&found.Count<100)
            {
                token.ThrowIfCancellationRequested();try
                {
                    DockerDeepDiagnostic.CheckAncestors(new WindowsFileSystem(),dir.Path);
                    bool artisan=File.Exists(Path.Combine(dir.Path,"artisan"));var compose=new[]{"compose.yml","compose.yaml","docker-compose.yml","docker-compose.yaml"}.Where(f=>File.Exists(Path.Combine(dir.Path,f))).ToArray();
                    if(artisan||compose.Length>0)found.Add(dir.Path+": "+(artisan?"Laravel/artisan presente; ":"")+string.Join(", ",compose)+". Somente nomes/metadados; PostgreSQL/Redis e mounts não confirmados offline.");
                    if(dir.Depth>=4)continue;
                    foreach(var d in new DirectoryInfo(dir.Path).EnumerateDirectories())if(!d.Attributes.HasFlag(FileAttributes.ReparsePoint)&&!new[]{".git","node_modules","vendor","target",".tmp","output",".sdk","bin","obj","storage","database","E2.5","E2-5"}.Contains(d.Name,StringComparer.OrdinalIgnoreCase))stack.Push((d.FullName,dir.Depth+1));
                }catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){}
            }
        }
        if(seen>=3000||found.Count>=100)found.Add("Inventário de marcadores limitado; ausência de marcador não prova ausência de dependências.");
        return found;
    }
}