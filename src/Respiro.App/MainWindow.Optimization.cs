using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Respiro.Core;
namespace Respiro.App;

public partial class MainWindow
{
    IOptimizationJournal OptimizationLog => new OptimizationJournal(Path.Combine(DataFolder, "optimization.jsonl"));
    StartupChangeService? startupChanges;
    StartupChangeService StartupChanges => startupChanges ??= new(new WindowsStartupBackend(), OptimizationLog);
    async Task OptimizationDiagnostic(string path)
    {
        await OptimizationQuery(async token =>
        {
            var startup = await Task.Run(() => StartupInventory.Read(token), token);
            var processes = await Task.Run(() => ProcessMonitor.Sample(token), token);
            StartupGrid.ItemsSource = startup.Entries; ProcessGrid.ItemsSource = processes;
            using var current = System.Diagnostics.Process.GetCurrentProcess();
            var full = Path.GetFullPath(path); Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, JsonSerializer.Serialize(new {
                At = DateTimeOffset.Now, Mode = "READ_ONLY", Candidate = BetaReleasePolicy.CandidateId, Readiness = BetaReleasePolicy.Mechanisms, Startup = startup, Processes = processes,
                current.WorkingSet64, current.PeakWorkingSet64,
                CleaningExecuted = false, WindowsSettingsChanged = false, ProcessCloseRequested = false, UninstallerLaunched = false
            }, new JsonSerializerOptions { WriteIndented = true }));
            OptimizationStatus.Text = $"{startup.Entries.Count} entradas e {processes.Count} processos consultados; nenhuma alteração.";
            Navigate(Page.Optimization);
        });
    }
    void InitializeOptimization()
    {
        OptimizationCleanupGrid.ItemsSource = CleanupIntegrationCatalog.Rows;
        StartupDetail.Text = BetaReleasePolicy.Reason(BetaOperation.StartupChange);
        ProcessDetail.Text = BetaReleasePolicy.Reason(BetaOperation.NormalClose);
        OptimizationStatus.Text = BetaReleasePolicy.CandidateId + ": ações pendentes de VM permanecem indisponíveis.";
        foreach (var button in new[] { DisableStartupButton, RestoreStartupButton }) { button.ToolTip = StartupDetail.Text; ToolTipService.SetShowOnDisabled(button, true); }
        CloseProcessButton.ToolTip = ProcessDetail.Text; ToolTipService.SetShowOnDisabled(CloseProcessButton, true);
        UninstallSelectedButton.ToolTip = BetaReleasePolicy.Reason(BetaOperation.MsiUninstall); ToolTipService.SetShowOnDisabled(UninstallSelectedButton, true);
    }
    async Task OptimizationQuery(Func<CancellationToken, Task> query)
    {
        if (cancellation != null) return;
        cancellation = new();
        var token = cancellation.Token;
        OptimizationTabs.IsEnabled = false;
        AnalyzeButton.IsEnabled = WindowsDiagnoseButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        BeginOperation("Consultando otimização — somente leitura");
        try { await query(token); }
        catch (OperationCanceledException) { OptimizationStatus.Text = "Consulta cancelada; nenhum ajuste foi aplicado."; }
        catch (Exception ex) { OptimizationStatus.Text = "Consulta inconclusiva: " + ex.Message; }
        finally
        {
            cancellation.Dispose(); cancellation = null;
            OptimizationTabs.IsEnabled = AnalyzeButton.IsEnabled = WindowsDiagnoseButton.IsEnabled = true;
            CancelButton.IsEnabled = false; EndOperation(); Status.Text = OptimizationStatus.Text; UpdateOptimizationSelection();
        }
    }
    async Task OptimizationChange(Func<string> operation)
    {
        if (cancellation != null) return;
        cancellation = new();
        OptimizationTabs.IsEnabled = AnalyzeButton.IsEnabled = WindowsDiagnoseButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        BeginOperation("Aplicando a operação individual confirmada; aguarde o resultado");
        try { OptimizationStatus.Text = await Task.Run(operation); }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223) { OptimizationStatus.Text = "Operação cancelada na confirmação do Windows. Nenhuma conclusão de desinstalação foi registrada."; }
        catch (Exception ex) { OptimizationStatus.Text = "Operação não concluída: " + ex.Message; }
        finally
        {
            cancellation.Dispose(); cancellation = null;
            OptimizationTabs.IsEnabled = AnalyzeButton.IsEnabled = WindowsDiagnoseButton.IsEnabled = true;
            EndOperation(); Status.Text = OptimizationStatus.Text; UpdateOptimizationSelection();
        }
    }
    async void RefreshOptimizationStartup(object sender, RoutedEventArgs e) =>
        await OptimizationQuery(async token =>
        {
            var report = await Task.Run(() => StartupInventory.Read(token), token);
            StartupGrid.ItemsSource = report.Entries;
            StartupRestores.ItemsSource = await Task.Run(() => StartupChanges.Restorable(), token);
            StartupGrid.UnselectAll(); StartupRestores.SelectedIndex = -1;
            OptimizationStatus.Text = $"{report.Entries.Count} entradas; {(report.Cancelled ? "consulta parcial cancelada" : "consulta concluída")}. " +
                string.Join(" · ", report.Notices.Take(5));
        });
    async void RefreshOptimizationProcesses(object sender, RoutedEventArgs e) =>
        await OptimizationQuery(async token =>
        {
            ProcessGrid.ItemsSource = await Task.Run(() => ProcessMonitor.Sample(token), token);
            ProcessGrid.UnselectAll();
            OptimizationStatus.Text = "Amostra de processos concluída. RAM é ocupação atual; CPU e E/S são medições do intervalo. Nada foi encerrado.";
        });
    void OptimizationSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateOptimizationSelection();
    void UpdateOptimizationSelection()
    {
        if (DisableStartupButton == null) return;
        DisableStartupButton.IsEnabled = cancellation == null && BetaReleasePolicy.Allows(BetaOperation.StartupChange) && StartupGrid.SelectedItem is StartupEntry { Manageable: true };
        RestoreStartupButton.IsEnabled = cancellation == null && BetaReleasePolicy.Allows(BetaOperation.StartupChange) && StartupRestores.SelectedItem is StartupEntry;
        CloseProcessButton.IsEnabled = cancellation == null && BetaReleasePolicy.Allows(BetaOperation.NormalClose) && ProcessGrid.SelectedItem is ProcessSample { CanRequestClose: true };
        if (StartupGrid.SelectedItem is StartupEntry startup)
            StartupDetail.Text = $"{startup.Name}: {BetaReleasePolicy.Reason(BetaOperation.StartupChange)}\n{startup.Reason}\n{startup.Impact}\nEstado nativo: {startup.NativeState}. Repor uma entrada não altera a habilitação feita pelas Configurações do Windows.";
        if (ProcessGrid.SelectedItem is ProcessSample process)
            ProcessDetail.Text = $"{process.Name} (PID {process.Id}): {BetaReleasePolicy.Reason(BetaOperation.NormalClose)}\n{process.Reason}\n{process.Relation}. Fechar pode perder trabalho não salvo; não impede inicialização futura.";
    }
    async void DisableOptimizationStartup(object sender, RoutedEventArgs e)
    {
        if (cancellation != null || StartupGrid.SelectedItem is not StartupEntry { Manageable: true } item) return;
        try
        {
            var plan = StartupChanges.Prepare(item, false);
            if (MessageBox.Show($"Desativar somente a entrada «{item.Name}»?\n\nOrigem: {item.Source}\nExecutável: {item.Target}\n\nO aplicativo não abrirá por esta entrada no próximo login. Outras entradas e a habilitação do Windows são independentes. Não libera espaço de instalação. O valor original será registrado localmente para reposição.",
                "Confirmar alteração de inicialização", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            await OptimizationChange(() => StartupChanges.Execute(plan, plan.Confirmation));
            StartupRestores.ItemsSource = StartupChanges.Restorable();
            StartupGrid.ItemsSource = null;
        }
        catch (Exception ex) { OptimizationStatus.Text = "Entrada preservada: " + ex.Message; }
    }
    async void RestoreOptimizationStartup(object sender, RoutedEventArgs e)
    {
        if (cancellation != null || StartupRestores.SelectedItem is not StartupEntry item) return;
        try
        {
            var plan = StartupChanges.Prepare(item, true);
            if (MessageBox.Show($"Repor a entrada «{item.Name}» no local original?\n\nNenhum valor ou atalho existente será sobrescrito. O aplicativo poderá voltar a abrir no login, dependendo do estado nativo do Windows. O executável será revalidado.",
                "Confirmar reposição", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            await OptimizationChange(() => StartupChanges.Execute(plan, plan.Confirmation));
            StartupRestores.ItemsSource = StartupChanges.Restorable(); StartupGrid.ItemsSource = null;
        }
        catch (Exception ex) { OptimizationStatus.Text = "Reposição bloqueada: " + ex.Message; }
    }
    async void CloseOptimizationProcess(object sender, RoutedEventArgs e)
    {
        if (cancellation != null || ProcessGrid.SelectedItem is not ProcessSample { CanRequestClose: true } item) return;
        if (MessageBox.Show($"Solicitar fechamento normal de «{item.Name}» (PID {item.Id})?\n\nPode haver trabalho não salvo. O aplicativo pode pedir para salvar ou permanecer na bandeja. Nenhum processo relacionado será fechado automaticamente. Isso não altera a inicialização futura e não comprova economia de memória.",
            "Confirmar solicitação de fechamento", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        await OptimizationChange(() => new ProcessCloseService(new WindowsProcessCloseBackend(), OptimizationLog).Request(item, "FECHAR " + item.Id));
    }
    void OptimizationApps(object sender, RoutedEventArgs e) => Navigate(Page.Apps);
    void OptimizationStartupSettings(object sender, RoutedEventArgs e) => ShowOfficialChoice("startup");
    void OptimizationBackgroundSettings(object sender, RoutedEventArgs e) => ShowOfficialChoice("apps");
    void OptimizationMaintenance(object sender, RoutedEventArgs e) => Navigate(Page.Maintenance);
    void UpdateUninstallSelection()
    {
        if (UninstallSelectedButton == null) return;
        UninstallSelectedButton.IsEnabled = cancellation == null && InstalledAppsGrid.SelectedItem is InstalledApp app && ApplicationUninstallService.CanOfferDirect(app);
    }
    async void UninstallSelectedApplication(object sender, RoutedEventArgs e)
    {
        if (cancellation != null || InstalledAppsGrid.SelectedItem is not InstalledApp app) return;
        try
        {
            var service = new ApplicationUninstallService(new WindowsMsiUninstallBackend(), OptimizationLog);
            var plan = service.Prepare(app);
            if (MessageBox.Show($"Abrir a desinstalação oficial de «{app.Name}» {app.Version}?\n\nFabricante: {app.Publisher}\nProduto MSI: {app.ProductCode}\n\nPode remover dados locais ou dependências e exigir confirmação administrativa pelo instalador. Não há reversão automática: reinstalação depende do fornecedor. Nenhuma opção silenciosa será enviada; revise a interface do instalador.",
                "Confirmar desinstalação individual", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            await OptimizationChange(() => service.Execute(plan, plan.Confirmation));
            AppRelationship.Text = OptimizationStatus.Text;
        }
        catch (Exception ex) { AppRelationship.Text = "Desinstalador não iniciado: " + ex.Message; }
    }
}
