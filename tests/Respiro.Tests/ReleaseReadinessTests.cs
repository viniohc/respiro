using Respiro.Core;
namespace Respiro.Tests;

internal static class ReleaseReadinessTests
{
    static void Check(bool condition) { if (!condition) throw new Exception("Regressão no bloqueio de lançamento."); }
    static void Blocked(Action action) { try { action(); } catch (IOException) { return; } throw new Exception("Uma operação não validada foi liberada."); }
    public static void Register(List<(string, Action)> tests)
    {
        tests.Add(("Lançamento: todos os controles pendentes permanecem diagnósticos", () => {
            foreach (var operation in Enum.GetValues<BetaOperation>()) { Check(!BetaReleasePolicy.Allows(operation)); Blocked(() => BetaReleasePolicy.Demand(operation)); }
            Check(BetaReleasePolicy.Mechanisms.Count(m => m.State.Contains("somente para diagnóstico")) == 3);
        }));
        tests.Add(("Lançamento: backend Run recusa antes de abrir ou alterar registro", () => {
            var entry = new StartupEntry("fixture", StartupOrigin.UserRun, "", "", 1, "", "", "", null, true, "");
            var backend = new WindowsStartupBackend();
            Blocked(() => backend.ValidateOriginal(entry)); Blocked(() => backend.Remove(entry)); Blocked(() => backend.Restore(entry));
        }));
        tests.Add(("Lançamento: fechamento e MSI não podem ser liberados por seleção", () => {
            var process = new ProcessSample(int.MaxValue, 0, "fixture", "", "", null, null, null, 1, true, "", "");
            var close = new WindowsProcessCloseBackend();
            Check(!close.StillSame(process)); Blocked(() => close.RequestNormalClose(process));
            var app = new InstalledApp("Google Chrome", "Google LLC", "1", null, "", "Tradicional", "")
            { ProductCode = "{11111111-1111-1111-1111-111111111111}", WindowsInstaller = true };
            Check(!ApplicationUninstallService.CanOfferDirect(app));
            Check(!new WindowsMsiUninstallBackend().StillSame(app));
            Blocked(() => new WindowsMsiUninstallBackend().Launch(app));
        }));
        tests.Add(("Lançamento: ambiente operacional exige máquina diferente autorização e VM", () => {
            string source = new('A', 64), guest = new('B', 64);
            Blocked(() => GuestValidationGuard.ValidateInputs(source, source, true, "Virtual Machine"));
            Blocked(() => GuestValidationGuard.ValidateInputs(source, guest, false, "Virtual Machine"));
            Blocked(() => GuestValidationGuard.ValidateInputs(source, guest, true, "Physical Desktop"));
            GuestValidationGuard.ValidateInputs(source, guest, true, "Virtual Machine");
            Check(!GuestValidationGuard.IsActive);
            Blocked(GuestValidationGuard.AssertActive);
        }));
    }
}
