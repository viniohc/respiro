using Microsoft.Win32;
using System.Diagnostics;
namespace Respiro.Core;

public interface IAppUninstallBackend { bool StillSame(InstalledApp selected); void Launch(InstalledApp selected); }
public sealed class UninstallPlan
{
    public InstalledApp App { get; }
    public Guid Id { get; } = Guid.NewGuid();
    public DateTime Created { get; } = DateTime.UtcNow;
    public string Confirmation => "DESINSTALAR " + App.Name;
    internal UninstallPlan(InstalledApp app) { App = app; }
}
public sealed class ApplicationUninstallService(IAppUninstallBackend backend, IOptimizationJournal journal)
{
    readonly HashSet<Guid> used = [];
    public static bool CanOfferDirect(InstalledApp app) => BetaReleasePolicy.Allows(BetaOperation.MsiUninstall) && OptimizationSafety.SafeMsi(app) && WindowsManagementGuard.AllowsChanges(out _);
    public static string Availability(InstalledApp app) => !BetaReleasePolicy.Allows(BetaOperation.MsiUninstall) ? BetaReleasePolicy.Reason(BetaOperation.MsiUninstall) : OptimizationSafety.SafeMsi(app) && !WindowsManagementGuard.AllowsChanges(out _)
        ? "Vínculo corporativo ou gerenciamento inconclusivo: alteração direta bloqueada; revise com o administrador ou no Windows."
        : OptimizationSafety.SafeMsi(app)
        ? "MSI compatível: abre o desinstalador oficial com interface e confirmação. Pode exigir administrador; dados locais e dependências precisam de revisão."
        : "Desinstalação direta bloqueada: identidade MSI compatível não comprovada ou componente protegido. Use a revisão individual do Windows.";
    public UninstallPlan Prepare(InstalledApp app)
    {
        if (!OptimizationSafety.SafeMsi(app) || !backend.StillSame(app)) throw new IOException(Availability(app));
        return new(app);
    }
    public string Execute(UninstallPlan plan, string confirmation)
    {
        if (confirmation != plan.Confirmation || DateTime.UtcNow - plan.Created > TimeSpan.FromMinutes(5) || used.Contains(plan.Id) ||
            !OptimizationSafety.SafeMsi(plan.App) || !backend.StillSame(plan.App)) throw new IOException("Confirmação, prévia ou identidade do aplicativo inválida.");
        journal.Append(new(plan.Id, DateTime.UtcNow, "uninstall-msi", "intent", null, plan.App.Name + "; produto=" + plan.App.ProductCode));
        used.Add(plan.Id);
        try { backend.Launch(plan.App); }
        catch (Exception e)
        {
            try { journal.Append(new(plan.Id, DateTime.UtcNow, "uninstall-msi", e is System.ComponentModel.Win32Exception { NativeErrorCode: 1223 } ? "cancelled" : "failed", null, OptimizationEvent.FailureDetail(e))); } catch { }
            throw;
        }
        const string detail = "Interface do Windows Installer solicitada. Nenhuma desinstalação ou recuperação de espaço foi confirmada; a decisão final pertence ao instalador.";
        try { journal.Append(new(plan.Id, DateTime.UtcNow, "uninstall-msi", "launched", null, detail)); } catch { }
        return detail;
    }
}
public sealed class WindowsMsiUninstallBackend : IAppUninstallBackend
{
    public bool StillSame(InstalledApp app)
    {
        if (!BetaReleasePolicy.Allows(BetaOperation.MsiUninstall)) return false;
        try
        {
            if (!WindowsManagementGuard.AllowsChanges(out _) || !OptimizationSafety.SafeMsi(app) || !Enum.TryParse<RegistryHive>(app.UninstallHive, out var hive) ||
                hive is not (RegistryHive.CurrentUser or RegistryHive.LocalMachine) ||
                !Enum.TryParse<RegistryView>(app.UninstallView, out var view) || view is not (RegistryView.Registry32 or RegistryView.Registry64) ||
                app.UninstallKey != @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + app.ProductCode) return false;
            using var registry = RegistryKey.OpenBaseKey(hive, view);
            using var key = registry.OpenSubKey(app.UninstallKey);
            return key != null && key.GetValue("DisplayName") as string == app.Name &&
                key.GetValue("Publisher") as string == app.Publisher && key.GetValue("DisplayVersion") as string == app.Version &&
                key.GetValue("WindowsInstaller") is int n && n == 1 &&
                !(key.GetValue("SystemComponent") is int s && s != 0) && key.GetValue("ParentKeyName") == null;
        }
        catch { return false; }
    }
    internal static ProcessStartInfo CreateStartInfo(InstalledApp app)
    {
        if (!OptimizationSafety.SafeMsi(app)) throw new IOException("Produto MSI incompatível.");
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "msiexec.exe"))
        { UseShellExecute = false };
        start.ArgumentList.Add("/x"); start.ArgumentList.Add(app.ProductCode!);
        start.ArgumentList.Add("/qf"); start.ArgumentList.Add("/norestart");
        return start;
    }
    public void Launch(InstalledApp app)
    {
        if (!StillSame(app)) throw new IOException("Cadastro do aplicativo mudou. Nenhum desinstalador foi iniciado.");
        using var process = Process.Start(CreateStartInfo(app)) ?? throw new IOException("Windows Installer não iniciou.");
    }
}
public sealed record CleanupIntegration(string Category, string Class, string Mechanism, string Limit);
public static class CleanupIntegrationCatalog
{
    public static IReadOnlyList<CleanupIntegration> Rows => WindowsCleanupCatalog.Categories.Select(c =>
        new CleanupIntegration(c.Name, c.SupportsRemoval ? "A — Respiro (restrito)" :
            c.Kind is WindowsCleanupKind.DirectXShaders or WindowsCleanupKind.DeliveryOptimization ? "C — interface do Windows" : "D — bloqueado",
            c.SupportsRemoval ? "Prévia, seleção, confirmação e revalidação existentes" :
            c.Kind is WindowsCleanupKind.DirectXShaders or WindowsCleanupKind.DeliveryOptimization ? "Revisar a categoria na manutenção nativa" : "Sem prova suficiente para remoção",
            c.Restriction)).Concat(new[]
            {
                new CleanupIntegration("Temporários próprios do Respiro (Scratch)", "A — Respiro (restrito)", "Adaptador próprio existente com comprovante e hash", "Somente cópias exportadas pelo próprio Respiro, com prévia e confirmação; não apaga relatórios do usuário."),
                new CleanupIntegration("Temporários de instalação e caches de aplicativos", "C — interface do Windows", "Arquivos temporários / manutenção própria do aplicativo", "Origem, dependências e término da instalação precisam ser comprovados; caches genéricos permanecem protegidos."),
                new CleanupIntegration("Windows Update / instalação anterior", "C — interface do Windows", "Recomendações de limpeza", "Preserva critérios nativos e explica perda da possibilidade de retorno."),
                new CleanupIntegration("WinSxS / restauração / paginação", "D — bloqueado para alteração", "Diagnóstico e orientação oficiais", "Dados e dependências de sistema; sem limpeza por este aplicativo.")
            }).ToArray();
}
