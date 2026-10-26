using System.Buffers.Binary;
using System.Text;
using Respiro.Core;
internal static class WindowsCleanupTests
{
    internal static void Register(List<(string, Action)> tests)
    {
        void Test(string n, Action a) => tests.Add(("M5: " + n, a));
        Test("minidump estrutural em temporário é removível somente após confirmação", () => { var f = Fixture.Create(); string p = f.Dump(); var s = f.Service(); var plan = s.Prepare(WindowsCleanupKind.UserTemporary, [p]); Equal(plan.EligibleCount, 1); Require(plan.EstimatedRecoverable > 0); Throws(() => s.Run(plan, false)); Require(File.Exists(p)); var r = s.Run(plan, false, true, plan.Confirmation); Equal(r.State, "Concluída"); Equal(r.EstimatedReleasedBytes, plan.EstimatedRecoverable); Require(!File.Exists(p)); });
        Test("simulação não remove nem altera arquivo", () => { var f = Fixture.Create(); string p = f.Dump(); byte[] before = File.ReadAllBytes(p); var s = f.Service(); var plan = s.Prepare(WindowsCleanupKind.UserTemporary, [p]); var r = s.Run(plan, true); Require(r.Simulated && r.EstimatedReleasedBytes == 0 && before.SequenceEqual(File.ReadAllBytes(p))); });
        Test("temporário genérico não vira removível por nome ou idade", () => { var f = Fixture.Create(); var p = f.Write(f.Temp, "generic.tmp", new byte[128]); Old(p); Equal(f.Service().Prepare(WindowsCleanupKind.UserTemporary, [p]).EligibleCount, 0); });
        Test("assinatura MDMP isolada é insuficiente", () => { var f = Fixture.Create(); var p = f.Write(f.Temp, "fake.dmp", Encoding.ASCII.GetBytes("MDMP" + new string('x', 96))); Old(p); Equal(f.Service().Prepare(WindowsCleanupKind.UserTemporary, [p]).EligibleCount, 0); });
        Test("streams fora do arquivo bloqueiam dump", () => { var f = Fixture.Create(); var p = f.Dump(); var b = File.ReadAllBytes(p); U32(b, 40, uint.MaxValue); File.WriteAllBytes(p, b); Old(p); Equal(f.Service().Prepare(WindowsCleanupKind.UserTemporary, [p]).EligibleCount, 0); });
        Test("dump recente bloqueado pela data interna", () => { var f = Fixture.Create(); var p = f.Dump(); var b = File.ReadAllBytes(p); U32(b, 20, (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds()); File.WriteAllBytes(p, b); Old(p); Equal(f.Service().Prepare(WindowsCleanupKind.UserTemporary, [p]).EligibleCount, 0); });
        Test("cache CMMM estrutural tem adaptador específico", () => { var f = Fixture.Create(); var p = f.Thumbnail(); var s = f.Service(); var plan = s.Prepare(WindowsCleanupKind.Thumbnails, [p]); Equal(plan.EligibleCount, 1); Equal(s.Run(plan, false, true, plan.Confirmation).State, "Concluída"); Require(!File.Exists(p)); });
        Test("SQLite disfarçado de miniatura é preservado", () => { var f = Fixture.Create(); var p = f.Write(f.Thumbs, "thumbcache_32.db", Encoding.UTF8.GetBytes("SQLite format 3\0" + new string('x', 128))); Equal(f.Service().Prepare(WindowsCleanupKind.Thumbnails, [p]).EligibleCount, 0); Require(File.Exists(p)); });
        Test("versão CMMM desconhecida é bloqueada", () => { var f = Fixture.Create(); var p = f.Thumbnail(); var b = File.ReadAllBytes(p); U32(b, 4, 99); File.WriteAllBytes(p, b); Equal(f.Service().Prepare(WindowsCleanupKind.Thumbnails, [p]).EligibleCount, 0); });
        Test("entrada CMMM inválida é bloqueada", () => { var f = Fixture.Create(); var p = f.Thumbnail(); var b = File.ReadAllBytes(p); U32(b, 28, uint.MaxValue); File.WriteAllBytes(p, b); Equal(f.Service().Prepare(WindowsCleanupKind.Thumbnails, [p]).EligibleCount, 0); });
        Test("banco pessoal fora do cache permanece protegido", () => { var f = Fixture.Create(); var p = f.Write(f.Temp, "personal.db", new byte[128]); Equal(f.Service().Prepare(WindowsCleanupKind.Thumbnails, [p]).EligibleCount, 0); Equal(f.Service().Prepare(WindowsCleanupKind.UserTemporary, [p]).EligibleCount, 0); });
        Test("arquivo em uso é bloqueado na prévia", () => { var f = Fixture.Create(); var p = f.Dump(); using var h = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read); Equal(f.Service().Prepare(WindowsCleanupKind.UserTemporary, [p]).EligibleCount, 0); });
        Test("uso iniciado após a prévia impede remoção", () => { var f = Fixture.Create(); var p = f.Dump(); var s = f.Service(); var plan = s.Prepare(WindowsCleanupKind.UserTemporary, [p]); using var h = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.Read); Equal(s.Run(plan, false, true, plan.Confirmation).State, "Não executada"); Require(File.Exists(p)); });
        Test("alteração do conteúdo com mesma data/tamanho impede remoção", () => { var f = Fixture.Create(); var p = f.Dump(); var s = f.Service(); var plan = s.Prepare(WindowsCleanupKind.UserTemporary, [p]); var date = File.GetLastWriteTimeUtc(p); var b = File.ReadAllBytes(p); b[^1] ^= 1; File.WriteAllBytes(p, b); File.SetLastWriteTimeUtc(p, date); Equal(s.Run(plan, false, true, plan.Confirmation).State, "Não executada"); Require(File.Exists(p)); });
        Test("arquivo substituído não é removido", () => { var f = Fixture.Create(); var p = f.Dump(); var s = f.Service(); var plan = s.Prepare(WindowsCleanupKind.UserTemporary, [p]); File.Move(p, p + ".preserved"); File.WriteAllText(p, "important"); Equal(s.Run(plan, false, true, plan.Confirmation).State, "Não executada"); Require(File.Exists(p)); });
        Test("hard link impede remoção", () => { var f = Fixture.Create(); var p = f.Dump(); Require(Native.CreateHardLink(Path.Combine(f.Base, "shared.dmp"), p, IntPtr.Zero)); Equal(f.Service().Prepare(WindowsCleanupKind.UserTemporary, [p]).EligibleCount, 0); });
        Test("fluxo NTFS adicional protege conteúdo", () => { var f = Fixture.Create(); var p = f.Dump(); File.WriteAllText(p + ":personal", "protected"); Equal(f.Service().Prepare(WindowsCleanupKind.UserTemporary, [p]).EligibleCount, 0); Require(File.Exists(p)); });
        Test("somente leitura não é contornado", () => { var f = Fixture.Create(); var p = f.Dump(); File.SetAttributes(p, FileAttributes.ReadOnly); Equal(f.Service().Prepare(WindowsCleanupKind.UserTemporary, [p]).EligibleCount, 0); Require(File.GetAttributes(p).HasFlag(FileAttributes.ReadOnly)); });
        Test("Downloads não é escopo de limpeza", () => { var f = Fixture.Create(); var dir = Path.Combine(f.Temp, "Downloads"); Directory.CreateDirectory(dir); var p = f.Dump(dir); Equal(f.Service().Prepare(WindowsCleanupKind.UserTemporary, [p]).EligibleCount, 0); });
        Test("E2.5 e projetos Git continuam bloqueados", () => { var f = Fixture.Create(); var p = f.Dump(); File.WriteAllText(Path.Combine(f.Temp, ".git"), "protected"); Equal(f.Service().Prepare(WindowsCleanupKind.UserTemporary, [p]).EligibleCount, 0); });
        Test("proteção surgida após a prévia bloqueia", () => { var f = Fixture.Create(); var p = f.Dump(); var s = f.Service(); var plan = s.Prepare(WindowsCleanupKind.UserTemporary, [p]); File.WriteAllText(Path.Combine(f.Temp, "E2.5"), "protected"); Equal(s.Run(plan, false, true, plan.Confirmation).State, "Não executada"); Require(File.Exists(p)); });
        Test("cancelamento entre operações preserva itens seguintes", () => { var f = Fixture.Create(); var a = f.Dump(); var b = f.Dump(); var s = f.Service(); var plan = s.Prepare(WindowsCleanupKind.UserTemporary, [a, b]); using var c = new CancellationTokenSource(); s.BeforeItemForTest = i => { if (i == 1) c.Cancel(); }; var r = s.Run(plan, false, true, plan.Confirmation, c.Token); Require(r.State.StartsWith("Parcial")); Require(!File.Exists(a) && File.Exists(b)); });
        Test("falha individual permite os demais e retorna parcial", () => { var f = Fixture.Create(); var a = f.Dump(); var b = f.Dump(); var s = f.Service(); var plan = s.Prepare(WindowsCleanupKind.UserTemporary, [a, b]); using var open = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.Read); var r = s.Run(plan, false, true, plan.Confirmation); Equal(r.State, "Parcial"); Require(File.Exists(a) && !File.Exists(b)); });
        Test("auditoria indisponível impede exclusão", () => { var f = Fixture.Create(); var p = f.Dump(); Directory.CreateDirectory(f.Journal); var s = f.Service(); var plan = s.Prepare(WindowsCleanupKind.UserTemporary, [p]); Equal(s.Run(plan, false, true, plan.Confirmation).State, "Não executada"); Require(File.Exists(p)); });
        Test("prévia expirada não pode executar", () => { var f = Fixture.Create(); var s = f.Service(); var p = f.Dump(); var plan = s.Prepare(WindowsCleanupKind.UserTemporary, [p]); f.Clock.Now = f.Clock.Now.AddMinutes(6); Throws(() => s.Run(plan, false, true, plan.Confirmation)); Require(File.Exists(p)); });
        Test("plano de uso único impede repetição", () => { var f = Fixture.Create(); var s = f.Service(); var plan = s.Prepare(WindowsCleanupKind.UserTemporary, [f.Dump()]); s.Run(plan, false, true, plan.Confirmation); Throws(() => s.Run(plan, false, true, plan.Confirmation)); });
        Test("nenhuma seleção não autoriza ação", () => { var f = Fixture.Create(); var s = f.Service(); var plan = s.Prepare(WindowsCleanupKind.UserTemporary, []); Equal(plan.Items.Count, 0); Throws(() => s.Run(plan, false, true, plan.Confirmation)); });
        Test("categorias opacas permanecem bloqueadas", () => { var f = Fixture.Create(); var p = f.Dump(); foreach (var kind in new[] { WindowsCleanupKind.DirectXShaders, WindowsCleanupKind.DeliveryOptimization, WindowsCleanupKind.KnownLogs }) Equal(f.Service().Prepare(kind, [p]).EligibleCount, 0); });
        Test("categoria administrativa não eleva o processo", () => { var f = Fixture.Create(); var p = f.Dump(f.WindowsTemp); Equal(f.Service().Prepare(WindowsCleanupKind.WindowsTemporary, [p]).EligibleCount, 0); var elevatedFixture = f.Service(true); var plan = elevatedFixture.Prepare(WindowsCleanupKind.WindowsTemporary, [p]); Equal(plan.EligibleCount, 1); Equal(elevatedFixture.Run(plan, false, true, plan.Confirmation).State, "Concluída"); });
        Test("minidump de área de erros validado", () => { var f = Fixture.Create(); var p = f.Dump(f.Dumps); var s = f.Service(); var plan = s.Prepare(WindowsCleanupKind.ErrorReports, [p]); Equal(plan.EligibleCount, 1); s.Run(plan, false, true, plan.Confirmation); Require(!File.Exists(p)); });
        Test("relatório WER textual sem prova permanece bloqueado", () => { var f = Fixture.Create(); var p = f.Write(f.Dumps, "Report.wer", Encoding.UTF8.GetBytes("Version=1\nReportIdentifier=" + Guid.NewGuid())); Equal(f.Service().Prepare(WindowsCleanupKind.ErrorReports, [p]).EligibleCount, 0); });
        Test("Lixeira exige confirmação por arquivo", () => { var f = Fixture.Create(); var p = f.Recycled(); var s = f.Service(); var plan = s.Prepare(WindowsCleanupKind.RecycleBin, [p]); Equal(plan.EligibleCount, 1); Throws(() => s.Run(plan, false, true, plan.Confirmation)); Require(File.Exists(p)); var r = s.Run(plan, false, true, plan.Confirmation, recycleConfirmedPaths: new HashSet<string> { p }); Equal(r.State, "Concluída"); Require(!File.Exists(p) && !File.Exists(f.IndexFor(p))); });
        Test("simulação da Lixeira mantém os dois arquivos", () => { var f = Fixture.Create(); var p = f.Recycled(); var s = f.Service(); var plan = s.Prepare(WindowsCleanupKind.RecycleBin, [p]); s.Run(plan, true); Require(File.Exists(p) && File.Exists(f.IndexFor(p))); });
        Test("Lixeira com origem em Downloads é bloqueada", () => { var f = Fixture.Create(); var original = Path.Combine(f.Temp, "Downloads", "file.bin"); Directory.CreateDirectory(Path.GetDirectoryName(original)!); var p = f.Recycled(original); Equal(f.Service().Prepare(WindowsCleanupKind.RecycleBin, [p]).EligibleCount, 0); });
        Test("Lixeira preserva código e bancos mesmo de Temp", () => { var f = Fixture.Create(); foreach (var name in new[] { "main.rs", "state.sqlite", "auth.json", "private.key" }) { var p = f.Recycled(Path.Combine(f.Temp, name)); Equal(f.Service().Prepare(WindowsCleanupKind.RecycleBin, [p]).EligibleCount, 0); } });
        Test("Lixeira sem metadados correspondentes é inconclusiva", () => { var f = Fixture.Create(); var p = f.Write(f.Bin, "$R-no-index.bin", new byte[128]); Equal(f.Service().Prepare(WindowsCleanupKind.RecycleBin, [p]).EligibleCount, 0); });
        Test("metadados de Lixeira alterados invalidam prévia", () => { var f = Fixture.Create(); var p = f.Recycled(); var s = f.Service(); var plan = s.Prepare(WindowsCleanupKind.RecycleBin, [p]); File.WriteAllText(f.IndexFor(p), "changed"); Equal(s.Run(plan, false, true, plan.Confirmation, recycleConfirmedPaths: new HashSet<string> { p }).State, "Não executada"); Require(File.Exists(p)); });
        Test("metadados em uso impedem remoção do payload", () => { var f = Fixture.Create(); var p = f.Recycled(); var s = f.Service(); var plan = s.Prepare(WindowsCleanupKind.RecycleBin, [p]); using var open = new FileStream(f.IndexFor(p), FileMode.Open, FileAccess.Read, FileShare.Read); Equal(s.Run(plan, false, true, plan.Confirmation, recycleConfirmedPaths: new HashSet<string> { p }).State, "Não executada"); Require(File.Exists(p)); });
        Test("pastas inteiras da Lixeira permanecem intactas", () => { var f = Fixture.Create(); var p = Path.Combine(f.Bin, "$R-folder"); Directory.CreateDirectory(p); File.WriteAllText(Path.Combine(p, "important.db"), "protected"); Equal(f.Service().Prepare(WindowsCleanupKind.RecycleBin, [p]).EligibleCount, 0); Require(Directory.Exists(p)); });
        Test("origem original ausente bloqueia por dependências inconclusivas", () => { var f = Fixture.Create(); var p = f.Recycled(Path.Combine(f.Temp, "missing", "item.bin")); Equal(f.Service().Prepare(WindowsCleanupKind.RecycleBin, [p]).EligibleCount, 0); });
        Test("falha no registro final não duplica nem oculta o espaço estimado", () => { var f = Fixture.Create(); var p = f.Dump(); var s = f.Service(); var plan = s.Prepare(WindowsCleanupKind.UserTemporary, [p]); s.BeforeAuditForTest = action => { if (action == "Removed") throw new IOException("Falha final simulada"); }; var r = s.Run(plan, false, true, plan.Confirmation); Equal(r.State, "Parcial"); Equal(r.EstimatedReleasedBytes, plan.EstimatedRecoverable); Require(!File.Exists(p)); });
        Test("fluxo NTFS adicionado após prévia bloqueia", () => { var f = Fixture.Create(); var p = f.Dump(); var s = f.Service(); var plan = s.Prepare(WindowsCleanupKind.UserTemporary, [p]); var date = File.GetLastWriteTimeUtc(p); File.WriteAllText(p + ":personal", "protected"); File.SetLastWriteTimeUtc(p, date); Equal(s.Run(plan, false, true, plan.Confirmation).State, "Não executada"); Require(File.Exists(p)); });
        Test("fixtures não inflam potencial da produção", () => { var f = Fixture.Create(); var p = f.Dump(); var roots = Enum.GetValues<WindowsCleanupKind>().ToDictionary(k => k, _ => new[] { f.Temp }); var s = new WindowsCleanupService(new WindowsCleanupEnvironment(roots, f.Journal, false, f.Clock, false)); Equal(s.Prepare(WindowsCleanupKind.UserTemporary, [p]).EligibleCount, 0); Require(File.Exists(p)); });
        Test("configurações e cofres da origem da Lixeira são preservados", () => { var f = Fixture.Create(); foreach (var ext in new[] { ".kdbx", ".accdb", ".vue", ".cfg" }) { var p = f.Recycled(Path.Combine(f.Temp, "data" + ext)); Equal(f.Service().Prepare(WindowsCleanupKind.RecycleBin, [p]).EligibleCount, 0); } });
        Test("miniatura vazia usa cabeçalho real v32 e não conta tamanho lógico como ocupado", () => { var f = Fixture.Create(); var b = new byte[24]; "CMMM"u8.CopyTo(b); U32(b, 4, 32); U32(b, 8, 1); U32(b, 16, 24); U32(b, 20, 24); var p = f.Write(f.Thumbs, "thumbcache_32.db", b); var plan = f.Service().Prepare(WindowsCleanupKind.Thumbnails, [p]); Equal(plan.EligibleCount, 1); Require(plan.Items[0].Occupied != null); });
        Test("variante de cabeçalho CMMM não suportada permanece bloqueada", () => { var f = Fixture.Create(); var p = f.Thumbnail(); var b = File.ReadAllBytes(p); U32(b, 12, 24); U32(b, 16, 80); U32(b, 20, 1); File.WriteAllBytes(p, b); Equal(f.Service().Prepare(WindowsCleanupKind.Thumbnails, [p]).EligibleCount, 0); });
        Test("limpeza rápida simula dumps sem removê-los", () =>
        {
            var f = Fixture.Create(); var p = f.Dump();
            var analysis = new QuickCleanupAnalyzer(f.Service()).Analyze(WindowsCleanupKind.UserTemporary);
            Require(analysis.Verified.ContainsKey(p) && analysis.EstimatedRecoverable > 0 && File.Exists(p));
        });
        Test("limpeza rápida não contabiliza arquivos genéricos", () =>
        {
            var f = Fixture.Create(); var p = f.Write(f.Temp, "pessoal.tmp", new byte[128]);
            var analysis = new QuickCleanupAnalyzer(f.Service()).Analyze(WindowsCleanupKind.UserTemporary);
            Equal(analysis.EstimatedRecoverable, 0L); Require(File.Exists(p) && !analysis.Verified.ContainsKey(p));
        });
        Test("limpeza rápida sem auditoria não confirma recuperável", () =>
        {
            var f = Fixture.Create(); f.Dump(); var service = f.Service();
            service.BeforeAuditForTest = _ => throw new IOException("Sem auditoria");
            Equal(new QuickCleanupAnalyzer(service).Analyze(WindowsCleanupKind.UserTemporary).EstimatedRecoverable, 0L);
        });
        Test("limpeza rápida deduplica categorias sobrepostas", () =>
        {
            var f = Fixture.Create(); f.Dump();
            var a = new QuickCleanupAnalyzer(f.Service()).Analyze(WindowsCleanupKind.UserTemporary);
            Equal(QuickCleanupAnalyzer.Total([a, a]), a.EstimatedRecoverable);
        });
        Test("limpeza rápida mantém categorias oficiais e npm fora da execução", () =>
        {
            var f = Fixture.Create();
            foreach (var kind in new[] { WindowsCleanupKind.NpmCache, WindowsCleanupKind.DirectXShaders })
            {
                bool blocked = false;
                try { new QuickCleanupAnalyzer(f.Service()).Analyze(kind); } catch (IOException) { blocked = true; }
                Require(blocked);
            }
        });
        Test("limpeza rápida cancelada preserva os dados", () =>
        {
            var f = Fixture.Create(); var p = f.Dump(); using var cancel = new CancellationTokenSource(); cancel.Cancel();
            bool cancelled = false;
            try { new QuickCleanupAnalyzer(f.Service()).Analyze(WindowsCleanupKind.UserTemporary, token: cancel.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Require(cancelled && File.Exists(p));
        });
        Test("descoberta é cancelável e não seleciona arquivos", () => { var f = Fixture.Create(); f.Dump(); using var c = new CancellationTokenSource(); c.Cancel(); var d = f.Service().Discover(WindowsCleanupKind.UserTemporary, token: c.Token); Require(d.Cancelled); });
    }
    internal sealed class Clock : TimeProvider { internal DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    sealed class Fixture
    {
        internal string Base, Temp, WindowsTemp, Thumbs, Dumps, Bin, Journal;
        internal Clock Clock = new();
        Fixture(string root) { Base = root; Temp = Path.Combine(root, "temp"); WindowsTemp = Path.Combine(root, "windows-temp"); Thumbs = Path.Combine(root, "thumbs"); Dumps = Path.Combine(root, "dumps"); Bin = Path.Combine(root, "recycle"); Journal = Path.Combine(root, "windows-cleanup.jsonl"); foreach (var p in new[] { Temp, WindowsTemp, Thumbs, Dumps, Bin }) Directory.CreateDirectory(p); }
        internal static Fixture Create() => new(Path.Combine(Path.GetTempPath(), "Respiro.Missao5.Tests", Guid.NewGuid().ToString("N")));
        internal WindowsCleanupService Service(bool admin = false)
        {
            var roots = Enum.GetValues<WindowsCleanupKind>().ToDictionary(k => k, _ => new[] { Path.Combine(Base, "blocked") });
            roots[WindowsCleanupKind.UserTemporary] = [Temp]; roots[WindowsCleanupKind.WindowsTemporary] = [WindowsTemp]; roots[WindowsCleanupKind.Thumbnails] = [Thumbs]; roots[WindowsCleanupKind.ErrorReports] = [Dumps]; roots[WindowsCleanupKind.RecycleBin] = [Bin];
            return new(new WindowsCleanupEnvironment(roots, Journal, true, Clock, admin));
        }
        internal string Write(string dir, string name, byte[] bytes) { var p = Path.Combine(dir, name); File.WriteAllBytes(p, bytes); return p; }
        internal string Dump(string? dir = null)
        {
            var b = new byte[4096]; "MDMP"u8.CopyTo(b); U32(b, 4, 0xa793); U32(b, 8, 1); U32(b, 12, 32); U32(b, 20, (uint)DateTimeOffset.UtcNow.AddDays(-60).ToUnixTimeSeconds()); U32(b, 32, 7); U32(b, 36, 56); U32(b, 40, 44); var p = Write(dir ?? Temp, Guid.NewGuid().ToString("N") + ".dmp", b); Old(p); return p;
        }
        internal string Thumbnail() { var b = new byte[4096]; "CMMM"u8.CopyTo(b); U32(b, 4, 32); U32(b, 8, 1); U32(b, 12, 0); U32(b, 16, 24); U32(b, 20, 80); "CMMM"u8.CopyTo(b.AsSpan(24)); U32(b, 28, 56); return Write(Thumbs, "thumbcache_32.db", b); }
        internal string IndexFor(string p) => Path.Combine(Bin, "$I" + Path.GetFileName(p)[2..]);
        internal string Recycled(string? original = null)
        {
            var p = Write(Bin, "$R" + Guid.NewGuid().ToString("N") + ".bin", Enumerable.Repeat((byte)0xa5, 2048).ToArray());
            var source = Encoding.Unicode.GetBytes((original ?? Path.Combine(Temp, "discard.bin")) + "\0"); var bytes = new byte[28 + source.Length]; BinaryPrimitives.WriteInt64LittleEndian(bytes, 2); BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(8), 2048); BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), DateTime.UtcNow.AddDays(-1).ToFileTimeUtc()); U32(bytes, 24, (uint)(source.Length / 2)); source.CopyTo(bytes, 28); File.WriteAllBytes(IndexFor(p), bytes); return p;
        }
    }
    static void Old(string p) => File.SetLastWriteTimeUtc(p, DateTime.UtcNow.AddDays(-60));
    static void U32(byte[] b, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(offset), value);
    static void Equal<T>(T actual, T expected) { if (!EqualityComparer<T>.Default.Equals(actual, expected)) throw new Exception($"Esperado {expected}; obtido {actual}"); }
    static void Require(bool value) { if (!value) throw new Exception("Condição de segurança não atendida."); }
    static void Throws(Action action) { try { action(); } catch (IOException) { return; } throw new Exception("Operação deveria ser bloqueada."); }
}


