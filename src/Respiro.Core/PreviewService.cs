using System.Diagnostics;
namespace Respiro.Core;
public record PreviewRow(string Path,string Size,string Use,string Git,string Decision);
public record PreviewResult(long SelectedBytes,long AuthorizedBytes,long CandidateBytes,List<PreviewRow> Items,string Processes);
public sealed class PreviewService(IReadOnlyFileSystem fs)
{
    public PreviewResult Evaluate(IEnumerable<FileRow> selected,CancellationToken token=default)
    {
        var rows=new List<PreviewRow>();long bytes=0;long candidateBytes=0;bool gitTimedOut=false; var gitCache=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var item in selected.DistinctBy(x=>x.Path,StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();string use="Inconclusivo";string git="Inconclusivo: nenhum repositório identificado.";
            try
            {
                bool link=false;for(string? p=item.Path;p!=null;p=Path.GetDirectoryName(p.TrimEnd('\\'))){if(fs.Stat(p).Attributes.HasFlag(FileAttributes.ReparsePoint)){link=true;break;}if(Path.GetPathRoot(p)==p)break;}
                var current=fs.Stat(item.Path);
                use=link?"Bloqueado: link/junction no caminho.": current.Length!=item.Bytes || current.Modified!=item.Modified ? "Bloqueado: arquivo mudou após a análise." : fs.ProbeUse(item.Path);
                var root=FindGit(item.Path);if(root!=null){if(!gitCache.TryGetValue(root,out git!)){git=gitTimedOut || gitCache.Count>=8 ? "Inconclusivo: limite de consultas Git nesta simula\u00e7\u00e3o." : ReadGit(root);if(git.Contains("5 s"))gitTimedOut=true;gitCache[root]=git;}}
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){use="Inconclusivo: "+ex.Message;}
            bytes+=item.Bytes;if(item.Nature==Nature.PotencialmenteRegeneravel)candidateBytes+=item.Bytes;
            rows.Add(new(item.Path,item.Size,use,git,item.Nature==Nature.Protegido?"Bloqueado: conteúdo protegido.":"Bloqueado: simulação; dependências e política E2.5 não validadas."));
        }
        string processes;try{processes=string.Join(", ",Process.GetProcesses().Select(p=>{using(p){try{return p.ProcessName;}catch{return "indisponível";}}}).Where(n=>new[]{"node","cargo","rustc","java","gradle","codex","multicodex","docker","emulator","dotnet"}.Any(t=>n.Contains(t,StringComparison.OrdinalIgnoreCase))).Distinct());}catch{processes="Consulta inconclusiva";}
        return new(bytes,0,candidateBytes,rows,processes);
    }
    string? FindGit(string path){for(var p=Path.GetDirectoryName(path);p!=null;p=Path.GetDirectoryName(p))if(fs.Exists(Path.Combine(p,".git")))return p;return null;}
    static string ReadGit(string root)
    {
        // Fixed arguments, no shell, hooks or project scripts. Optional Git index writes disabled.
        try
        {
            var start=new ProcessStartInfo("git") { RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false,CreateNoWindow=true };
            foreach(var a in new[]{"--no-optional-locks","-c","core.fsmonitor=false","-c","core.untrackedCache=false","-C",root,"status","--porcelain=v1","--untracked-files=normal","--ignored=matching"})start.ArgumentList.Add(a);
            start.Environment["GIT_OPTIONAL_LOCKS"]="0";
            using var process=Process.Start(start)!; int lines=0;process.OutputDataReceived+=(_,e)=>{if(!string.IsNullOrEmpty(e.Data))Interlocked.Increment(ref lines);};process.ErrorDataReceived+=(_,_)=>{};process.BeginOutputReadLine();process.BeginErrorReadLine();
            if(!process.WaitForExit(5000)) return "Inconclusivo: Git excedeu 5 s; consulta não autoriza remoção.";
            process.WaitForExit();return process.ExitCode!=0?"Inconclusivo: Git recusou a consulta; configurações de segurança preservadas.":lines>0?"Há alterações, arquivos não rastreados ou ignorados. Preservar; ignorado não significa descartável.":"Status sem alterações reportadas; dependências, worktrees e arquivos ignorados exigem revisão específica.";
        }catch{return "Inconclusivo: Git indisponível.";}
    }
}
