using Respiro.Core;
namespace Respiro.Tests;

internal static class OptimizationTests
{
    static StartupEntry Entry(string command = "\"C:\\Apps\\spotify.exe\" /login") =>
        new("Spotify", StartupOrigin.UserRun, @"Software\Microsoft\Windows\CurrentVersion\Run", command, 1,
            @"C:\Apps\spotify.exe", "Spotify", "fixture-hash", null, true, "fixture");
    static InstalledApp App() => new("Google Chrome", "Google LLC", "1", 1024, @"C:\Apps", "Tradicional", "fixture")
    {
        ProductCode = "{11111111-1111-1111-1111-111111111111}", WindowsInstaller = true,
        UninstallHive = "CurrentUser", UninstallView = "Registry64",
        UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{11111111-1111-1111-1111-111111111111}"
    };
    static ProcessSample Process() => new(456, 12345, "Spotify", "Spotify", @"C:\Apps\spotify.exe", 1, 1024, 42, 2, true, "fixture", "hash");
    static void Check(bool condition) { if (!condition) throw new Exception("Condição não atendida."); }
    static void Blocked(Action operation) { try { operation(); } catch (IOException) { return; } throw new Exception("Operação deveria ter sido bloqueada."); }

    public static void Register(List<(string, Action)> tests)
    {
        void Test(string name, Action action) => tests.Add(("Otimização: " + name, action));
        Test("Run sem confirmação não modifica fixture", () => {
            var backend = new StartupFake(Entry()); var service = new StartupChangeService(backend, new JournalFake());
            var plan = service.Prepare(Entry(), false); Blocked(() => service.Execute(plan, "SIM")); Check(backend.Removes == 0);
        });
        Test("Run registra intenção antes da alteração", () => {
            var log = new JournalFake(); var backend = new StartupFake(Entry()) { BeforeRemove = () => Check(log.Events.Single().State == "intent") };
            var service = new StartupChangeService(backend, log); var plan = service.Prepare(Entry(), false);
            service.Execute(plan, plan.Confirmation); Check(backend.Current == null && log.Events.Last().State == "completed");
        });
        Test("falha no journal bloqueia desativação", () => {
            var backend = new StartupFake(Entry()); var service = new StartupChangeService(backend, new JournalFake { Fail = true });
            var plan = service.Prepare(Entry(), false); Blocked(() => service.Execute(plan, plan.Confirmation)); Check(backend.Removes == 0);
        });
        Test("mudança entre prévia e execução preservada", () => {
            var backend = new StartupFake(Entry()); var service = new StartupChangeService(backend, new JournalFake());
            var plan = service.Prepare(Entry(), false); backend.Current = Entry("changed");
            Blocked(() => service.Execute(plan, plan.Confirmation)); Check(backend.Removes == 0);
        });
        Test("prévia de inicialização não pode ser reutilizada", () => {
            var backend = new StartupFake(Entry()); var service = new StartupChangeService(backend, new JournalFake());
            var plan = service.Prepare(Entry(), false); service.Execute(plan, plan.Confirmation);
            backend.Current = Entry(); Blocked(() => service.Execute(plan, plan.Confirmation)); Check(backend.Removes == 1);
        });
        Test("reposição preserva valor e tipo originais", () => {
            var original = Entry("%LOCALAPPDATA%\\Apps\\spotify.exe /login") with { ValueKind = 2 };
            var backend = new StartupFake(original); var service = new StartupChangeService(backend, new JournalFake());
            var disable = service.Prepare(original, false); service.Execute(disable, disable.Confirmation);
            var restore = service.Prepare(original, true); service.Execute(restore, restore.Confirmation);
            Check(backend.Current == original && backend.Restores == 1);
        });
        Test("reposição não sobrescreve nova entrada", () => {
            var backend = new StartupFake(null); var service = new StartupChangeService(backend, new JournalFake());
            var restore = service.Prepare(Entry(), true); backend.Current = Entry("new");
            Blocked(() => service.Execute(restore, restore.Confirmation)); Check(backend.Restores == 0);
        });
        Test("recibo mais recente prevalece na reversão", () => {
            var old = Entry("old"); var latest = Entry("latest"); var log = new JournalFake();
            log.Append(new(Guid.NewGuid(), DateTime.UtcNow, "startup-disable", "intent", old, ""));
            log.Append(new(Guid.NewGuid(), DateTime.UtcNow, "startup-disable", "intent", latest, ""));
            var service = new StartupChangeService(new StartupFake(null), log);
            Check(service.Restorable().Single().Command == "latest");
        });
        Test("recibo não oferece reposição sobre entrada presente", () => {
            var log = new JournalFake(); log.Append(new(Guid.NewGuid(), DateTime.UtcNow, "startup-disable", "intent", Entry(), ""));
            Check(new StartupChangeService(new StartupFake(Entry()), log).Restorable().Count == 0);
        });
        Test("entradas somente diagnósticas são bloqueadas", () => {
            var backend = new StartupFake(Entry() with { Manageable = false });
            Blocked(() => new StartupChangeService(backend, new JournalFake()).Prepare(backend.Current!, false)); Check(backend.Removes == 0);
        });
        Test("backend real rejeita máquina antes de acessar Run", () => Blocked(() => new WindowsStartupBackend().ValidateOriginal(Entry() with { Origin = StartupOrigin.MachineRun })));
        Test("backend real rejeita RunOnce antes de acessar registro", () => Blocked(() => new WindowsStartupBackend().ValidateOriginal(Entry() with { Locator = @"Software\Microsoft\Windows\CurrentVersion\RunOnce" })));
        Test("backend real rejeita atalhos para alteração", () => Blocked(() => new WindowsStartupBackend().ValidateOriginal(Entry() with { Origin = StartupOrigin.UserFolder })));
        Test("parser admite caminho entre aspas com espaços", () => Check(OptimizationSafety.ParseExecutable("\"C:\\Program Files\\Player\\player.exe\" --start") == @"C:\Program Files\Player\player.exe"));
        Test("parser bloqueia caminho relativo shell e URL", () => {
            foreach (var command in new[] { "player.exe", "cmd /c app.exe", "https://host/app.exe", "\"C:\\Apps\\app.exe" })
                Check(OptimizationSafety.ParseExecutable(command) == null);
        });
        Test("agentes desenvolvimento e Windows são protegidos", () => {
            Check(OptimizationSafety.ProtectedPath(@"C:\Users\user\dev\app.exe"));
            Check(OptimizationSafety.ProtectedPath(@"C:\Apps\MultiCodex\app.exe"));
            Check(OptimizationSafety.ProtectedPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")));
        });
        Test("vínculo corporativo e consulta incerta bloqueiam alterações", () => {
            Check(WindowsManagementGuard.AllowsChanges(0, 2, 0, false));
            Check(!WindowsManagementGuard.AllowsChanges(0, 3, 0, false));
            Check(!WindowsManagementGuard.AllowsChanges(0, 2, 0, true));
            Check(!WindowsManagementGuard.AllowsChanges(5, 2, 0, false));
        });
        Test("argumentos compatíveis são explícitos", () => {
            Check(OptimizationSafety.CompatibleStartupCommand("\"C:\\Apps\\spotify.exe\" --autostart"));
            Check(!OptimizationSafety.CompatibleStartupCommand("\"C:\\Apps\\chrome.exe\" --load-extension=C:\\anything"));
            Check(!OptimizationSafety.CompatibleStartupCommand("\"C:\\Apps\\spotify.exe\" --unknown"));
        });
        Test("journal interrompido bloqueia nova intenção", () => {
            var folder = Path.Combine(Path.GetTempPath(), "Respiro.MissaoOptimization.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder); var path = Path.Combine(folder, "interrupted.jsonl");
            File.WriteAllText(path, "{\"partial\":");
            var journal = new OptimizationJournal(path);
            Blocked(() => journal.Append(new(Guid.NewGuid(), DateTime.UtcNow, "fixture", "intent", null, "")));
            Check(File.ReadAllText(path) == "{\"partial\":");
        });
        Test("transação nativa abre e reverte sem abrir ou alterar chaves", StartupRegistryTransaction.ProbeWithoutRegistryChanges);
        Test("verificador nativo lê signatário Microsoft sem executar arquivo", () => {
            var runtime = Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? Path.Combine(Environment.CurrentDirectory, ".sdk");
            var signed = Path.Combine(runtime, "dotnet.exe");
            Check(OptimizationSafety.TrustedSigner(signed)?.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) == true);
        });
        Test("assinatura adulterada é bloqueada em fixture", () => {
            var runtime = Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? Path.Combine(Environment.CurrentDirectory, ".sdk");
            var folder = Path.Combine(Path.GetTempPath(), "Respiro.MissaoOptimization.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder); var fixture = Path.Combine(folder, "signed-copy.exe");
            File.Copy(Path.Combine(runtime, "dotnet.exe"), fixture, false);
            using (var file = new FileStream(fixture, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                file.Position = 4096; int b = file.ReadByte(); Check(b >= 0); file.Position = 4096; file.WriteByte((byte)(b ^ 0xff));
            }
            Check(OptimizationSafety.TrustedSigner(fixture) == null);
        });
        Test("valores Run diferentes conservam recibos separados", () => {
            var log = new JournalFake();
            log.Append(new(Guid.NewGuid(), DateTime.UtcNow, "startup-disable", "intent", Entry(), ""));
            log.Append(new(Guid.NewGuid(), DateTime.UtcNow, "startup-disable", "intent", Entry() with { Name = "VLC" }, ""));
            Check(new StartupChangeService(new StartupFake(null), log).Restorable().Count == 2);
        });
        Test("recibos Run respeitam nomes sem diferença de caixa", () => {
            var log = new JournalFake();
            log.Append(new(Guid.NewGuid(), DateTime.UtcNow, "startup-disable", "intent", Entry("old"), ""));
            log.Append(new(Guid.NewGuid(), DateTime.UtcNow, "startup-disable", "intent", Entry("latest") with { Name = "spotify" }, ""));
            var receipts = new StartupChangeService(new StartupFake(null), log).Restorable();
            Check(receipts.Count == 1 && receipts[0].Command == "latest");
        });
        Test("conflitos Run com outra caixa são reconhecidos", () => Check(StartupRegistryTransaction.NameExists(new[] { "spotify" }, "Spotify")));
        Test("componente com nome semelhante não herda autorização MSI", () => Check(!OptimizationSafety.SafeMsi(App() with { Name = "Google Chrome Binaries" })));
        Test("MSI usa produto fixo interface completa e nenhuma reinicialização automática", () => {
            var app = App(); var start = WindowsMsiUninstallBackend.CreateStartInfo(app);
            Check(start.FileName == Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe"));
            Check(!start.UseShellExecute && start.Verb != "runas");
            Check(start.ArgumentList.SequenceEqual(new[] { "/x", app.ProductCode!, "/qf", "/norestart" }));
            Blocked(() => WindowsMsiUninstallBackend.CreateStartInfo(app with { ProductCode = "bad /quiet" }));
        });
        Test("S_FALSE sem informação de vínculo é sucesso e erro HRESULT continua bloqueado", () => {
            Check(WindowsManagementGuard.AllowsChanges(0, 2, 1, false));
            Check(!WindowsManagementGuard.AllowsChanges(0, 2, 1, true));
            Check(!WindowsManagementGuard.AllowsChanges(0, 2, unchecked((int)0x80004005), false));
        });
        Test("CPU é normalizada por processadores lógicos", () => Check(ProcessMonitor.CpuDelta(100, 900, 1, 8) == 10));
        Test("CPU inválida não vira zero medido", () => Check(ProcessMonitor.CpuDelta(20, 10, 1, 4) == null && ProcessMonitor.CpuDelta(0, 1, 0, 4) == null));
        Test("processo protegido nunca recebe fechamento", () => {
            var backend = new CloseFake(); var service = new ProcessCloseService(backend, new JournalFake());
            Blocked(() => service.Request(Process() with { CanRequestClose = false }, "FECHAR 456")); Check(backend.Requests == 0);
        });
        Test("PID reutilizado ou identidade alterada bloqueia fechamento", () => {
            var backend = new CloseFake { Same = false }; Blocked(() => new ProcessCloseService(backend, new JournalFake()).Request(Process(), "FECHAR 456")); Check(backend.Requests == 0);
        });
        Test("fechamento normal exige confirmação específica", () => {
            var backend = new CloseFake(); Blocked(() => new ProcessCloseService(backend, new JournalFake()).Request(Process(), "SIM")); Check(backend.Requests == 0);
        });
        Test("fechamento sem journal é bloqueado", () => {
            var backend = new CloseFake(); Blocked(() => new ProcessCloseService(backend, new JournalFake { Fail = true }).Request(Process(), "FECHAR 456")); Check(backend.Requests == 0);
        });
        Test("fechamento normal registra pedido sem afirmar economia", () => {
            var backend = new CloseFake(); var log = new JournalFake();
            string result = new ProcessCloseService(backend, log).Request(Process(), "FECHAR 456");
            Check(backend.Requests == 1 && log.Events.First().State == "intent" && result.Contains("não comprova economia"));
        });
        Test("recusa do fechamento não provoca encerramento forçado", () => {
            var backend = new CloseFake { Accept = false }; string result = new ProcessCloseService(backend, new JournalFake()).Request(Process(), "FECHAR 456");
            Check(backend.Requests == 1 && result.Contains("não aceitou"));
        });
        Test("MSI exige GUID inequívoco e fornecedor compatível", () => {
            Check(OptimizationSafety.SafeMsi(App()));
            Check(!OptimizationSafety.SafeMsi(App() with { ProductCode = "{GUID} /quiet" }));
            Check(!OptimizationSafety.SafeMsi(App() with { WindowsInstaller = false }));
            Check(!OptimizationSafety.SafeMsi(App() with { Publisher = "Unknown" }));
        });
        Test("componentes runtimes e Store não são desinstalados diretamente", () => {
            Check(!OptimizationSafety.SafeMsi(App() with { SystemComponent = true }));
            Check(!OptimizationSafety.SafeMsi(App() with { Name = "Microsoft Visual C++ Runtime", Publisher = "Microsoft" }));
            Check(!OptimizationSafety.SafeMsi(App() with { Kind = "Microsoft Store / MSIX" }));
        });
        Test("MSI sem confirmação não abre desinstalador", () => {
            var backend = new UninstallFake(); var service = new ApplicationUninstallService(backend, new JournalFake()); var plan = service.Prepare(App());
            Blocked(() => service.Execute(plan, "SIM")); Check(backend.Launched == 0);
        });
        Test("MSI alterado após seleção não abre desinstalador", () => {
            var backend = new UninstallFake(); var service = new ApplicationUninstallService(backend, new JournalFake()); var plan = service.Prepare(App());
            backend.Same = false; Blocked(() => service.Execute(plan, plan.Confirmation)); Check(backend.Launched == 0);
        });
        Test("MSI sem intenção durável não inicia", () => {
            var backend = new UninstallFake(); var service = new ApplicationUninstallService(backend, new JournalFake { Fail = true }); var plan = service.Prepare(App());
            Blocked(() => service.Execute(plan, plan.Confirmation)); Check(backend.Launched == 0);
        });
        Test("MSI registra lançamento sem afirmar remoção", () => {
            var backend = new UninstallFake(); var log = new JournalFake(); var service = new ApplicationUninstallService(backend, log); var plan = service.Prepare(App());
            string result = service.Execute(plan, plan.Confirmation); Check(backend.Launched == 1 && result.Contains("Nenhuma desinstalação") && log.Events.Last().State == "launched");
            Blocked(() => service.Execute(plan, plan.Confirmation)); Check(backend.Launched == 1);
        });
        Test("catalogação não amplia categorias de limpeza", () => {
            Check(CleanupIntegrationCatalog.Rows.Count(r => r.Class.StartsWith("A ")) == WindowsCleanupCatalog.Categories.Count(c => c.SupportsRemoval) + 1);
            Check(CleanupIntegrationCatalog.Rows.Single(r => r.Category == "Temporários próprios do Respiro (Scratch)").Class.StartsWith("A "));
            Check(CleanupIntegrationCatalog.Rows.Single(r => r.Category == "Shaders DirectX").Class.StartsWith("C "));
            Check(CleanupIntegrationCatalog.Rows.Single(r => r.Category == "Logs temporários conhecidos").Class.StartsWith("D "));
        });
        Test("journal persiste recibo em fixture isolada", () => {
            var folder = Path.Combine(Path.GetTempPath(), "Respiro.MissaoOptimization.Tests", Guid.NewGuid().ToString("N"));
            var journal = new OptimizationJournal(Path.Combine(folder, "operations.jsonl"));
            var entry = Entry(); var id = Guid.NewGuid();
            journal.Append(new(id, DateTime.UtcNow, "startup-disable", "intent", entry, "fixture somente"));
            var read = journal.Read().Single(); Check(read.Id == id && read.Entry == entry);
        });
        Test("histórico antigo de aplicativos não exige novos campos", () => {
            var app = System.Text.Json.JsonSerializer.Deserialize<InstalledApp>("""{"Name":"Antigo","Publisher":"Fornecedor","Version":"1","Bytes":null,"Location":"","Kind":"Tradicional","Notes":""}""")!;
            Check(!app.WindowsInstaller && app.ProductCode == null && !OptimizationSafety.SafeMsi(app));
        });
    }
    sealed class JournalFake : IOptimizationJournal
    {
        public readonly List<OptimizationEvent> Events = [];
        public bool Fail;
        public void Append(OptimizationEvent operation) { if (Fail) throw new IOException("Fixture: journal indisponível."); Events.Add(operation); }
        public IReadOnlyList<OptimizationEvent> Read() => Events;
    }
    sealed class StartupFake(StartupEntry? current) : IStartupBackend
    {
        public StartupEntry? Current = current; public int Removes, Restores; public Action? BeforeRemove;
        public void ValidateOriginal(StartupEntry entry) { if (!entry.Manageable) throw new IOException("Fixture protegida."); }
        public StartupEntry? Read(StartupEntry original) => Current;
        public void Remove(StartupEntry original) { BeforeRemove?.Invoke(); if (Current != original) throw new IOException("Mudou"); Current = null; Removes++; }
        public void Restore(StartupEntry original) { if (Current != null) throw new IOException("Conflito"); Current = original; Restores++; }
    }
    sealed class CloseFake : IProcessCloseBackend
    {
        public bool Same = true, Accept = true; public int Requests;
        public bool StillSame(ProcessSample sample) => Same;
        public bool RequestNormalClose(ProcessSample sample) { Requests++; return Accept; }
    }
    sealed class UninstallFake : IAppUninstallBackend
    {
        public bool Same = true; public int Launched;
        public bool StillSame(InstalledApp app) => Same;
        public void Launch(InstalledApp app) => Launched++;
    }
}
