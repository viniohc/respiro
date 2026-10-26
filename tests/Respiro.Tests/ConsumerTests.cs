using Respiro.Core;
internal static class ConsumerTests
{
    static void Check(bool value) { if (!value) throw new Exception("Filtro incorreto"); }
    static readonly DateTime Now = new(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc);
    static FileRow File(string path, long bytes = 200, DateTime? modified = null) =>
        new(path, bytes, bytes, "Pessoal", Nature.Protegido, "Revisão manual", modified ?? Now);
    public static void Register(List<(string, Action)> tests)
    {
        void Test(string name, Action action) => tests.Add(("Usuários: " + name, action));
        Test("vídeos preservam classificação e limite mínimo", () =>
        {
            var files = new[] { File(@"C:\Dados\filme.MP4"), File(@"C:\Dados\foto.jpg"), File(@"C:\Dados\pequeno.mkv", 50) };
            var filtered = PersonalFileExplorer.Filter(files, PersonalFileKind.Videos, 100, Now);
            Check(filtered.Count == 1 && filtered[0] == files[0] && filtered[0].Nature == Nature.Protegido);
        });
        Test("Downloads realocado respeita limite da pasta", () =>
        {
            var files = new[] { File(@"D:\Meus downloads\x.zip"), File(@"D:\Meus downloads antigos\x.zip") };
            Check(PersonalFileExplorer.Filter(files, PersonalFileKind.Downloads, 0, Now, @"D:\Meus downloads").Single() == files[0]);
            Check(PersonalFileExplorer.Filter(files, PersonalFileKind.Downloads, 0, Now).Count == 0);
        });
        Test("possíveis duplicatas exigem nome e tamanho sem presumir conteúdo", () =>
        {
            var files = new[] { File(@"C:\A\foto.jpg"), File(@"C:\B\FOTO.jpg"), File(@"C:\C\foto.jpg", 300), File(@"C:\D\outra.jpg") };
            var found = PersonalFileExplorer.Filter(files, PersonalFileKind.PossibleDuplicates, 0, Now);
            Check(found.Count == 2 && found.All(f => f.Bytes == 200));
        });
        Test("arquivos vazios não geram oportunidades de duplicação", () =>
        {
            var files = new[] { File(@"C:\A\vazio.zip", 0), File(@"C:\B\vazio.zip", 0) };
            Check(PersonalFileExplorer.Filter(files, PersonalFileKind.PossibleDuplicates, 0, Now).Count == 0);
        });
        Test("filtro de alteração não usa data futura nem declara último acesso", () =>
        {
            var files = new[] { File(@"C:\A\antigo.zip", modified: Now.AddDays(-180)), File(@"C:\A\futuro.zip", modified: Now.AddDays(1)) };
            Check(PersonalFileExplorer.Filter(files, PersonalFileKind.OldModification, 0, Now).Single() == files[0]);
        });
        Test("instaladores são somente um filtro inclusive para executáveis", () =>
        {
            var files = new[] { File(@"C:\A\programa.exe"), File(@"C:\A\pacote.msi"), File(@"C:\A\notas.txt") };
            Check(PersonalFileExplorer.Filter(files, PersonalFileKind.Installers, 0, Now).Count == 2);
        });
        Test("categorias rápidas só usam encaminhamentos conhecidos", () =>
        {
            Check(QuickCleanupCatalog.Options.All(o => OfficialMaintenanceCatalog.Resolve(o.OfficialId) != null));
            Check(!QuickCleanupCatalog.Options.Any(o => o.Kind == WindowsCleanupKind.NpmCache));
        });
    }
}
