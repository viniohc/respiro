using System.Diagnostics;
using System.Text.Json;
using Respiro.Core;
namespace Respiro.Tests;

internal static class OperationalProcessTests
{
    public static void Register(List<(string, Action)> tests)
    {
        void Test(string name, Action action) => OperationalEvidence.Register(tests, name, action);
        Test("janela descartável recebe fechamento normal e termina", () =>
        {
            using var child = new ControlledHost("normal");
            var selected = child.Sample();
            OperationalEvidence.Check(!new WindowsProcessCloseBackend().StillSame(selected), "Fixture não deve entrar na lista de produção.");
            var journal = child.Journal;
            string result = new ProcessCloseService(child, journal).Request(selected, "FECHAR " + selected.Id);
            OperationalEvidence.Check(result.Contains("Solicitação de fechamento enviada"));
            OperationalEvidence.Check(child.Process.WaitForExit(5000), "Fixture não fechou normalmente.");
            OperationalEvidence.Check(child.Process.ExitCode == 0 && child.CloseEvents() == 1);
            OperationalEvidence.Check(journal.Read().Select(e => e.State).SequenceEqual(new[] { "intent", "requested" }));
            child.Evidence("normal", new { NormalExit = true, ExitCode = child.Process.ExitCode, WindowsProcessesTouched = false });
        });
        Test("processo descartável já encerrado não recebe outro pedido", () =>
        {
            using var child = new ControlledHost("normal"); var selected = child.Sample();
            new ProcessCloseService(child, child.Journal).Request(selected, "FECHAR " + selected.Id);
            OperationalEvidence.Check(child.Process.WaitForExit(5000));
            int receipts = child.Journal.Read().Count;
            OperationalEvidence.Failure(() => new ProcessCloseService(child, child.Journal).Request(selected, "FECHAR " + selected.Id));
            OperationalEvidence.Check(child.CloseEvents() == 1 && child.Journal.Read().Count == receipts);
            child.Evidence("already-exited", new { SecondRequestBlocked = true, NoExtraReceipt = true });
        });
        Test("cancelamento antes da confirmação não envia WM_CLOSE", () =>
        {
            using var child = new ControlledHost("normal"); var sample = child.Sample();
            OperationalEvidence.Failure(() => new ProcessCloseService(child, child.Journal).Request(sample, "CANCELAR"));
            Thread.Sleep(200);
            OperationalEvidence.Check(!child.Process.HasExited && child.CloseEvents() == 0 && child.Journal.Read().Count == 0);
            child.Evidence("cancelled-before-request", new { NoCloseMessage = true, NoIntent = true });
        });
        Test("aplicativo descartável pode recusar fechamento sem ser forçado", () =>
        {
            using var child = new ControlledHost("refuse"); var sample = child.Sample();
            new ProcessCloseService(child, child.Journal).Request(sample, "FECHAR " + sample.Id);
            child.WaitFor(() => child.CloseEvents() == 1);
            OperationalEvidence.Check(!child.Process.HasExited);
            var e = JsonDocument.Parse(File.ReadLines(Path.Combine(child.Folder, "events.jsonl")).Single());
            OperationalEvidence.Check(e.RootElement.GetProperty("Cancelled").GetBoolean()); e.Dispose();
            child.Evidence("refused", new { RequestDelivered = true, ApplicationCancelled = true, StillRunning = true, ForcedTermination = false });
        });
        Test("janela sem resposta não bloqueia o Respiro nem recebe encerramento forçado", () =>
        {
            using var child = new ControlledHost("hung");
            child.WaitFor(() => File.Exists(Path.Combine(child.Folder, "blocked.txt")));
            var sample = child.Sample(); var timer = Stopwatch.StartNew();
            new ProcessCloseService(child, child.Journal).Request(sample, "FECHAR " + sample.Id);
            long elapsed = timer.ElapsedMilliseconds;
            OperationalEvidence.Check(elapsed < 2000, "Solicitação bloqueou além de dois segundos.");
            Thread.Sleep(250);
            OperationalEvidence.Check(!child.Process.HasExited && child.CloseEvents() == 0);
            child.Evidence("hung", new { RequestElapsedMs = elapsed, StillRunning = true, ForcedTermination = false });
        });
        Test("janela desabilitada não é tratada como processo encerrado", () =>
        {
            using var child = new ControlledHost("disabled"); var sample = child.Sample();
            string result = new ProcessCloseService(child, child.Journal).Request(sample, "FECHAR " + sample.Id);
            OperationalEvidence.Check(result.Contains("não aceitou") && !child.Process.HasExited && child.CloseEvents() == 0 && child.Journal.Read().Last().State == "not-sent");
            child.Evidence("disabled-window", new { RequestAccepted = false, StillRunning = true });
        });
        Test("falha de fechamento registra resultado e preserva a fixture", () =>
        {
            using var child = new ControlledHost("normal"); var sample = child.Sample();
            OperationalEvidence.Failure(() => new ProcessCloseService(new FailingClose(child), child.Journal).Request(sample, "FECHAR " + sample.Id));
            OperationalEvidence.Check(child.Journal.Read().Last().State == "failed" && !child.Process.HasExited && child.CloseEvents() == 0);
        });
    }
    sealed class FailingClose(ControlledHost child) : IProcessCloseBackend
    {
        public bool StillSame(ProcessSample selected) => child.StillSame(selected);
        public bool RequestNormalClose(ProcessSample selected) => throw new IOException("Fixture: falha de envio, nenhum WM_CLOSE.");
    }
    sealed class ControlledHost : IDisposable, IProcessCloseBackend
    {
        readonly string nonce = Guid.NewGuid().ToString("N");
        readonly EventWaitHandle exit;
        readonly string executable;
        readonly long started;
        readonly long window;
        internal string Folder { get; } = OperationalEvidence.Folder();
        internal Process Process { get; }
        internal OptimizationJournal Journal => new(Path.Combine(Folder, "journal.jsonl"));
        internal ControlledHost(string mode)
        {
            GuestValidationGuard.AssertActive();
            executable = GuestValidationGuard.FixtureExecutable;
            if (!File.Exists(executable)) throw new IOException("Compile o projeto de fixture antes da validação operacional.");
            exit = new EventWaitHandle(false, EventResetMode.ManualReset, @"Local\Respiro.Operational." + nonce);
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
            string? runtimeRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            if (!string.IsNullOrWhiteSpace(runtimeRoot) && Directory.Exists(Path.Combine(runtimeRoot, "shared")))
                start.Environment["DOTNET_ROOT"] = runtimeRoot;
            else foreach (var variable in new[] { "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_X86", "DOTNET_ROOT(x86)" }) start.Environment.Remove(variable);
            foreach (var arg in new[] { nonce, mode, Folder }) start.ArgumentList.Add(arg);
            Process = System.Diagnostics.Process.Start(start) ?? throw new IOException("Fixture não iniciou.");
            _ = Process.SafeHandle; started = Process.StartTime.ToUniversalTime().Ticks;
            try
            {
                WaitFor(() => File.Exists(Path.Combine(Folder, "ready.json")));
                using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder, "ready.json")));
                var ready = doc.RootElement;
                OperationalEvidence.Check(ready.GetProperty("Nonce").GetString() == nonce && ready.GetProperty("Pid").GetInt32() == Process.Id &&
                    ready.GetProperty("Started").GetInt64() == started, "Identidade da fixture não corresponde ao filho criado.");
                window = ready.GetProperty("Hwnd").GetInt64();
                OperationalEvidence.Check(window != 0);
            }
            catch { exit.Set(); Process.WaitForExit(5000); Process.Dispose(); exit.Dispose(); throw; }
        }
        internal void WaitFor(Func<bool> ready)
        {
            var timeout = Stopwatch.StartNew();
            while (timeout.ElapsedMilliseconds < 8000)
            {
                Process.Refresh(); if (Process.HasExited) throw new IOException("Fixture encerrou antes da verificação.");
                try { if (ready()) return; } catch (IOException) { }
                Thread.Sleep(30);
            }
            throw new IOException("Fixture não ficou pronta em oito segundos.");
        }
        internal ProcessSample Sample() => new(Process.Id, started, "Respiro fixture", "Fixture only", executable, null, null, null, 1, true, "Owned child, not production compatibility", nonce);
        public bool StillSame(ProcessSample selected)
        {
            try
            {
                Process.Refresh();
                return selected.Id == Process.Id && selected.StartedUtcTicks == started && selected.ExecutableHash == nonce &&
                    !Process.HasExited && Process.StartTime.ToUniversalTime().Ticks == started &&
                    string.Equals(Process.MainModule?.FileName, executable, StringComparison.OrdinalIgnoreCase) &&
                    Process.MainWindowHandle.ToInt64() == window;
            }
            catch { return false; }
        }
        public bool RequestNormalClose(ProcessSample selected) => WindowsProcessCloseBackend.RequestForSameWindow(selected, StillSame);
        internal int CloseEvents()
        {
            string path = Path.Combine(Folder, "events.jsonl");
            if (!File.Exists(path)) return 0;
            try
            {
                int count = 0;
                foreach (var line in File.ReadAllLines(path)) { using var doc = JsonDocument.Parse(line); if (doc.RootElement.GetProperty("Kind").GetString() == "WM_CLOSE") count++; }
                return count;
            }
            catch (Exception e) when (e is IOException or JsonException) { return 0; }
        }
        internal void Evidence(string mode, object data) => OperationalEvidence.Record("controlled-window-" + mode, new { Folder, OwnedPid = Process.Id, Nonce = nonce, Data = data });
        public void Dispose()
        {
            if (!Process.HasExited)
            {
                // The owned fixture exits itself through a nonce-bound event; no external process is killed.
                exit.Set();
                OperationalEvidence.Check(Process.WaitForExit(5000), "A fixture não respondeu ao seu canal de saída.");
            }
            OperationalEvidence.Check(Process.ExitCode != 97, "A fixture precisou do watchdog; teste inconclusivo.");
            Process.Dispose(); exit.Dispose();
        }
    }
}
