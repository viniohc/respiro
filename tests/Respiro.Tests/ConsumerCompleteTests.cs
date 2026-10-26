using System.Text.Json;
using Respiro.Core;
internal static class ConsumerCompleteTests
{
    static void Check(bool value) { if (!value) throw new Exception("Condição não atendida"); }
    static void Blocked(Action action) { try { action(); } catch (IOException) { return; } throw new Exception("Operação deveria ser bloqueada"); }
    static readonly DateTime Now = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
    static string Fixture()
    {
        string path = Path.Combine(Path.GetTempPath(), "Respiro.MissaoConsumerContent.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path); return path;
    }
    static FileRow Write(string root, string name, byte[] data)
    {
        string path = Path.Combine(root, name); File.WriteAllBytes(path, data); return Row(path);
    }
    static FileRow Row(string path) => new(path, new FileInfo(path).Length, null, "Pessoal", Nature.Protegido, "Preservado", File.GetLastWriteTimeUtc(path));
    static StorageTimelineSample Sample(long free = 2000, DateTime? at = null, long folder = 100, bool complete = true) =>
        new(at ?? Now, @"C:\", "1234ABCD", 10000, free, @"C:\Photos", complete, [new(@"C:\Photos\Album", folder)]);
    public static void Register(List<(string, Action)> tests)
    {
        void Test(string name, Action action) => tests.Add(("Uso comum: " + name, action));
        Test("hash confirma conteúdo igual mesmo com nomes diferentes sem apagar", () =>
        {
            string root = Fixture(); var a = Write(root, "video.bin", [1,2,3]); var b = Write(root, "copia.bin", [1,2,3]);
            var report = new DuplicateContentService().Compare([a,b]);
            Check(report.ConfirmedFiles == 2 && report.Files[0].Group == report.Files[1].Group && File.Exists(a.Path) && File.Exists(b.Path));
        });
        Test("mesmo tamanho com conteúdo diferente não vira duplicata", () =>
        {
            string root = Fixture(); var a = Write(root, "a.bin", [1,2,3]); var b = Write(root, "b.bin", [3,2,1]);
            var report = new DuplicateContentService().Compare([a,b]);
            Check(report.ConfirmedFiles == 0 && report.Files.All(r => r.State == "Conteúdo diferente"));
        });
        Test("hash respeita limite total e não confirma pares incompletos", () =>
        {
            string root = Fixture(); var a = Write(root, "a.bin", [1,2,3]); var b = Write(root, "b.bin", [1,2,3]);
            var report = new DuplicateContentService().Compare([a,b], new(MaximumReadBytes: 3));
            Check(report.Limited && report.ReadBytes == 3 && report.ConfirmedFiles == 0);
        });
        Test("hash cancela durante leitura sem confirmar conteúdo parcial", () =>
        {
            string root = Fixture(); var a = Write(root, "a.bin", new byte[131072]); var b = Write(root, "b.bin", new byte[131072]);
            using var cancel = new CancellationTokenSource();
            var progress = new InlineProgress<DuplicateProgress>(p => { if (p.ReadBytes > 0) cancel.Cancel(); });
            var report = new DuplicateContentService().Compare([a,b], progress: progress, token: cancel.Token);
            Check(report.Cancelled && report.ConfirmedFiles == 0 && report.ReadBytes < a.Bytes + b.Bytes);
        });
        Test("arquivo alterado após análise não confirma cópia", () =>
        {
            string root = Fixture(); var a = Write(root, "a.bin", [1,2,3]); var b = Write(root, "b.bin", [1,2,3]);
            File.SetLastWriteTimeUtc(a.Path, DateTime.UtcNow.AddHours(1));
            var report = new DuplicateContentService().Compare([a,b]);
            Check(report.Files[0].SHA256 == null && report.ConfirmedFiles == 0);
        });
        Test("marcador legado e fluxo adicional bloqueiam hash", () =>
        {
            string root = Fixture(); var a = Write(root, "a.bin", [1,2,3]); var b = Write(root, "b.bin", [1,2,3]);
            File.WriteAllText(Path.Combine(root, ".limpapc-protect"), "preserve");
            Check(new DuplicateContentService().Compare([a,b]).ReadBytes == 0);
            string other = Fixture(); var c = Write(other, "a.bin", [1,2,3]); var d = Write(other, "b.bin", [1,2,3]);
            File.WriteAllText(c.Path + ":personal", "preserve");
            Check(new DuplicateContentService().Compare([Row(c.Path),d]).Files[0].SHA256 == null);
        });
        Test("áreas e formatos protegidos não são lidos para hash", () =>
        {
            foreach (string path in new[] { @"C:\Users\x\.codex\a.bin", @"C:\work\E2.5\a.bin", @"C:\work\db.sqlite", @"C:\work\private.key", @"C:\work\docker\disk.vhdx" })
                Blocked(() => DuplicateContentService.RequireReadablePersonalPath(path));
        });
        Test("lista limitada e arquivos vazios não sugerem recuperação", () =>
        {
            string root = Fixture(); var a = Write(root, "a.bin", []); var b = Write(root, "b.bin", []); var c = Write(root, "c.bin", []);
            var report = new DuplicateContentService().Compare([a,b,c], new(MaximumFiles: 2));
            Check(report.Limited && report.Files.Count == 2 && report.ReadBytes == 0 && report.ConfirmedFiles == 0);
        });
        Test("inventário separa tradicionais e Store sem excluir desconhecidos", () =>
        {
            var a = new InstalledApp("A", "Vendor", "1", 12, "", "Tradicional", "");
            var b = new InstalledApp("B", "Other", "1", null, "", "Microsoft Store / MSIX", "");
            Check(ApplicationInventoryView.Filter([a,b], "", 1).Single() == a);
            Check(ApplicationInventoryView.Filter([a,b], "", 2).Single() == b);
            Check(ApplicationInventoryView.Filter([a,b], "other", 0).Single() == b);
        });
        Test("data de instalador ausente ou inválida não é inventada", () =>
        {
            Check(ApplicationInventoryView.InstallationDate(null) == "Não informada");
            Check(ApplicationInventoryView.InstallationDate("20260230") == "Não informada");
            Check(ApplicationInventoryView.InstallationDate("20261025") == "25/10/2026");
        });
        Test("aplicativo legado sem data continua legível", () =>
        {
            var old = JsonSerializer.Deserialize<InstalledApp>("""{"Name":"A","Publisher":"Vendor","Version":"1","Bytes":null,"Location":"","Kind":"Tradicional","Notes":""}""")!;
            Check(old.InstallerDate == null && old.InstallationDateText == "Não informada" && old.Size == "Não informado");
        });
        Test("mesmo fabricante é pista e fabricante ausente não cria relação", () =>
        {
            var a = new InstalledApp("A", "Vendor", "1", null, "", "Tradicional", "");
            var b = a with { Name = "B" };
            Check(ApplicationInventoryView.Related(a,[a,b]).Contains("não prova"));
            Check(!ApplicationInventoryView.Related(a with { Publisher = "Não informado" },[a,b]).Contains("Mesmo fabricante"));
        });
        Test("histórico inválido não vira amostra de espaço zero", () =>
        {
            Check(StorageTimeline.Parse("null\n{}\n{broken\n").Count == 0);
            Check(StorageTimeline.Parse(JsonSerializer.Serialize(Sample(-1))).Count == 0);
        });
        Test("histórico escolhe data válida e compara crescimento sem atribuição", () =>
        {
            var current = Sample(folder: 300);
            var previous = new[] { Sample(4000, Now.AddHours(-2), 100), Sample(3000, Now.AddHours(-1), 200), Sample(1000, Now.AddHours(1), 400) };
            var compared = StorageTimeline.Compare(current, previous);
            Check(compared.Previous!.Free == 3000 && compared.Growth.Single().Occupied == 100 && compared.Explanation.Contains("desconhecida"));
        });
        Test("mudança de volume ou capacidade impede falsa comparação", () =>
        {
            Check(StorageTimeline.Compare(Sample(), [Sample(at: Now.AddHours(-1)) with { Serial = "FFFFABCD" }]).Previous == null);
            Check(StorageTimeline.Compare(Sample(), [Sample(at: Now.AddHours(-1)) with { Capacity = 20000 }]).Previous == null);
        });
        Test("análise parcial ou outro escopo não prova crescimento de pasta", () =>
        {
            var old = Sample(at: Now.AddHours(-1));
            Check(StorageTimeline.Compare(Sample(folder: 300, complete: false), [old]).Growth.Count == 0);
            Check(StorageTimeline.Compare(Sample(folder: 300), [old with { Scope = @"C:\Other" }]).Growth.Count == 0);
        });
        Test("histórico cheio não sobrescreve nem remove amostras", () =>
        {
            string root = Fixture(), path = Path.Combine(root, "space-timeline.jsonl");
            string content = string.Concat(Enumerable.Repeat(JsonSerializer.Serialize(Sample()) + "\n", StorageTimeline.SampleLimit));
            File.WriteAllText(path, content);
            Blocked(() => StorageTimeline.Append(path, Sample()));
            Check(File.ReadAllText(path) == content);
        });
        Test("histórico preserva última linha incompleta e separa nova amostra", () =>
        {
            string path = Path.Combine(Fixture(), "space-timeline.jsonl");
            File.WriteAllText(path, "{incomplete");
            StorageTimeline.Append(path, Sample());
            Check(File.ReadAllText(path).StartsWith("{incomplete\n") && StorageTimeline.Read(path).Count == 1);
        });
        Test("manutenção cotidiana explica espaço e reversão sem alterar destinos", () =>
        {
            Check(OfficialMaintenanceCatalog.Options.All(o => o.Purpose.Length > 0 && o.SpaceImpact.Length > 0 && o.Reversibility.Length > 0));
            Check(OfficialMaintenanceCatalog.Resolve("startup").SpaceImpact.Contains("não libera"));
        });
    }
    sealed class InlineProgress<T>(Action<T> action) : IProgress<T>
    { public void Report(T value) => action(value); }
}
