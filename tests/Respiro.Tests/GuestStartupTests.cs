using Microsoft.Win32;
using Respiro.Core;
namespace Respiro.Tests;

internal static class GuestStartupTests
{
    public static void Register(List<(string, Action)> tests) => OperationalEvidence.Register(tests,
        "VM: round trip transacional em valor Run próprio e conflito externo", () =>
        {
            GuestValidationGuard.AssertActive();
            string nonce = Guid.NewGuid().ToString("N"), name = "Respiro.ReleaseFixture." + nonce;
            string raw = "\"" + GuestValidationGuard.FixtureExecutable + "\" --fixture " + nonce;
            const string external = "Respiro guest fixture external conflict";
            using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var key = root.CreateSubKey(WindowsStartupBackend.RunKey, true);
            if (StartupRegistryTransaction.NameExists(key.GetValueNames(), name)) throw new IOException("Nome da fixture já existe.");
            bool seeded = false;
            var entry = new StartupEntry(name, StartupOrigin.UserRun, WindowsStartupBackend.RunKey, raw, 1,
                GuestValidationGuard.FixtureExecutable, "Guest fixture only", nonce, null, true, "Guest-only backend; production policy remains locked.");
            try
            {
                key.SetValue(name, raw, RegistryValueKind.String); seeded = true; key.Flush();
                var backend = new GuestRunBackend(entry);
                var journal = new OptimizationJournal(Path.Combine(OperationalEvidence.Folder(), "active-run-journal.jsonl"));
                var service = new StartupChangeService(backend, journal);
                var disable = service.Prepare(entry, false); service.Execute(disable, disable.Confirmation);
                OperationalEvidence.Check(backend.Read(entry) == null);
                var restore = service.Prepare(entry, true); service.Execute(restore, restore.Confirmation);
                OperationalEvidence.Check(backend.Read(entry) == entry);
                var disableAgain = service.Prepare(entry, false); service.Execute(disableAgain, disableAgain.Confirmation);
                var conflict = service.Prepare(entry, true);
                key.SetValue(name, external, RegistryValueKind.String); key.Flush();
                OperationalEvidence.Failure(() => service.Execute(conflict, conflict.Confirmation));
                OperationalEvidence.Check((string?)key.GetValue(name) == external);
                OperationalEvidence.Record("guest-active-run-roundtrip", new {
                    Name = name, RoundTripVerified = true, OriginalStringRestored = true, ExternalConflictPreserved = true,
                    ReceiptCount = journal.Read().Count, UsesProductionTransactionPrimitive = true, ProductionReleaseGateUnlocked = false
                });
            }
            finally
            {
                // Exact fixture value only; never remove the Run key or overwrite a third-party value.
                if (seeded && key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string current)
                {
                    if (current == raw || current == external) { key.DeleteValue(name, true); key.Flush(); }
                    else throw new IOException("A fixture mudou para conteúdo desconhecido: preservar e investigar no convidado.");
                }
            }
        });
    sealed class GuestRunBackend(StartupEntry owned) : IStartupBackend
    {
        public void ValidateOriginal(StartupEntry entry)
        {
            GuestValidationGuard.AssertActive();
            if (entry != owned) throw new IOException("Backend aceita somente o valor criado por este caso no convidado.");
        }
        public StartupEntry? Read(StartupEntry entry)
        {
            ValidateOriginal(entry);
            using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using var key = root.OpenSubKey(WindowsStartupBackend.RunKey);
            if (key == null || !StartupRegistryTransaction.NameExists(key.GetValueNames(), entry.Name)) return null;
            return entry with { Command = key.GetValue(entry.Name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "", ValueKind = (int)key.GetValueKind(entry.Name) };
        }
        public void Remove(StartupEntry entry) { ValidateOriginal(entry); StartupRegistryTransaction.Change(entry, false); }
        public void Restore(StartupEntry entry) { ValidateOriginal(entry); StartupRegistryTransaction.Change(entry, true); }
    }
}
