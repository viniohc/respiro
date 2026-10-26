using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
namespace Respiro.Core;
internal static class RecoveryEvidence
{
    internal static async Task Enrich(RecoveryReport report,CancellationToken token,bool inspectCommandLines=true)
    {
        if(!inspectCommandLines)report.Notices.Add("Linhas de comando não consultadas por privacidade; nomes/PIDs e caminhos de executáveis não provam ausência de uso.");
        var matches=report.Groups.ToDictionary(g=>g, _=>new List<string>());bool incomplete=false;var tools=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            await Task.Run(()=>
            {
                object? locator=null, service=null, results=null;
                try
                {
                    locator=Activator.CreateInstance(Type.GetTypeFromProgID("WbemScripting.SWbemLocator")!);service=((dynamic)locator!).ConnectServer(".","root\\cimv2");results=((dynamic)service).ExecQuery(inspectCommandLines?"SELECT ProcessId,Name,ExecutablePath,CommandLine FROM Win32_Process":"SELECT ProcessId,Name,ExecutablePath FROM Win32_Process");
                    foreach(dynamic item in (System.Collections.IEnumerable)results)
                    {
                        try
                        {
                            token.ThrowIfCancellationRequested();string name=(string)item.Name;uint pid=(uint)item.ProcessId;
                            string? executable=item.ExecutablePath as string, command=inspectCommandLines?item.CommandLine as string:null;
                            if(command==null)incomplete=true;
                            if(new[]{"cargo.exe","rustc.exe","sccache.exe","multicodex.exe","java.exe","gradle.exe","adb.exe","emulator.exe","qemu-system-x86_64.exe"}.Contains(name,StringComparer.OrdinalIgnoreCase))tools.Add(name+" (PID "+pid+")");
                            foreach(var group in report.Groups)
                            {
                                // Raw command lines can contain secrets. They are compared in memory only, never reported.
                                bool direct=(command?.Contains(group.Path,StringComparison.OrdinalIgnoreCase)??false)||(executable?.StartsWith(group.Path.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase)??false);
                                bool possible=group.Origin.Contains("npm")&&name.Equals("node.exe",StringComparison.OrdinalIgnoreCase)&&command?.Contains("npm-cli",StringComparison.OrdinalIgnoreCase)==true;
                                if(direct||possible)matches[group].Add(name+" (PID "+pid+")"+(direct?" referência ao escopo":" possível consumidor npm"));
                            }
                        }
                        finally{if(Marshal.IsComObject(item))Marshal.ReleaseComObject(item);}
                    }
                }
                finally{foreach(var obj in new[]{results,service,locator})if(obj!=null&&Marshal.IsComObject(obj))Marshal.ReleaseComObject(obj);}
            },token);
        }
        catch(OperationCanceledException){throw;}
        catch(Exception ex)
        {
            incomplete=true;report.Notices.Add("Consulta de comandos de processos inconclusiva: "+ex.GetType().Name+"; usando somente nomes/PIDs disponíveis, sem elevar privilégios.");
            foreach(var process in Process.GetProcesses())
            {
                using(process)try
                {
                    token.ThrowIfCancellationRequested();string name=process.ProcessName+".exe", label=name+" (PID "+process.Id+")";
                    if(new[]{"cargo.exe","rustc.exe","sccache.exe","multicodex.exe","java.exe","gradle.exe","adb.exe","emulator.exe"}.Contains(name,StringComparer.OrdinalIgnoreCase))tools.Add(label);
                    foreach(var group in report.Groups)
                    {
                        bool possible=group.Origin.Contains("npm",StringComparison.OrdinalIgnoreCase)&&name.Equals("node.exe",StringComparison.OrdinalIgnoreCase)
                            ||(group.Origin.Contains("MultiCodex")||group.Origin.Contains("Rust/Tauri"))&&new[]{"cargo.exe","rustc.exe","sccache.exe","multicodex.exe"}.Contains(name,StringComparer.OrdinalIgnoreCase)
                            ||(group.Origin.Contains("Gradle")||group.Origin.Contains("Android"))&&new[]{"java.exe","gradle.exe","adb.exe","emulator.exe"}.Contains(name,StringComparer.OrdinalIgnoreCase);
                        if(possible&&matches[group].Count<12)matches[group].Add(label+" possível consumidor; vínculo ao diretório não comprovado");
                    }
                }
                catch(Exception error)when(error is InvalidOperationException or System.ComponentModel.Win32Exception){ }
            }
        }
        foreach(var group in report.Groups)group.ProcessEvidence=(matches[group].Count>0?string.Join("; ",matches[group].Take(12)):"Nenhum consumidor identificado nesta amostra")+(incomplete?"; comandos inacessíveis impedem prova de ausência de uso.":"; ausência de referência não comprova inatividade.");
        report.Notices.Add("Ferramentas/ambientes ativos: "+(tools.Count>0?string.Join("; ",tools):"não identificados na amostra")+". Nenhum processo foi encerrado.");
        string project=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Documents","ChatGPT","codexHub");
        var repo=InspectRepository(project,token);report.Notices.Add("MultiCodex: "+repo);
        foreach(var group in report.Groups.Where(g=>g.Origin.StartsWith("MultiCodex /")))group.Checks=repo+"; bloqueio E2.5 preservado; nenhuma ação de build/limpeza executada.";
        foreach(var group in report.Groups.Where(g=>Path.GetFileName(g.Path)=="target"&&!g.Origin.StartsWith("MultiCodex /")))
        {
            if(group.Partial){group.Checks="Medição parcial; análise de dependências/uso inconclusiva, sem limpeza de build.";continue;}
            string parent=Path.GetDirectoryName(group.Path)!;
            bool tag=false;try{string p=Path.Combine(group.Path,"CACHEDIR.TAG");if(new FileInfo(p).Length<4096)tag=File.ReadAllText(p).StartsWith("Signature: 8a477f597d28d172789f06886806bc55",StringComparison.Ordinal);}catch(IOException){}catch(UnauthorizedAccessException){}
            bool config=Environment.GetEnvironmentVariable("CARGO_TARGET_DIR")!=null;
            for(string? p=parent;p!=null;p=Path.GetDirectoryName(p))config|=File.Exists(Path.Combine(p,".cargo","config"))||File.Exists(Path.Combine(p,".cargo","config.toml"));
            config|=File.Exists(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".cargo","config.toml"));
            group.Checks=$"CACHEDIR.TAG Cargo: {tag}; manifesto: {File.Exists(Path.Combine(parent,"Cargo.toml"))}; configuração/target compartilhado {(config?"possível, não resolvido":"não resolvido por consulta Cargo")}; "+InspectRepository(parent,token)+"; catálogo do Cargo e dependências E2.5 não validados. Sem elegibilidade automática.";
        }
        foreach(var name in new[]{"hiberfil.sys","pagefile.sys"})
        {
            string path=Path.Combine(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!,name);
            try
            {
                var entry=new WindowsFileSystem().Enumerate(Path.GetPathRoot(path)!).FirstOrDefault(e=>!e.Directory&&Path.GetFileName(e.Path).Equals(name,StringComparison.OrdinalIgnoreCase));
                if(entry==null){report.Notices.Add(name+": não listado na raiz; nenhum espaço recuperável incluído. Confirmar estado com powercfg /a nas consultas oficiais.");continue;}
                report.Notices.Add($"{name}: {Sizes.Format(entry.Length)} lógicos por metadados da raiz. "+(name=="hiberfil.sys"?"Economia hipotética somente após decisão explícita sobre hibernação/Inicialização Rápida; nenhuma desativação implementada.":"Paginação é memória virtual necessária; não incluída no recuperável."));
            }
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){report.Notices.Add(name+": inconclusivo; nenhuma economia confirmada.");}
        }
    }
    static string InspectRepository(string path,CancellationToken token)
    {
        try{for(string? p=path;p!=null;p=Path.GetDirectoryName(p))if(File.GetAttributes(p).HasFlag(FileAttributes.ReparsePoint))return "Repositório sob link/junction; consulta não executada";}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){return "Repositório inacessível/inconclusivo";}
        string git=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"Git","cmd","git.exe");
        if(!File.Exists(git))return "Git confiável não encontrado; repositório inconclusivo";
        string Run(params string[] arguments)
        {
            var start=new ProcessStartInfo(git){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=Environment.GetFolderPath(Environment.SpecialFolder.System)};
            foreach(var a in new[]{"--no-optional-locks","-c","core.fsmonitor=false","-c","core.hooksPath=NUL","-C",path}.Concat(arguments))start.ArgumentList.Add(a);
            using var process=Process.Start(start)!;var output=process.StandardOutput.ReadToEndAsync();var errors=process.StandardError.ReadToEndAsync();
            if(!process.WaitForExit(10000))throw new IOException("Consulta Git excedeu tempo; processo não encerrado.");token.ThrowIfCancellationRequested();var text=output.GetAwaiter().GetResult();_ = errors.GetAwaiter().GetResult();if(process.ExitCode!=0||text.Length>1024*1024)throw new IOException("Git não confirmou o repositório.");return text;
        }
        try
        {
            string root=Run("rev-parse","--show-toplevel").Trim();string status=Run("status","--porcelain=v1","--untracked-files=no");string worktrees=Run("worktree","list","--porcelain");
            return $"Repositório confirmado: {root}; {status.Split('\n',StringSplitOptions.RemoveEmptyEntries).Length} alterações rastreadas; {worktrees.Split('\n').Count(l=>l.StartsWith("worktree "))} worktrees. Não rastreados/ignorados ainda exigem revisão; nenhuma ausência de commits autoriza exclusão";
        }
        catch(OperationCanceledException){throw;}
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception){return "Repositório/dependências inconclusivos; nenhuma limpeza de build autorizada";}
    }
}
public sealed class RecoveryHistory
{
    readonly string path;
    public RecoveryHistory() : this(Path.Combine(RespiroDataPaths.Root, "growth.jsonl")) { }
    internal RecoveryHistory(string path) { this.path = path; }
    internal static string ScopeHash(RecoveryReport report)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\n",report.Groups.Select(g=>g.Path.ToUpperInvariant()).Order(StringComparer.Ordinal)))));
    internal static string GroupHash(RecoveryReport report,RecoveryGroup group)=>Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("M6-v2-compression:"+group.Path.ToUpperInvariant()+"\n"+string.Join("\n",report.Groups.Where(g=>g.Path.StartsWith(group.Path.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase)).Select(g=>g.Path.ToUpperInvariant()).Order(StringComparer.Ordinal)))));
    public void Record(RecoveryReport report)
    {
        if (report.Finished == default) throw new IOException("Análise sem data de conclusão.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // One exclusive handle covers read, comparison, capacity check and append.
        using var log = LockedFile.Open(path, write: true, create: !File.Exists(path));
        string text = RecoveryHistoryReader.Read(log);
        var samples = RecoveryHistoryReader.Parse(text);
        var pending = new System.Text.StringBuilder();
        if (log.Stamp.Bytes > 0 && !text.EndsWith('\n')) pending.Append('\n');
        foreach (var group in report.Groups)
        {
            group.GrowthBytes = null; group.GrowthBaselineUtc = null; group.GrowthObservedHours = null;
            bool complete = !report.Cancelled && !group.Partial && group.UnknownAllocation == 0 && group.PhysicalBytes >= 0;
            var previous = complete ? RecoveryHistoryReader.Previous(samples, report, group, report.Finished.ToUniversalTime()) : null;
            if (previous != null)
            {
                group.GrowthBytes = group.PhysicalBytes - previous.PhysicalBytes;
                group.GrowthBaselineUtc = previous.At;
                group.GrowthObservedHours = (report.Finished.ToUniversalTime() - previous.At).TotalHours;
            }
            pending.Append(JsonSerializer.Serialize(new RecoverySample(report.Finished.ToUniversalTime(), group.Path,
                GroupHash(report, group), group.PhysicalBytes, complete))).Append('\n');
        }
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(pending.ToString());
        if (log.Stamp.Bytes + bytes.Length > RecoveryHistoryReader.Limit)
            throw new IOException("Histórico cheio: comparação disponível, mas nenhuma amostra desta análise foi acrescentada. Arquivamento manual necessário.");
        RandomAccess.Write(log.Handle, bytes, log.Stamp.Bytes);
        RandomAccess.FlushToDisk(log.Handle);
    }
}
