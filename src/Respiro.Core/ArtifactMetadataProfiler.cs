using System.Diagnostics;
namespace Respiro.Core;
public sealed record ArtifactExtension(string Extension,long LogicalBytes,long KnownPhysicalBytes,long Files,long UnknownAllocation);
public sealed record ArtifactFile(string Path,long LogicalBytes,long? PhysicalBytes,DateTime LastWriteUtc,bool KnownProtected);
public sealed record ArtifactProfile(string Path,bool Partial,long ExaminedFiles,List<ArtifactExtension> Extensions,List<ArtifactFile> Largest,List<ArtifactExtension> Children,string Evidence)
{
    public string Summary=>Evidence+"\nPor extensão (físico conhecido): "+string.Join("; ",Extensions.Take(8).Select(e=>e.Extension+" "+Sizes.Format(e.KnownPhysicalBytes)))+"\nSubpastas imediatas (físico conhecido): "+string.Join("; ",Children.Take(8).Select(e=>e.Extension+" "+Sizes.Format(e.KnownPhysicalBytes)))+"\nMaiores arquivos (metadados): "+string.Join("; ",Largest.Take(6).Select(f=>System.IO.Path.GetFileName(f.Path)+" "+(f.PhysicalBytes is long n?Sizes.Format(n):"inconclusivo")));
}
internal static class ArtifactMetadataProfiler
{
    internal static ArtifactProfile Inspect(IReadOnlyFileSystem fs,string root,CancellationToken token,int limit=50000,IReadOnlySet<string>? excluded=null)
    {
        var extensions=new Dictionary<string,(long Logical,long Physical,long Files,long Unknown)>(StringComparer.OrdinalIgnoreCase);
        var children=new Dictionary<string,(long Logical,long Physical,long Files,long Unknown)>(StringComparer.OrdinalIgnoreCase);
        var largest=new List<ArtifactFile>();var identities=new HashSet<string>(StringComparer.Ordinal);var stack=new Stack<(string Path,int Depth)>();stack.Push((root,0));
        long files=0;bool partial=false;var clock=Stopwatch.StartNew();
        void Add(Dictionary<string,(long Logical,long Physical,long Files,long Unknown)> map,string key,DiskEntry e,Allocation a)
        {
            if(key.Length>100)key=key[..100];if(map.Count>=128&&!map.ContainsKey(key))key="(outros)";map.TryGetValue(key,out var v);map[key]=(v.Logical+e.Length,v.Physical+(a.Bytes??0),v.Files+1,v.Unknown+(a.Bytes==null?1:0));
        }
        while(stack.TryPop(out var dir))
        {
            token.ThrowIfCancellationRequested();try
            {
                DockerDeepDiagnostic.CheckAncestors(fs,dir.Path);
                foreach(var e in fs.Enumerate(dir.Path))
                {
                    token.ThrowIfCancellationRequested();if(files>=limit||clock.Elapsed>TimeSpan.FromSeconds(20)||stack.Count>=4096){partial=true;stack.Clear();break;}
                    if(e.Attributes.HasFlag(FileAttributes.ReparsePoint)){partial=true;continue;}
                    if(e.Directory&&excluded?.Contains(e.Path.TrimEnd('\\'))==true)continue;
                    if(e.Directory){if(dir.Depth<24)stack.Push((e.Path,dir.Depth+1));else partial=true;continue;}
                    var a=fs.Allocated(e.Path);if(a.Identity!=null&&!identities.Add(a.Identity))continue;files++;
                    string ext=Path.GetExtension(e.Path);Add(extensions,string.IsNullOrEmpty(ext)?"(sem extensão)":ext,e,a);
                    string relative=Path.GetRelativePath(root,e.Path),child=relative.Contains(Path.DirectorySeparatorChar)?relative.Split(Path.DirectorySeparatorChar)[0]:"(arquivos da raiz)";Add(children,child,e,a);
                    largest.Add(new(e.Path,e.Length,a.Bytes,e.Modified,Classifier.KnownProtected(e.Path)));largest.Sort((x,y)=>(y.PhysicalBytes??-1).CompareTo(x.PhysicalBytes??-1));if(largest.Count>8)largest.RemoveAt(8);
                }
            }
            catch(Exception ex)when(ex is IOException or UnauthorizedAccessException){partial=true;}
        }
        List<ArtifactExtension> Rows(Dictionary<string,(long Logical,long Physical,long Files,long Unknown)> values)=>values.Select(p=>new ArtifactExtension(p.Key,p.Value.Logical,p.Value.Physical,p.Value.Files,p.Value.Unknown)).OrderByDescending(e=>e.KnownPhysicalBytes).ToList();
        return new(root,partial,files,Rows(extensions),largest,Rows(children),"Somente nomes/metadados, sem leitura de conteúdo. Subescopos separados são excluídos; não somar perfis parciais. "+(partial?"Amostra limitada/parcial; não representa todo o diretório. ":"Percurso concluído dentro dos limites. ")+"Código, bancos, configurações, perfis e evidências E2.5 permanecem protegidos. Extensão ou idade não autoriza descarte.");
    }
}