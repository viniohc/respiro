using System.Diagnostics;
namespace Respiro.Core;
public sealed class Scanner(IReadOnlyFileSystem fs)
{
    public const int FileLimit=1500, FolderLimit=50000, IdentityLimit=500000;
    sealed class Frame(FolderRow folder, IEnumerator<DiskEntry> entries) : IDisposable
    { public FolderRow Folder=folder; public IEnumerator<DiskEntry> Entries=entries; public void Dispose()=>Entries.Dispose(); }
    public ScanResult Scan(string root, IProgress<ScanProgress>? progress=null, CancellationToken token=default)
    {
        var r=new ScanResult { Root=Path.GetFullPath(root) }; var classifier=new Classifier(fs);
        var stack=new Stack<Frame>(); var ids=new HashSet<string>(StringComparer.Ordinal);
        var categories=new Dictionary<string,(long Bytes,long Count)>(); var extensions=new Dictionary<string,(long Bytes,long Count)>(StringComparer.OrdinalIgnoreCase);
        var largest=new PriorityQueue<FileRow,long>(); var candidates=new PriorityQueue<FileRow,long>(); var protectedFiles=new PriorityQueue<FileRow,long>();
        var clock=Stopwatch.StartNew(); long last=0;
        void Issue(string p,string message){r.IssueCount++; if(r.Issues.Count<1000) r.Issues.Add(new(p,message)); foreach(var f in stack)f.Folder.Partial=true;}
        void Push(string p,string? parent)
        {
            var folder=new FolderRow { Path=p, Parent=parent }; r.FolderCount++;
            if(r.Folders.Count<FolderLimit) r.Folders.Add(folder); else r.FolderLimitReached=true;
            try { stack.Push(new(folder,fs.Enumerate(p).GetEnumerator())); }
            catch(Exception e) when(e is IOException or UnauthorizedAccessException) {folder.Partial=true;Issue(p,e.Message);}
        }
        static void Top(PriorityQueue<FileRow,long> q,FileRow row){q.Enqueue(row,row.Bytes);if(q.Count>FileLimit)q.Dequeue();}
        static void Add(Dictionary<string,(long Bytes,long Count)> d,string key,long bytes){var old=d.GetValueOrDefault(key);d[key]=(old.Bytes+bytes,old.Count+1);}
        try
        {
            // Validate all ancestors, including a selected path underneath a junction.
            for(string? p=r.Root;p!=null;p=Path.GetDirectoryName(p.TrimEnd(Path.DirectorySeparatorChar)))
            { token.ThrowIfCancellationRequested(); if(fs.Stat(p).Attributes.HasFlag(FileAttributes.ReparsePoint))throw new IOException("Raiz ou ancestral é link/junction; análise bloqueada."); if(Path.GetPathRoot(p)==p)break; }
            if(!fs.Stat(r.Root).Directory) throw new IOException("Selecione um diretório ou disco.");
            Push(r.Root,null);
            while(stack.Count>0)
            {
                token.ThrowIfCancellationRequested(); var frame=stack.Peek(); DiskEntry e;
                try { if(!frame.Entries.MoveNext()){stack.Pop().Dispose();continue;} e=frame.Entries.Current; }
                catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) {Issue(frame.Folder.Path,ex.Message);stack.Pop().Dispose();continue;}
                if(e.Attributes.HasFlag(FileAttributes.ReparsePoint)) {r.SkippedLinks++;Issue(e.Path,"Link, junction ou placeholder não percorrido.");continue;}
                if(Path.GetFileName(e.Path).Equals(".git",StringComparison.OrdinalIgnoreCase) && r.Findings.Count<2000)
                    r.Findings.Add(new(e.Path,e.Directory?"Reposit\u00f3rio Git identificado. Preservar.":"Refer\u00eancia Git externa: poss\u00edvel worktree ou subm\u00f3dulo. N\u00e3o autoriza remo\u00e7\u00e3o."));
                if(e.Directory){Push(e.Path,frame.Folder.Path);continue;}
                r.Files++;
                Allocation allocation; try{allocation=fs.Allocated(e.Path);}catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){allocation=new(null,null);}
                if(allocation.Identity!=null && ids.Contains(allocation.Identity)){r.DuplicateLinks++;continue;}
                if(allocation.Identity!=null && ids.Count<IdentityLimit)ids.Add(allocation.Identity);
                else if(allocation.Identity==null || allocation.Links>1) { allocation=new(null,null); r.UnknownOccupied++; }
                if(allocation.Bytes is long occupied)r.KnownOccupied+=occupied; else if(allocation.Identity!=null)r.UnknownOccupied++;
                r.Bytes+=e.Length;
                foreach(var f in stack){f.Folder.Bytes+=e.Length;f.Folder.Files++;if(allocation.Bytes is long physical)f.Folder.KnownOccupied+=physical;else f.Folder.UnknownOccupied++;}
                var c=allocation.Links>1 ? new Classification("Arquivos com hard links",Nature.Protegido,"Há múltiplos nomes para os mesmos dados. Dependências compartilhadas: preservar.") : classifier.Classify(e); var row=new FileRow(e.Path,e.Length,allocation.Bytes,c.Category,c.Nature,c.Reason,e.Modified);
                Add(categories,c.Category,e.Length); var ext=Path.GetExtension(e.Path); if(extensions.Count>=2048 && !extensions.ContainsKey(ext))ext="(outras extensões)"; Add(extensions,string.IsNullOrEmpty(ext)?"(sem extensão)":ext,e.Length);
                Top(largest,row); if(c.Nature==Nature.PotencialmenteRegeneravel)Top(candidates,row);if(c.Nature==Nature.Protegido)Top(protectedFiles,row);
                if(clock.ElapsedMilliseconds-last>=250){progress?.Report(new(r.Files,r.FolderCount,r.Bytes,frame.Folder.Path));last=clock.ElapsedMilliseconds;}
            }
        }
        catch(OperationCanceledException){r.Cancelled=true;foreach(var f in stack)f.Folder.Partial=true;}
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){Issue(r.Root,ex.Message);}
        finally{while(stack.Count>0)stack.Pop().Dispose();}
        r.LargestFiles=largest.UnorderedItems.Select(x=>x.Element).OrderByDescending(x=>x.Bytes).ToList();
        r.Candidates=candidates.UnorderedItems.Select(x=>x.Element).OrderByDescending(x=>x.Bytes).ToList();
        r.Protected=protectedFiles.UnorderedItems.Select(x=>x.Element).OrderByDescending(x=>x.Bytes).ToList();
        r.Categories=categories.Select(x=>new AggregateRow(x.Key,x.Value.Bytes,x.Value.Count)).OrderByDescending(x=>x.Bytes).ToList();
        r.Extensions=extensions.Select(x=>new AggregateRow(x.Key,x.Value.Bytes,x.Value.Count)).OrderByDescending(x=>x.Bytes).ToList();
        r.Finished=DateTime.Now;progress?.Report(new(r.Files,r.FolderCount,r.Bytes,r.Root));return r;
    }
}

