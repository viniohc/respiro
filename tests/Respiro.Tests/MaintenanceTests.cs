using Respiro.Core;
using System.Runtime.InteropServices;
using System.Text.Json;
internal static class MaintenanceTests
{
    internal static void Register(List<(string, Action)> tests)
    {
        void Test(string n, Action a) => tests.Add(("M3: " + n, a));
        Test("prévia e simulação preservam arquivo e comprovante", () =>
        { var f = Fixture.Create(); var path = f.OldFile(); var engine = new CleanupService(f.Store); var plan = engine.Prepare([path]); Equal(plan.EligibleCount, 1); var results = engine.Run(plan, true); Equal(results[0].Status, "Simulado"); Require(File.Exists(path) && File.Exists(path + ".receipt.json")); });
        Test("remoção exclusivamente em fixture comprovada", () =>
        { var f = Fixture.Create(); var path = f.OldFile(); var engine = new CleanupService(f.Store); var plan = engine.Prepare([path]); var result = engine.Run(plan, false, true, plan.Confirmation); Equal(result[0].Status, "Removido"); Require(!File.Exists(path)); Require(File.Exists(path + ".receipt.json")); string log = File.ReadAllText(f.Store.JournalPath); Require(log.Contains("Intent") && log.Contains("Removed")); });
        Test("categoria não aprovada bloqueia", () =>
        { var f = Fixture.Create(); var p = f.OldFile(); var e = new CleanupService(f.Store); var plan = e.Prepare([p]); Throws(() => e.Run(plan, false, false, plan.Confirmation)); Require(File.Exists(p)); });
        Test("confirmação incorreta bloqueia", () =>
        { var f = Fixture.Create(); var p = f.OldFile(); var e = new CleanupService(f.Store); var plan = e.Prepare([p]); Throws(() => e.Run(plan, false, true, "APAGAR TUDO")); Require(File.Exists(p)); });
        Test("seleção vazia não autoriza operação", () =>
        { var f = Fixture.Create(); var e = new CleanupService(f.Store); var plan = e.Prepare([]); Equal(plan.EligibleCount, 0); Throws(() => e.Run(plan, false, true, plan.Confirmation)); });
        Test("plano é de uso único e pertence ao serviço", () =>
        { var f = Fixture.Create(); var e = new CleanupService(f.Store); var plan = e.Prepare([f.OldFile()]); Throws(() => new CleanupService(f.Store).Run(plan, false, true, plan.Confirmation)); e.Run(plan, false, true, plan.Confirmation); Throws(() => e.Run(plan, false, true, plan.Confirmation)); });
        Test("origem sem comprovante é inconclusiva", () =>
        { var f = Fixture.Create(); var p = Path.Combine(f.Store.Root, Guid.NewGuid().ToString("N") + ".tmp"); File.WriteAllText(p, "dados pessoais temporários"); File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddDays(-10)); Equal(new CleanupService(f.Store).Prepare([p]).EligibleCount, 0); Require(File.Exists(p)); });
        Test("comprovante adulterado bloqueia", () =>
        { var f = Fixture.Create(); var p = f.OldFile(); File.WriteAllText(p + ".receipt.json", "{invalid}"); Equal(new CleanupService(f.Store).Prepare([p]).EligibleCount, 0); });
        Test("temporário recente é preservado", () =>
        { var f = Fixture.Create(); var p = f.Store.CreateDiagnosticSnapshot("{}"); Equal(new CleanupService(f.Store).Prepare([p]).EligibleCount, 0); Require(File.Exists(p)); });
        Test("código e banco dentro da área não são candidatos", () =>
        { var f = Fixture.Create(); foreach (var n in new[] { "source.cs", "personal.db", "credentials.json", "document.tmp" }) { var p = Path.Combine(f.Store.Root, n); File.WriteAllText(p, "importante"); Equal(new CleanupService(f.Store).Prepare([p]).EligibleCount, 0); Require(File.Exists(p)); } });
        Test("pasta com nome de temporário não é removida", () =>
        { var f = Fixture.Create(); var p = Path.Combine(f.Store.Root, Guid.NewGuid().ToString("N") + ".tmp"); Directory.CreateDirectory(p); Equal(new CleanupService(f.Store).Prepare([p]).EligibleCount, 0); Require(Directory.Exists(p)); });
        Test("arquivo externo e prefixo semelhante são bloqueados", () =>
        { var f = Fixture.Create(); var outside = Path.Combine(f.Base, "owned-extra"); Directory.CreateDirectory(outside); var p = Path.Combine(outside, Guid.NewGuid().ToString("N") + ".tmp"); File.WriteAllText(p, "protegido"); Equal(new CleanupService(f.Store).Prepare([p]).EligibleCount, 0); Require(File.Exists(p)); });
        Test("caminhos relativos, UNC, ADS e segmentos ambíguos são recusados", () =>
        { foreach (var p in new[] { @"relative.tmp", @"\\server\share\x.tmp", @"C:\root\x.tmp:stream", @"C:\root\..\x.tmp", @"C:\root\x.tmp.", @"C:\root\x.tmp " }) Throws(() => LockedFile.Canonical(p)); });
        Test("metadados mudaram após prévia", () =>
        { var f = Fixture.Create(); var p = f.OldFile(); var e = new CleanupService(f.Store); var plan = e.Prepare([p]); File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddDays(-9)); Equal(e.Run(plan, false, true, plan.Confirmation)[0].Status, "Bloqueado"); Require(File.Exists(p)); });
        Test("conteúdo mudou com mesmo tamanho e mesma data", () =>
        { var f = Fixture.Create(); var p = f.OldFile(); var e = new CleanupService(f.Store); var plan = e.Prepare([p]); var date = File.GetLastWriteTimeUtc(p); var bytes = File.ReadAllBytes(p); bytes[^1] ^= 1; File.WriteAllBytes(p, bytes); File.SetLastWriteTimeUtc(p, date); Equal(e.Run(plan, false, true, plan.Confirmation)[0].Status, "Bloqueado"); Require(File.Exists(p)); });
        Test("substituição do arquivo após prévia é bloqueada", () =>
        { var f = Fixture.Create(); var p = f.OldFile(); var e = new CleanupService(f.Store); var plan = e.Prepare([p]); File.Move(p, p + ".preserved"); File.WriteAllText(p, "substituto protegido"); Equal(e.Run(plan, false, true, plan.Confirmation)[0].Status, "Bloqueado"); Require(File.Exists(p) && File.Exists(p + ".preserved")); });
        Test("arquivo em uso na prévia é bloqueado", () =>
        { var f = Fixture.Create(); var p = f.OldFile(); using var open = new FileStream(p, FileMode.Open, FileAccess.ReadWrite, FileShare.None); Equal(new CleanupService(f.Store).Prepare([p]).EligibleCount, 0); });
        Test("arquivo aberto após prévia não é removido", () =>
        { var f = Fixture.Create(); var p = f.OldFile(); var e = new CleanupService(f.Store); var plan = e.Prepare([p]); using var open = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read); Equal(e.Run(plan, false, true, plan.Confirmation)[0].Status, "Bloqueado"); Require(File.Exists(p)); });
        Test("somente leitura não é contornado", () =>
        { var f = Fixture.Create(); var p = f.OldFile(); File.SetAttributes(p, FileAttributes.ReadOnly); Equal(new CleanupService(f.Store).Prepare([p]).EligibleCount, 0); Require(File.GetAttributes(p).HasFlag(FileAttributes.ReadOnly)); });
        Test("hard link real bloqueia remoção", () =>
        { var f = Fixture.Create(); var p = f.OldFile(); Require(CreateHardLink(Path.Combine(f.Base, "shared.bin"), p, IntPtr.Zero)); Equal(new CleanupService(f.Store).Prepare([p]).EligibleCount, 0); Require(File.Exists(p)); });
        Test("ancestrais são fixados durante acesso", () =>
        { var f = Fixture.Create(); var p = f.OldFile(); using var handle = LockedFile.Open(p); Throws(() => f.MoveOwned(f.Store.Root + "-moved")); Require(File.Exists(p)); });
        Test("junction criada depois da prévia é bloqueada", () =>
        { var f = Fixture.Create(); var p = f.OldFile(); var e = new CleanupService(f.Store); var plan = e.Prepare([p]); f.MoveOwned(f.Store.Root + "-preserved"); var outside = Path.Combine(f.Base, "outside"); Directory.CreateDirectory(outside); var protectedFile = Path.Combine(outside, Path.GetFileName(p)); File.WriteAllText(protectedFile, "protegido"); Junction(f.Store.Root, outside); Equal(e.Run(plan, false, true, plan.Confirmation)[0].Status, "Bloqueado"); Require(File.Exists(protectedFile)); });
        Test("comprovante substituído por diretório é bloqueado", () =>
        { var f = Fixture.Create(); var p = f.OldFile(); var receipt = p + ".receipt.json"; File.Move(receipt, receipt + ".preserved"); Directory.CreateDirectory(receipt); Equal(new CleanupService(f.Store).Prepare([p]).EligibleCount, 0); });
        Test("marcador E2.5 impede remoção", () =>
        { var f = Fixture.Create(); var p = f.OldFile(); File.WriteAllText(Path.Combine(f.Store.Root, "E2.5"), "preservar"); Equal(new CleanupService(f.Store).Prepare([p]).EligibleCount, 0); Require(File.Exists(p)); });
        Test("marcador Git surgido após prévia bloqueia", () =>
        { var f = Fixture.Create(); var p = f.OldFile(); var e = new CleanupService(f.Store); var plan = e.Prepare([p]); File.WriteAllText(Path.Combine(f.Store.Root, ".git"), "gitdir: protected"); Equal(e.Run(plan, false, true, plan.Confirmation)[0].Status, "Bloqueado"); Require(File.Exists(p)); });
        Test("falha na auditoria impede remoção", () =>
        { var f = Fixture.Create(); var p = f.OldFile(); Directory.CreateDirectory(f.Store.JournalPath); var e = new CleanupService(f.Store); var plan = e.Prepare([p]); Equal(e.Run(plan, false, true, plan.Confirmation)[0].Status, "Bloqueado"); Require(File.Exists(p)); });
        Test("falha em um item não impede os demais", () =>
        { var f = Fixture.Create(); var p1 = f.OldFile(); var p2 = f.OldFile(); var e = new CleanupService(f.Store); var plan = e.Prepare([p1, p2]); using var open = new FileStream(p1, FileMode.Open, FileAccess.Read, FileShare.None); var results = e.Run(plan, false, true, plan.Confirmation); Equal(results[0].Status, "Bloqueado"); Equal(results[1].Status, "Removido"); Require(File.Exists(p1) && !File.Exists(p2)); });
        Test("cancelamento impede remoção dos próximos itens", () =>
        { var f = Fixture.Create(); var p = f.OldFile(); var e = new CleanupService(f.Store); var plan = e.Prepare([p]); using var c = new CancellationTokenSource(); c.Cancel(); Equal(e.Run(plan, false, true, plan.Confirmation, c.Token)[0].Status, "Cancelado"); Require(File.Exists(p)); });
        Test("repetição de caminho na seleção não duplica o plano", () =>
        { var f = Fixture.Create(); var p = f.OldFile(); Equal(new CleanupService(f.Store).Prepare([p, p]).Items.Count, 1); });
        Test("diagnóstico inacessível não reporta zero comprovado", () =>
        { var fs = new FakeFs(@"C:\fixture"); fs.Denied.Add(@"C:\fixture"); var row = new WindowsMaintenanceService(fs).Measure("Temporários", @"C:\fixture", "", "", null, default); Equal(row.Bytes, null); Require(row.Status.Contains("Parcial")); });
        Test("diagnóstico não atravessa junction", () =>
        { var fs = new FakeFs(@"C:\fixture"); fs.Directory("link", FileAttributes.ReparsePoint); fs.File(@"link\data.bin", 20); var row = new WindowsMaintenanceService(fs).Measure("Cache", @"C:\fixture", "", "", null, default); Require(!fs.Visited.Contains(@"C:\fixture\link")); Require(row.Status.Contains("links")); });
        Test("filtro de diagnóstico não conta dados fora da categoria", () =>
        { var fs = new FakeFs(@"C:\fixture"); fs.File("log.log", 10); fs.File("source.cs", 30); var row = new WindowsMaintenanceService(fs).Measure("Logs", @"C:\fixture", "", "", e => e.Path.EndsWith(".log"), default); Equal(row.Bytes, 10L); });
        Test("resposta oficial com acesso negado mantém estados inconclusivos", () =>
        { var r = new MaintenanceReport(); WindowsMaintenanceService.ApplyOfficial(r, """{"Store":[],"Features":[],"Pagefiles":[],"ShadowStorage":[],"RestorePoints":[],"Delivery":null,"Reserved":null,"Power":[],"Errors":["Restauracao: acesso negado"]}"""); Require(r.Diagnostics.Single(x => x.Category == "Pontos de restauração").Status.Contains("Inconclusivo")); Require(r.Diagnostics.Single(x => x.Category == "Armazenamento reservado").Bytes == null); });
        Test("auditoria cheia bloqueia sem apagar o temporário", () =>
        { var f = Fixture.Create(); var p = f.OldFile(); File.WriteAllBytes(f.Store.JournalPath, new byte[32]); var e = new CleanupService(f.Store, 16); var plan = e.Prepare([p]); Equal(e.Run(plan, false, true, plan.Confirmation)[0].Status, "Bloqueado"); Require(File.Exists(p)); });
        Test("listagem de área própria por junction é recusada", () =>
        { var f = Fixture.Create(); f.OldFile(); f.MoveOwned(f.Store.Root + "-saved"); Junction(f.Store.Root, f.Store.Root + "-saved"); Throws(() => f.Store.Enumerate().ToList()); });
        Test("prévia sob caminho E2.5 permanece bloqueada", () =>
        { var area = Fixture.Create(); var original = area.OldFile(); var e2 = Path.Combine(area.Base, "E2.5"); area.MoveOwned(e2); var store = new OwnedTemporaryStore(e2, Path.Combine(area.Base, "e2-audit.jsonl")); var p = Path.Combine(e2, Path.GetFileName(original)); Equal(new CleanupService(store).Prepare([p]).EligibleCount, 0); Require(File.Exists(p)); });
        Test("escopo exige a forma exata do caminho conhecido", () =>
        { var f = Fixture.Create(); var p = f.OldFile(); var alias = Path.Combine(f.Store.Root.ToUpperInvariant(), Path.GetFileName(p)); Equal(new CleanupService(f.Store).Prepare([alias]).EligibleCount, 0); Require(File.Exists(p)); });
        Test("Store e recursos nativos não são marcados como inúteis", () =>
        { var r = new MaintenanceReport(); WindowsMaintenanceService.ApplyOfficial(r, """{"Store":[{"Name":"Native.Framework","Publisher":"Microsoft","Version":"1.0","InstallLocation":"C:\\WindowsApps","IsFramework":true,"NonRemovable":true}],"Features":[{"Name":"Feature","Caption":"Recurso","InstallState":1}],"Pagefiles":[],"ShadowStorage":[],"RestorePoints":[],"Delivery":{"CacheSizeBytes":4096},"Reserved":{"ReservedStorageState":"Enabled"},"Power":[],"Errors":[]}"""); Equal(r.Applications.Count, 1); Equal(r.Applications[0].Bytes, null); Equal(r.Features[0].State, "Ativado"); Equal(r.Diagnostics.Single(x => x.Category == "Otimização de Entrega — API").Bytes, 4096L); });
    }
    static void Require(bool value) { if (!value) throw new Exception("Condição de segurança não atendida."); }
    static void Equal<T>(T actual, T expected) { if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw new Exception($"Esperado {expected}, obtido {actual}"); }
    static void Throws(Action action) { try { action(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; } throw new Exception("Operação deveria ter sido bloqueada."); }
    sealed record Fixture(string Base, OwnedTemporaryStore Store)
    {
        internal static Fixture Create()
        {
            var area = Path.Combine(Path.GetTempPath(), "Respiro.Missao3.Tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(area);
            var own = Path.Combine(area, "owned"); Directory.CreateDirectory(own);
            return new(area, new OwnedTemporaryStore(own, Path.Combine(area, "audit.jsonl")));
        }
        internal void MoveOwned(string destination)
        {
            string source = Path.GetFullPath(Store.Root), target = Path.GetFullPath(destination), boundary = Path.GetFullPath(Base) + Path.DirectorySeparatorChar;
            if (!source.StartsWith(boundary, StringComparison.OrdinalIgnoreCase) || !target.StartsWith(boundary, StringComparison.OrdinalIgnoreCase)) throw new IOException("Movimento fora da fixture.");
            Directory.Move(source, target);
        }
        internal string OldFile() { var p = Store.CreateDiagnosticSnapshot("{\"fixture\":true,\"kind\":\"diagnostic-snapshot\"}"); File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddDays(-10)); return p; }
    }
    static void Junction(string link, string target)
    {
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(start)!; p.WaitForExit(); if (p.ExitCode != 0) throw new Exception("Falha criando junction de teste.");
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")] static extern bool CreateHardLink(string link, string target, IntPtr reserved);
}




