using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using Respiro.Core;
namespace Respiro.Tests;

internal static class OperationalEvidence
{
    internal static string Folder()
    {
        GuestValidationGuard.AssertActive();
        string folder = Path.Combine(Environment.CurrentDirectory, "artifacts", "operational-fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder); return folder;
    }
    internal static void Check(bool value, string message = "Verificação operacional falhou.") { if (!value) throw new Exception(message); }
    internal static Exception Failure(Action action)
    {
        try { action(); } catch (Exception e) { return e; }
        throw new Exception("A operação deveria ter sido bloqueada.");
    }
    internal static string RunStamp()
    {
        var rows = new List<string>();
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            using var key = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (key == null) continue;
            foreach (var name in key.GetValueNames().Order(StringComparer.Ordinal))
                rows.Add($"{hive}/{view}/{name}|{key.GetValueKind(name)}|" +
                    JsonSerializer.Serialize(key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames)));
        }
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join("\n", rows))));
    }
    internal static void Record(string name, object evidence)
    {
        string path = Path.Combine(Environment.CurrentDirectory, "reports", "optimization-operational-steps.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, JsonSerializer.Serialize(new { At = DateTimeOffset.Now, Case = name, Evidence = evidence }) + "\n");
    }
    internal static void Register(List<(string, Action)> tests, string name, Action action)
    {
        tests.Add(("Operacional: " + name, () =>
        {
            GuestValidationGuard.AssertActive();
            string before = RunStamp(); var timer = Stopwatch.StartNew();
            try
            {
                action(); string after = RunStamp();
                Check(before == after, "Run real mudou durante o teste; investigar origem, sem atribuição automática.");
                Record(name, new { Passed = true, RunSnapshotUnchanged = true, Scope = "AUTHORIZED_GUEST", ElapsedMs = timer.ElapsedMilliseconds });
            }
            catch (Exception e) { Record(name, new { Passed = false, Error = e.Message, RunSnapshotUnchanged = RunStamp() == before, Scope = "AUTHORIZED_GUEST" }); throw; }
        }));
    }
}
internal static class OperationalRegistryTests
{
    public static void Register(List<(string, Action)> tests)
    {
        void Test(string name, Action action) => OperationalEvidence.Register(tests, name, action);
        Test("consulta transacional de Run real é somente leitura e rollback", StartupRegistryTransaction.ProbeRunReadOnly);
        Test("hive privada faz leitura desativação e reposição de String e ExpandString", () =>
        {
            foreach (var kind in new[] { RegistryValueKind.String, RegistryValueKind.ExpandString })
            {
                using var hive = new PrivateHive();
                var entry = hive.Seed(kind);
                var backend = new HiveBackend(hive);
                var journal = new OptimizationJournal(Path.Combine(hive.Folder, "journal.jsonl"));
                var service = new StartupChangeService(backend, journal);
                var plan = service.Prepare(entry, false); service.Execute(plan, plan.Confirmation);
                OperationalEvidence.Check(backend.Read(entry) == null);
                var receipt = new StartupChangeService(backend, journal).Restorable().Single();
                var restore = service.Prepare(receipt, true); service.Execute(restore, restore.Confirmation);
                using var key = hive.Root.OpenSubKey(PrivateHive.RunPath)!;
                OperationalEvidence.Check((string?)key.GetValue(entry.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) == entry.Command);
                OperationalEvidence.Check(key.GetValueKind(entry.Name) == kind);
                hive.CheckNeighbours();
                var events = journal.Read();
                OperationalEvidence.Check(events.Count == 4 && events.Count(e => e.State == "intent") == 2 && events.Count(e => e.State == "completed") == 2);
                OperationalEvidence.Record("private-hive-roundtrip", new { hive.Folder, ValueKind = kind.ToString(), OriginalValueRestored = true, OtherFixtureValuesUnchanged = true, Receipts = events.Count, PrivateHiveTransactionProbeCode = hive.ProbeTransaction(), ActiveWindowsRunModified = false });
            }
        });
        Test("alteração externa na hive privada invalida prévia", () =>
        {
            using var hive = new PrivateHive(); var entry = hive.Seed(RegistryValueKind.String);
            var journal = new OptimizationJournal(Path.Combine(hive.Folder, "journal.jsonl"));
            var service = new StartupChangeService(new HiveBackend(hive), journal);
            var plan = service.Prepare(entry, false);
            using (var key = hive.Root.OpenSubKey(PrivateHive.RunPath, true)!) key.SetValue(entry.Name, "external-change");
            OperationalEvidence.Failure(() => service.Execute(plan, plan.Confirmation));
            using var verify = hive.Root.OpenSubKey(PrivateHive.RunPath)!;
            OperationalEvidence.Check((string?)verify.GetValue(entry.Name) == "external-change");
            hive.CheckNeighbours();
        });
        Test("reposição não sobrescreve conflito real na hive privada", () =>
        {
            using var hive = new PrivateHive(); var entry = hive.Seed(RegistryValueKind.String);
            var service = new StartupChangeService(new HiveBackend(hive), new OptimizationJournal(Path.Combine(hive.Folder, "journal.jsonl")));
            var disable = service.Prepare(entry, false); service.Execute(disable, disable.Confirmation);
            var restore = service.Prepare(entry, true);
            using (var key = hive.Root.OpenSubKey(PrivateHive.RunPath, true)!) key.SetValue(entry.Name.ToLowerInvariant(), "new-owner");
            OperationalEvidence.Failure(() => service.Execute(restore, restore.Confirmation));
            using var verify = hive.Root.OpenSubKey(PrivateHive.RunPath)!;
            OperationalEvidence.Check((string?)verify.GetValue(entry.Name) == "new-owner"); hive.CheckNeighbours();
        });
        Test("handle sem escrita preserva hive e registra falha", () =>
        {
            using var hive = new PrivateHive(); var entry = hive.Seed(RegistryValueKind.String);
            var journal = new OptimizationJournal(Path.Combine(hive.Folder, "journal.jsonl"));
            var service = new StartupChangeService(new HiveBackend(hive, true), journal);
            var plan = service.Prepare(entry, false);
            var failure = OperationalEvidence.Failure(() => service.Execute(plan, plan.Confirmation));
            OperationalEvidence.Check(failure is UnauthorizedAccessException or IOException or System.Security.SecurityException);
            using var verify = hive.Root.OpenSubKey(PrivateHive.RunPath)!;
            OperationalEvidence.Check((string?)verify.GetValue(entry.Name) == entry.Command);
            OperationalEvidence.Check(journal.Read().Last().State == "failed");
            hive.CheckNeighbours();
            OperationalEvidence.Record("private-hive-readonly-denial", new { hive.Folder, Exception = failure.GetType().Name, NoValueRemoved = true, FailedReceipt = true });
        });
        Test("backend de produção rejeita recibo da hive privada", () =>
        {
            using var hive = new PrivateHive(); var entry = hive.Seed(RegistryValueKind.String);
            OperationalEvidence.Failure(() => new WindowsStartupBackend().ValidateOriginal(entry));
            hive.CheckNeighbours();
        });
        Test("erro de lançamento MSI e cancelamento de elevação deixam recibo de falha", () =>
        {
            var app = new InstalledApp("Google Chrome", "Google LLC", "fixture", null, "", "Tradicional", "metadata only")
            { ProductCode = "{11111111-1111-1111-1111-111111111111}", WindowsInstaller = true };
            foreach (int code in new[] { 5, 1223 })
            {
                var journal = new OptimizationJournal(Path.Combine(OperationalEvidence.Folder(), "msi-failure.jsonl"));
                var service = new ApplicationUninstallService(new FailingInstaller(code), journal);
                var plan = service.Prepare(app);
                var exception = OperationalEvidence.Failure(() => service.Execute(plan, plan.Confirmation));
                OperationalEvidence.Check(exception is Win32Exception w && w.NativeErrorCode == code);
                OperationalEvidence.Check(journal.Read().Last().State == (code == 1223 ? "cancelled" : "failed"));
                OperationalEvidence.Check(journal.Read().Last().Detail.Contains("Win32=" + code));
            }
        });
        Test("MSI descartável não recebe compatibilidade de fabricante fictício", () =>
        {
            var fixture = new InstalledApp("Respiro Operational Fixture", "Respiro Tests", "1.0.0", null, "", "Tradicional", "test only")
            { ProductCode = Guid.NewGuid().ToString("B"), WindowsInstaller = true };
            OperationalEvidence.Check(!OptimizationSafety.SafeMsi(fixture));
        });
    }
    sealed class FailingInstaller(int code) : IAppUninstallBackend
    {
        public bool StillSame(InstalledApp app) => true;
        public void Launch(InstalledApp app) => throw new Win32Exception(code, "Fixture: no installer process was started.");
    }
    sealed class HiveBackend(PrivateHive hive, bool readOnly = false) : IStartupBackend
    {
        public void ValidateOriginal(StartupEntry entry)
        {
            if (entry.Locator != hive.Locator || entry.Name != "RespiroFixture" || !entry.Manageable) throw new IOException("Somente a fixture desta hive é aceita.");
        }
        public StartupEntry? Read(StartupEntry entry)
        {
            ValidateOriginal(entry);
            using var key = hive.Root.OpenSubKey(PrivateHive.RunPath)!;
            if (!StartupRegistryTransaction.NameExists(key.GetValueNames(), entry.Name)) return null;
            return entry with { Command = key.GetValue(entry.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "", ValueKind = (int)key.GetValueKind(entry.Name) };
        }
        public void Remove(StartupEntry entry)
        {
            if (Read(entry) != entry) throw new IOException("Fixture mudou.");
            using var key = hive.Root.OpenSubKey(PrivateHive.RunPath, !readOnly)!;
            key.DeleteValue(entry.Name, true); key.Flush();
        }
        public void Restore(StartupEntry entry)
        {
            ValidateOriginal(entry);
            using var key = hive.Root.OpenSubKey(PrivateHive.RunPath, !readOnly)!;
            if (StartupRegistryTransaction.NameExists(key.GetValueNames(), entry.Name)) throw new IOException("Conflito na fixture.");
            key.SetValue(entry.Name, entry.Command, (RegistryValueKind)entry.ValueKind); key.Flush();
        }
    }
    sealed class PrivateHive : IDisposable
    {
        internal const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        internal string Folder { get; } = OperationalEvidence.Folder();
        internal RegistryKey Root { get; }
        internal string Locator => Path.Combine(Folder, "fixture.hiv") + "::" + RunPath;
        internal PrivateHive()
        {
            int code = RegLoadAppKey(Path.Combine(Folder, "fixture.hiv"), out var handle, 0xF003F, 1, 0);
            if (code != 0) { handle.Dispose(); throw new Win32Exception(code, "RegLoadAppKey da fixture falhou."); }
            Root = RegistryKey.FromHandle(handle);
            using var key = Root.CreateSubKey(RunPath, true);
            key.SetValue("Keep-1", "preserve", RegistryValueKind.String);
            key.SetValue("Keep-2", new[] { "a", "b" }, RegistryValueKind.MultiString);
        }
        internal StartupEntry Seed(RegistryValueKind kind)
        {
            string raw = kind == RegistryValueKind.ExpandString ? "\"%TEMP%\\RespiroFixture.exe\" --fixture" : "\"C:\\Fixture\\RespiroFixture.exe\" --fixture";
            using var key = Root.OpenSubKey(RunPath, true)!; key.SetValue("RespiroFixture", raw, kind); key.Flush();
            return new("RespiroFixture", StartupOrigin.UserRun, Locator, raw, (int)kind, @"C:\Fixture\RespiroFixture.exe", "Fixture only", "", null, true, "Private file-backed hive; never active HKCU.");
        }
        internal void CheckNeighbours()
        {
            using var key = Root.OpenSubKey(RunPath)!;
            OperationalEvidence.Check((string?)key.GetValue("Keep-1") == "preserve");
            OperationalEvidence.Check(key.GetValueKind("Keep-1") == RegistryValueKind.String && key.GetValueKind("Keep-2") == RegistryValueKind.MultiString);
            OperationalEvidence.Check(((string[])key.GetValue("Keep-2")!).SequenceEqual(new[] { "a", "b" }));
        }
        internal int ProbeTransaction()
        {
            using var tx = CreateTransaction(IntPtr.Zero, IntPtr.Zero, 0, 0, 0, 5000, "Respiro private hive probe");
            if (tx.IsInvalid) return -Marshal.GetLastWin32Error();
            try
            {
                int code = OpenTransacted(Root.Handle, RunPath, 0, 1, out var key, tx, IntPtr.Zero);
                key.Dispose(); return code;
            }
            finally { RollbackTransaction(tx); }
        }
        public void Dispose() => Root.Dispose();
        [DllImport("advapi32.dll", EntryPoint = "RegLoadAppKeyW", CharSet = CharSet.Unicode)] static extern int RegLoadAppKey(string file, out SafeRegistryHandle root, uint access, uint options, uint reserved);
        [DllImport("advapi32.dll", EntryPoint = "RegOpenKeyTransactedW", CharSet = CharSet.Unicode)] static extern int OpenTransacted(SafeRegistryHandle root, string sub, uint options, uint access, out SafeRegistryHandle key, SafeFileHandle transaction, IntPtr extended);
        [DllImport("ktmw32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern SafeFileHandle CreateTransaction(IntPtr attributes, IntPtr id, uint options, uint isolation, uint flags, uint timeout, string description);
        [DllImport("ktmw32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool RollbackTransaction(SafeFileHandle tx);
    }
}
