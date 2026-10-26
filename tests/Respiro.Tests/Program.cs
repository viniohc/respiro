using Respiro.Core;
using System.Runtime.InteropServices;
// Read-only production integration mode: no call can set simulate=false in this branch.
if(args.Length>=2&&args.Length<=3&&args[0]=="--simulate-production-npm")
{
    var service=new WindowsCleanupService(args.Contains("--verify-public-npm"));var discovery=service.Discover(WindowsCleanupKind.NpmCache);var previews=new List<WindowsCleanupItem>();var results=new List<CleanupOutcome>();
    foreach(var batch in discovery.Files.Select(f=>f.Path).Chunk(1000))
    {
        Console.WriteLine($"Verificando lote npm: {previews.Count} candidatos já examinados.");var plan=service.Prepare(WindowsCleanupKind.NpmCache,batch);previews.AddRange(plan.Items);results.AddRange(service.Run(plan,true).Items);
    }
    var valid=results.Where(i=>i.Status=="Simulado").Select(i=>i.Path).ToHashSet(StringComparer.Ordinal);
    long confirmed=previews.Where(i=>i.Eligible&&valid.Contains(i.Path)).DistinctBy(i=>i.Path,StringComparer.Ordinal).Sum(i=>i.EstimatedRecoverable);
    string path=Path.GetFullPath(args[1]);Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path,System.Text.Json.JsonSerializer.Serialize(new{At=DateTime.Now,Mode="SIMULATION_ONLY",RealDeletionExecuted=false,PublicNpmProbeAllowed=args.Contains("--verify-public-npm"),PublicHeadRequests=service.PublicNpmRequests,ConfirmedBytes=confirmed,VerifiedFiles=valid.Count,Discovery=discovery,Preview=previews,Results=results,PeakWorkingSet=System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64},new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));
    Console.WriteLine($"npm: {valid.Count} arquivos verificados; {confirmed} bytes; nenhuma remoção.");return 0;
}
var tests=new List<(string,Action)>();
void Test(string name,Action action)=>tests.Add((name,action));
void Equal<T>(T actual,T expected){if(!EqualityComparer<T>.Default.Equals(actual,expected))throw new Exception($"Esperado {expected}; obtido {actual}");}
var root=@"C:\fixture";
Test("Agregação lógica, categorias, extensões e pastas",()=>{var fs=new FakeFs(root);fs.File("a.bin",10);fs.Directory("sub");fs.File(@"sub\b.bin",20);var r=new Scanner(fs).Scan(root);Equal(r.Bytes,30L);Equal(r.Files,2L);Equal(r.FolderCount,2L);Equal(r.Categories.Sum(x=>x.Bytes),30L);Equal(r.Extensions.Sum(x=>x.Bytes),30L);Equal(r.Folders.Single(f=>f.Path==root).Bytes,30L);});
Test("Pasta inacessível produz resultado parcial",()=>{var fs=new FakeFs(root);fs.Directory("denied");fs.Denied.Add(root+@"\denied");fs.File("safe.bin",7);var r=new Scanner(fs).Scan(root);Equal(r.Bytes,7L);Equal(r.IssueCount,1L);Equal(r.Partial,true);});
Test("Junction não é percorrida e não cria ciclo",()=>{var fs=new FakeFs(root);fs.Directory("loop",FileAttributes.ReparsePoint);fs.File(@"loop\hidden.bin",99);var r=new Scanner(fs).Scan(root);Equal(r.Bytes,0L);Equal(r.SkippedLinks,1L);Equal(fs.Visited.Contains(root+@"\loop"),false);});
Test("Link de arquivo é ignorado",()=>{var fs=new FakeFs(root);fs.File("link.bin",50,FileAttributes.ReparsePoint);Equal(new Scanner(fs).Scan(root).Bytes,0L);});
Test("Raiz sob junction é bloqueada",()=>{var fs=new FakeFs(root);fs.Directory("link",FileAttributes.ReparsePoint);fs.Directory(@"link\child");Equal(new Scanner(fs).Scan(root+@"\link\child").IssueCount,1L);});
Test("Cancelamento preserva resultado parcial",()=>{var fs=new FakeFs(root);fs.File("a.bin",8);var cts=new CancellationTokenSource();cts.Cancel();var r=new Scanner(fs).Scan(root,null,cts.Token);Equal(r.Cancelled,true);Equal(r.Bytes,0L);});
Test("Cancelamento durante enumeração",()=>{var fs=new FakeFs(root);for(int i=0;i<10;i++)fs.File(i+".bin",10);var cts=new CancellationTokenSource();fs.OnRead=()=>cts.Cancel();var r=new Scanner(fs).Scan(root,null,cts.Token);Equal(r.Cancelled,true);if(r.Files>=10)throw new Exception("Não cancelou progressivamente");});
Test("Hard links não duplicam os totais",()=>{var fs=new FakeFs(root);fs.File("a.bin",20);fs.File("b.bin",20);fs.SharedIdentity=true;var r=new Scanner(fs).Scan(root);Equal(r.Bytes,20L);Equal(r.KnownOccupied,20L);Equal(r.DuplicateLinks,1L);});
Test("Ocupação desconhecida nunca vira zero comprovado",()=>{var fs=new FakeFs(root);fs.File("a.bin",20);fs.UnknownAllocation=true;var r=new Scanner(fs).Scan(root);Equal(r.UnknownOccupied,1L);Equal(r.LargestFiles[0].Occupied,null);});
Test("Dados importantes em pasta temporária são protegidos",()=>{var fs=new FakeFs(root);var classifier=new Classifier(fs);foreach(var name in new[]{@".tmp\main.rs",@".tmp\data.db",@".tmp\family.jpg",@".tmp\auth.json",@".tmp\.env",@".tmp\database.db-wal",@".tmp\private.key"})Equal(classifier.Classify(new(root+"\\"+name,false,0,10,DateTime.UtcNow)).Nature,Nature.Protegido);});
Test("Nome target isolado não autoriza classificação regenerável",()=>{var fs=new FakeFs(root);Equal(new Classifier(fs).Classify(new(root+@"\target\artifact.bin",false,0,10,DateTime.UtcNow)).Nature,Nature.Desconhecido);});
Test("Rust requer Cargo.toml e preserva fonte dentro do target",()=>{var fs=new FakeFs(root);fs.File("Cargo.toml",10);var c=new Classifier(fs);Equal(c.Classify(new(root+@"\target\artifact.bin",false,0,10,DateTime.UtcNow)).Nature,Nature.PotencialmenteRegeneravel);Equal(c.Classify(new(root+@"\target\source.rs",false,0,10,DateTime.UtcNow)).Nature,Nature.Protegido);});
Test("E2.5 preservada",()=>{var fs=new FakeFs(root);Equal(new Classifier(fs).Classify(new(root+@"\E2.5\artifact.bin",false,0,10,DateTime.UtcNow)).Nature,Nature.Protegido);});
Test("Prévia nunca autoriza remoção mesmo com uso não detectado",()=>{var fs=new FakeFs(root);fs.File("a.bin",10);var file=new Scanner(fs).Scan(root).LargestFiles[0];var r=new PreviewService(fs).Evaluate(new[]{file,file});Equal(r.AuthorizedBytes,0L);Equal(r.SelectedBytes,10L);Equal(r.Items.Count,1);});
Test("Mudança depois da análise bloqueia prévia",()=>{var fs=new FakeFs(root);fs.File("a.bin",10);var file=new Scanner(fs).Scan(root).LargestFiles[0];fs.File("a.bin",11);var r=new PreviewService(fs).Evaluate(new[]{file});if(!r.Items[0].Use.Contains("mudou"))throw new Exception("Mudança não detectada");});
Test("Arquivo bloqueado aparece na prévia",()=>{var fs=new FakeFs(root);fs.File("a.bin",10);fs.Use="Bloqueado: arquivo em uso.";var r=new PreviewService(fs).Evaluate(new Scanner(fs).Scan(root).LargestFiles);if(!r.Items[0].Use.Contains("em uso"))throw new Exception("Uso omitido");});
Test("Limite de memória das listas mantém os totais",()=>{var fs=new FakeFs(root);for(int i=0;i<2000;i++)fs.File(i+".bin",i);var r=new Scanner(fs).Scan(root);Equal(r.LargestFiles.Count,1500);Equal(r.Bytes,1999000L);Equal(r.LargestFiles[0].Bytes,1999L);});
Test("Windows: arquivo em uso e ocupação nativa",()=>{var dir=Path.Combine(Environment.CurrentDirectory,"artifacts","test-fixtures");System.IO.Directory.CreateDirectory(dir);var path=Path.Combine(dir,"locked.bin");System.IO.File.WriteAllBytes(path,new byte[8192]);var fs=new WindowsFileSystem();using(var stream=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){if(!fs.ProbeUse(path).Contains("Bloqueado"))throw new Exception("Bloqueio não detectado");}var a=fs.Allocated(path);if(a.Bytes is null || a.Identity is null)throw new Exception("Metadados NTFS não obtidos");Equal(fs.Stat(path).Length,8192L);});
Test("Node requer manifesto e não autoriza dependências",()=>{var fs=new FakeFs(root);fs.File("package.json",1);var c=new Classifier(fs).Classify(new(root+@"\node_modules\module\a.bin",false,0,12,DateTime.UtcNow));Equal(c.Category,"Node / dependências");Equal(c.Nature,Nature.Desconhecido);});
Test("npm requer estrutura interna do cache",()=>{var fs=new FakeFs(root);fs.Directory(@"_cacache\index-v5");fs.Directory(@"_cacache\content-v2");Equal(new Classifier(fs).Classify(new(root+@"\_cacache\content-v2\hash",false,0,12,DateTime.UtcNow)).Category,"npm / cache");});
Test("Gradle requer manifesto ou estrutura reconhecida",()=>{var fs=new FakeFs(root);fs.File("build.gradle",1);Equal(new Classifier(fs).Classify(new(root+@"\build\artifact.bin",false,0,12,DateTime.UtcNow)).Category,"Gradle / build Android");});
Test("AVD com config.ini permanece protegido",()=>{var fs=new FakeFs(root);fs.File(@"phone.avd\config.ini",1);var c=new Classifier(fs).Classify(new(root+@"\phone.avd\userdata.img",false,0,12,DateTime.UtcNow));Equal(c.Category,"Android / AVD");Equal(c.Nature,Nature.Protegido);});
Test("Ambiente Python requer pyvenv.cfg",()=>{var fs=new FakeFs(root);fs.File(@"venv\pyvenv.cfg",1);Equal(new Classifier(fs).Classify(new(root+@"\venv\Lib\a.bin",false,0,12,DateTime.UtcNow)).Category,"Python / ambiente virtual");});
Test("Cache Python requer fonte correspondente",()=>{var fs=new FakeFs(root);fs.File("module.py",1);Equal(new Classifier(fs).Classify(new(root+@"\__pycache__\module.cpython-313.pyc",false,0,12,DateTime.UtcNow)).Nature,Nature.PotencialmenteRegeneravel);});
Test("Disco virtual Docker WSL nunca é candidato",()=>{var fs=new FakeFs(root);Equal(new Classifier(fs).Classify(new(root+@"\Docker\ext4.vhdx",false,0,12,DateTime.UtcNow)).Nature,Nature.Protegido);});
Test("Saída de agente não é automaticamente regenerável",()=>{var fs=new FakeFs(root);fs.File("AGENTS.md",1);Equal(new Classifier(fs).Classify(new(root+@"\output\artifact.bin",false,0,12,DateTime.UtcNow)).Nature,Nature.Desconhecido);});
Test("Marcador de worktree é inventariado e protegido",()=>{var fs=new FakeFs(root);fs.File(".git",1);var r=new Scanner(fs).Scan(root);Equal(r.Findings.Count,1);Equal(r.Protected.Count,1);});
Test("Arquivos protegidos não entram no potencial regenerável",()=>{var fs=new FakeFs(root);fs.File("source.cs",40);var r=new PreviewService(fs).Evaluate(new Scanner(fs).Scan(root).LargestFiles);Equal(r.SelectedBytes,40L);Equal(r.CandidateBytes,0L);Equal(r.AuthorizedBytes,0L);});
Test("Windows: hard link real é contado apenas uma vez",()=>{var dir=Path.Combine(Environment.CurrentDirectory,"artifacts","hardlink-fixture");System.IO.Directory.CreateDirectory(dir);var a=Path.Combine(dir,"original.bin");var b=Path.Combine(dir,"alias.bin");System.IO.File.WriteAllBytes(a,new byte[12345]);if(!System.IO.File.Exists(b) && !Native.CreateHardLink(b,a,IntPtr.Zero))throw new Exception("Criação do hard link falhou");var r=new Scanner(new WindowsFileSystem()).Scan(dir);Equal(r.Bytes,12345L);Equal(r.DuplicateLinks,1L);});
Test("Windows: junction real não é percorrida",()=>{var dir=Path.Combine(Environment.CurrentDirectory,"artifacts","junction-fixture");var target=Path.Combine(dir,"target");var link=Path.Combine(dir,"link");System.IO.Directory.CreateDirectory(target);System.IO.File.WriteAllBytes(Path.Combine(target,"data.bin"),new byte[16]);if(!System.IO.Directory.Exists(link)){var si=new System.Diagnostics.ProcessStartInfo("cmd.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};foreach(var a in new[]{"/c","mklink","/J",link,target})si.ArgumentList.Add(a);using var process=System.Diagnostics.Process.Start(si)!;process.WaitForExit();if(process.ExitCode!=0)throw new Exception("Não foi possível criar junction de teste");}var r=new Scanner(new WindowsFileSystem()).Scan(dir);Equal(r.Bytes,16L);Equal(r.SkippedLinks,1L);});
MaintenanceTests.Register(tests);
WindowsCleanupTests.Register(tests);
RecoveryTests.Register(tests);
DockerDeepTests.Register(tests);
AstraTests.Register(tests);
ConsumerTests.Register(tests);
ConsumerCompleteTests.Register(tests);
Respiro.Tests.OptimizationTests.Register(tests);
Respiro.Tests.ReleaseReadinessTests.Register(tests);
var operational = new List<(string,Action)>();
Respiro.Tests.GuestStartupTests.Register(operational);
Respiro.Tests.OperationalRegistryTests.Register(operational);
Respiro.Tests.OperationalProcessTests.Register(operational);
if (args.Contains("--run-operational-tests"))
{
    int manifest = Array.IndexOf(args, "--guest-manifest");
    try
    {
        if (manifest < 0 || manifest + 1 >= args.Length) throw new IOException("GUEST BLOCKED: manifesto do convidado ausente.");
        Respiro.Tests.GuestValidationGuard.Require(args[manifest + 1], args.Contains("--ack-disposable-guest"));
        tests.Clear(); // Guest runner needs only Desktop Runtime, not SDK-dependent developer fixtures.
        tests.AddRange(operational);
    }
    catch (Exception e) { Console.WriteLine(e.Message); return 1; }
}
else Console.WriteLine($"{operational.Count} cenários operacionais preservados e pendentes de convidado autorizado; não executados neste host.");
int failed=0;foreach(var (name,action) in tests){try{action();Console.WriteLine("PASS "+name);}catch(Exception ex){failed++;Console.WriteLine("FAIL "+name+": "+ex.Message);}}Console.WriteLine($"{tests.Count-failed}/{tests.Count} testes passaram.");return failed==0?0:1;

sealed class FakeFs(string root):IReadOnlyFileSystem
{
    readonly Dictionary<string,DiskEntry> entries=new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Denied=new(StringComparer.OrdinalIgnoreCase),Visited=new(StringComparer.OrdinalIgnoreCase);
    public bool SharedIdentity,UnknownAllocation;public Action? OnRead;public string Use="Uso não detectado";
    public void File(string relative,long size,FileAttributes attributes=0){var p=root+"\\"+relative;entries[p]=new(p,false,attributes,size,new DateTime(2026,1,1,0,0,0,DateTimeKind.Utc));}
    public void Directory(string relative,FileAttributes attributes=0){var p=root+"\\"+relative;entries[p]=new(p,true,attributes|FileAttributes.Directory,0,DateTime.UtcNow);}
    public DiskEntry Stat(string path)=>entries.TryGetValue(path,out var e)?e:new(path,true,FileAttributes.Directory,0,DateTime.UtcNow);
    public IEnumerable<DiskEntry> Enumerate(string path){Visited.Add(path);if(Denied.Contains(path))throw new UnauthorizedAccessException("Acesso negado simulado");foreach(var e in entries.Values.Where(e=>Path.GetDirectoryName(e.Path)==path)){OnRead?.Invoke();yield return e;}}
    public bool Exists(string path)=>entries.ContainsKey(path);
    public Allocation Allocated(string path)=>UnknownAllocation?new(null,null):new(Stat(path).Length,SharedIdentity?"shared":path);
    public string ProbeUse(string path)=>Use;
}

static class Native
{
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true,EntryPoint="CreateHardLinkW")]
    public static extern bool CreateHardLink(string link,string target,IntPtr reserved);
}



