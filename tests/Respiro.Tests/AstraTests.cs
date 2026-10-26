using System.Text.Json;
using Respiro.Core;

internal static class AstraTests
{
    static void Assert(bool value) { if (!value) throw new Exception("Resultado inesperado"); }
    static void Blocked(Action action) { try { action(); } catch (IOException) { return; } throw new Exception("Operação não bloqueada"); }
    static readonly DateTime When = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
    static RecoveryReport Report(long bytes = 300, DateTime? at = null)
    {
        var r = new RecoveryReport { Finished = at ?? When };
        r.Groups.Add(new() { Scope = new(@"C:\fixture\volume", "Dados", RecoveryClass.Protected, "Protegido", "Dados únicos", "Revisar"), PhysicalBytes = bytes });
        return r;
    }
    static string Sample(RecoveryReport r, DateTime at, long bytes, bool complete = true, string? hash = null) =>
        JsonSerializer.Serialize(new RecoverySample(at, r.Groups[0].Path, hash ?? RecoveryHistory.GroupHash(r, r.Groups[0]), bytes, complete)) + "\n";
    static string Fixture()
    {
        string p = Path.Combine(Path.GetTempPath(), "Respiro.MissaoAstra.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(p); return p;
    }
    public static void Register(List<(string, Action)> tests)
    {
        void Test(string name, Action test) => tests.Add(("Astra: " + name, test));
        Test("fixtures antigas e novas protegidas também sem marcador", () =>
        {
            foreach (string name in new[] { "LimpaPC.Missao5.Tests", "limpapc.missao6.tests", "Respiro.MissaoAstra.Tests" })
            {
                string p = @"C:\Temp\" + name + @"\x.dmp";
                Assert(RespiroDataPaths.IsTestArtifact(p));
                var env = new WindowsCleanupEnvironment(new() { [WindowsCleanupKind.UserTemporary] = [@"C:\Temp"] }, @"C:\journal.jsonl", administrator: false);
                Blocked(() => env.RequireScope(WindowsCleanupKind.UserTemporary, p));
                Blocked(() => env.RequireOriginalTemporary(p));
            }
            Assert(!RespiroDataPaths.IsTestArtifact(@"C:\Temp\app\x.dmp"));
        });
        Test("dados legados resolvidos sem mover", () =>
        {
            string local = Fixture(), legacy = Path.Combine(local, "LimpaPC");
            Directory.CreateDirectory(legacy); File.WriteAllText(Path.Combine(legacy, "evidence.txt"), "preserve");
            Assert(RespiroDataPaths.Resolve(local) == legacy && !Directory.Exists(Path.Combine(local, "Respiro")));
            Directory.CreateDirectory(Path.Combine(local, "Respiro"));
            Assert(RespiroDataPaths.Resolve(local) == Path.Combine(local, "Respiro") && File.ReadAllText(Path.Combine(legacy, "evidence.txt")) == "preserve");
        });
        Test("histórico ignora nulos, negativos e linhas malformadas", () =>
        {
            var r = Report();
            var samples = RecoveryHistoryReader.Parse("null\n{}\n{broken\n" + Sample(r, When.AddHours(-1), -1) + Sample(r, When.AddHours(-2), 100));
            Assert(samples.Count == 1 && samples[0].PhysicalBytes == 100);
        });
        Test("histórico usa data em vez da ordem das linhas", () =>
        {
            var r = Report();
            var samples = RecoveryHistoryReader.Parse(Sample(r, When.AddHours(-1), 200) + Sample(r, When.AddHours(-3), 50));
            Assert(RecoveryHistoryReader.Previous(samples, r, r.Groups[0], When)!.PhysicalBytes == 200);
        });
        Test("amostra incompatível recente não oculta amostra válida", () =>
        {
            var r = Report();
            var samples = RecoveryHistoryReader.Parse(Sample(r, When.AddHours(-2), 100) + Sample(r, When.AddHours(-1), 250, hash: "other"));
            Assert(RecoveryHistoryReader.Previous(samples, r, r.Groups[0], When)!.PhysicalBytes == 100);
        });
        Test("futuro, mesmo instante e amostra parcial não são baseline", () =>
        {
            var r = Report();
            var samples = RecoveryHistoryReader.Parse(Sample(r, When.AddHours(1), 10) + Sample(r, When, 20) + Sample(r, When.AddHours(-1), 30, false));
            Assert(RecoveryHistoryReader.Previous(samples, r, r.Groups[0], When) == null);
        });
        Test("escopo alterado impede falsa tendência", () =>
        {
            var r = Report(); var samples = RecoveryHistoryReader.Parse(Sample(r, When.AddDays(-1), 10));
            r.Groups.Add(new() { Scope = new(@"C:\fixture\volume\child", "Filho", RecoveryClass.Protected, "", "", "") });
            Assert(RecoveryHistoryReader.Previous(samples, r, r.Groups[0], When) == null);
        });
        Test("persistência calcula intervalo e diferença física", () =>
        {
            string path = Path.Combine(Fixture(), "growth.jsonl"); var history = new RecoveryHistory(path);
            history.Record(Report(100, When.AddHours(-2))); var r = Report(); history.Record(r);
            Assert(r.Groups[0].GrowthBytes == 200 && r.Groups[0].GrowthObservedHours == 2 &&
                r.Groups[0].GrowthBaselineUtc == When.AddHours(-2) && File.ReadAllLines(path).Length == 2);
        });
        Test("reutilizar relatório parcial limpa resultado antigo", () =>
        {
            var history = new RecoveryHistory(Path.Combine(Fixture(), "growth.jsonl"));
            history.Record(Report(100, When.AddHours(-2))); var r = Report(); history.Record(r);
            r.Groups[0].Partial = true; r.Finished = When.AddHours(1); history.Record(r);
            Assert(r.Groups[0].GrowthBytes == null && r.Groups[0].GrowthBaselineUtc == null && r.Groups[0].GrowthObservedHours == null);
        });
        Test("queda de tamanho preserva sinal sem autorizar limpeza", () =>
        {
            var history = new RecoveryHistory(Path.Combine(Fixture(), "growth.jsonl"));
            history.Record(Report(500, When.AddHours(-2))); var r = Report(); history.Record(r);
            Assert(r.Groups[0].GrowthBytes == -200 && r.ConfirmedBytes == 0);
        });
        Test("histórico sem newline não concatena amostras", () =>
        {
            string p = Path.Combine(Fixture(), "growth.jsonl"); var r = Report();
            File.WriteAllText(p, Sample(r, When.AddHours(-2), 100).TrimEnd('\n')); new RecoveryHistory(p).Record(r);
            Assert(RecoveryHistoryReader.Parse(File.ReadAllText(p)).Count == 2 && r.Groups[0].GrowthBytes == 200);
        });
        Test("limite do histórico recusa lote inteiro sem alterar arquivo", () =>
        {
            string p = Path.Combine(Fixture(), "growth.jsonl");
            File.WriteAllText(p, new string(' ', RecoveryHistoryReader.Limit - 1) + "\n");
            var before = File.ReadAllBytes(p); Blocked(() => new RecoveryHistory(p).Record(Report()));
            Assert(File.ReadAllBytes(p).SequenceEqual(before));
        });
        Test("alertas escolhem baseline compatível dentro do período", () =>
        {
            var r = Report(3L << 30);
            string input = Sample(r, When.AddHours(-26), 1L << 30) + Sample(r, When.AddHours(-25), 0, hash: "other");
            var alerts = StorageAlertEvaluator.EvaluateHistory(r, new(), 30L << 30, new DateTimeOffset(When), input);
            Assert(alerts.Single().Kind == "Crescimento" && alerts[0].ObservedHours == 26);
        });
        Test("alerta aceita exato limite temporal e rejeita cancelamento", () =>
        {
            var r = Report(3L << 30); string input = Sample(r, When.AddHours(-24), 1L << 30);
            Assert(StorageAlertEvaluator.EvaluateHistory(r, new(), 30L << 30, new DateTimeOffset(When), input).Single().Kind == "Crescimento");
            r.Cancelled = true;
            Assert(StorageAlertEvaluator.EvaluateHistory(r, new(), 30L << 30, new DateTimeOffset(When), input).All(x => x.Kind != "Crescimento"));
        });
        Test("margem de build não altera reserva configurada", () =>
        {
            Assert(new StorageAlertSettings().MinimumFreeBytes == 10L << 30);
            Assert(StorageAlertEvaluator.BuildMarginWarning(11L << 30) != null);
            Assert(StorageAlertEvaluator.BuildMarginWarning(12L << 30) == null && StorageAlertEvaluator.BuildMarginWarning(null) == null);
            Assert(StorageAlertEvaluator.EvaluateFree(new(), -1).Single().Kind == "Inconclusivo");
        });
        Test("destinos oficiais recusam entrada arbitrária", () =>
        {
            Blocked(() => OfficialMaintenanceCatalog.Resolve("cmd.exe /c del"));
            Blocked(() => OfficialMaintenanceCatalog.Resolve("ms-settings:windowsupdate-action"));
            Assert(OfficialMaintenanceCatalog.Resolve("startup").Target == "ms-settings:startupapps");
            Assert(OfficialMaintenanceCatalog.Options.All(x => !string.IsNullOrWhiteSpace(x.Review) && !string.IsNullOrWhiteSpace(x.Consequences)));
            Assert(OfficialMaintenanceCatalog.Options.Select(x => x.Id).Distinct().Count() == OfficialMaintenanceCatalog.Options.Count);
        });
        Test("CIM de inicialização não expõe comandos nem promete economia", () =>
        {
            var report = new MaintenanceReport();
            WindowsMaintenanceService.ApplyOfficial(report, "{\"Startup\":[{\"Name\":\"Example\",\"Location\":\"HKCU\",\"Command\":\"private-token\"}]}");
            var row = report.Diagnostics.Single(x => x.Category == "Aplicativo na inicialização");
            Assert(row.Bytes == null && row.Status.Contains("Example") && !JsonSerializer.Serialize(report).Contains("private-token"));
        });
        Test("avisos de relatório histórico são preservados ao reabrir", () =>
        {
            const string oldJson = "{\"Finished\":\"2026-10-24T21:08:00-03:00\",\"Groups\":[],\"Notices\":[\"Medição parcial; não autoriza limpeza.\"]}";
            var old = JsonSerializer.Deserialize<RecoveryReport>(oldJson)!;
            Assert(old.Notices.Count == 1 && old.Notices[0].Contains("não autoriza"));
            var current = Report(); current.Notices.Add("Escopo protegido; revalidar antes de agir.");
            var copy = JsonSerializer.Deserialize<RecoveryReport>(JsonSerializer.Serialize(current))!;
            Assert(copy.Notices.SequenceEqual(current.Notices) && copy.Groups.Count == current.Groups.Count);
            Assert(JsonSerializer.Deserialize<RecoveryReport>("{\"Groups\":[]}")!.Notices.Count == 0);
        });
        Test("limite de cópias próprias preserva todos os dados", () =>
        {
            var store = new OwnedTemporaryStore(Path.Combine(Fixture(), "Scratch"), null);
            for (int i = 0; i < 100; i++) store.CreateDiagnosticSnapshot("{}");
            var before = Directory.GetFiles(store.Root).Order().ToArray(); Blocked(() => store.CreateDiagnosticSnapshot("{}"));
            Assert(Directory.GetFiles(store.Root).Order().SequenceEqual(before));
        });
    }
}
