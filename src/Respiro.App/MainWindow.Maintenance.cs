using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Respiro.Core;
using Microsoft.Win32;
namespace Respiro.App;

/// <summary>Diagnóstico do Windows, inventário e limpeza manual restrita. Validações reais ficam no CleanupService.</summary>
public partial class MainWindow
{
    MaintenanceReport? maintenance;
    readonly OwnedTemporaryStore temporaryStore = new();
    CleanupService? cleanupService;
    CleanupPlan? cleanupPlan;
    IReadOnlyList<CleanupOutcome>? cleanupOutcomes;

    async void DiagnoseWindows(object sender, RoutedEventArgs e) { Navigate(Page.Maintenance); await StartMaintenance(); }
    async Task StartMaintenance()
    {
        if (cancellation != null) return;
        cancellation = new(); AnalyzeButton.IsEnabled = false; WindowsDiagnoseButton.IsEnabled = false; CancelButton.IsEnabled = true;
        WindowsSummary.Text = "Consultando e medindo. Pode levar alguns minutos; Cancelar interrompe a enumeração.";
        BeginOperation("Diagnosticando o Windows — somente leitura");
        int step = 0;
        try
        {
            maintenance = await new WindowsMaintenanceService().CollectAsync(new Progress<string>(s => { step++; OperationDetail.Text = $"Etapa {step} · {Elapsed()}"; OperationPath.Text = s; OperationPercent.Text = "Duração varia conforme o sistema"; }), cancellation.Token);
            WindowsDiagnosticsGrid.ItemsSource = maintenance.Diagnostics.OrderByDescending(d => d.Bytes).ToList();
            OptionalFeaturesGrid.ItemsSource = maintenance.Features.OrderBy(f => f.Name).ToList();
            AppsEmpty.Visibility = Visibility.Collapsed; InstalledAppsGrid.Visibility = Visibility.Visible;
            ApplyAppFilter();
            WindowsSummary.Text = $"{(maintenance.Cancelled ? "Parcial / cancelado" : "Diagnóstico concluído")} · {maintenance.Diagnostics.Count} medições · {maintenance.Applications.Count} aplicativos · {maintenance.Features.Count} recursos · {Elapsed()}. Valores podem se sobrepor e não devem ser somados.";
            HomeWindowsSummary.Text = $"{(maintenance.Cancelled ? "Diagnóstico parcial" : "Diagnóstico concluído")} às {DateTime.Now:HH:mm}: {maintenance.Diagnostics.Count} medições, {maintenance.Applications.Count} aplicativos e {maintenance.Features.Count} recursos opcionais. Medições inconclusivas aparecem como tal.";
            WindowsNotices.Text = maintenance.Notices.Count == 0 ? "Nenhuma restrição ou falha registrada nesta consulta." : string.Join(Environment.NewLine + Environment.NewLine, maintenance.Notices);
            Status.Text = "Diagnóstico Windows disponível; configurações e arquivos do Windows preservados.";
            Log("Diagnóstico Windows", $"Medições={maintenance.Diagnostics.Count}; Apps={maintenance.Applications.Count}");
        }
        catch (Exception e) { WindowsSummary.Text = "Diagnóstico inconclusivo: " + e.Message; }
        finally { cancellation.Dispose(); cancellation = null; AnalyzeButton.IsEnabled = true; WindowsDiagnoseButton.IsEnabled = true; CancelButton.IsEnabled = false; EndOperation(); UpdateDisk(); }
    }

    void AppFilterChanged(object sender, TextChangedEventArgs e) => ApplyAppFilter();
    void ApplyAppFilter()
    {
        if (InstalledAppsGrid == null || maintenance == null) return;
        string filter = AppSearch.Text.Trim();
        var matches = ApplicationInventoryView.Filter(maintenance.Applications, filter, AppTypeFilter.SelectedIndex);
        var list = (AppSort.SelectedIndex switch
        {
            1 => matches.OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase),
            2 => matches.OrderBy(a => a.Publisher, StringComparer.CurrentCultureIgnoreCase).ThenBy(a => a.Name),
            _ => matches.OrderBy(a => a.Bytes is null).ThenByDescending(a => a.Bytes).ThenBy(a => a.Name)
        }).ToList();
        InstalledAppsGrid.ItemsSource = list;
        AppsCount.Text = $"{list.Count:N0} de {maintenance.Applications.Count:N0} aplicativos · {list.Count(a => a.Bytes == null):N0} sem tamanho";
        var largest = list.Where(a => a.Bytes is > 0).OrderByDescending(a => a.Bytes).Take(3).Select(a => a.Name + " (" + a.Size + ")").ToArray();
        AppRelationship.Text = (largest.Length == 0 ? "Nenhum tamanho conhecido nesta seleção." : "Maiores tamanhos informados: " + string.Join("; ", largest) + ".") +
            " São estimativas; selecione um aplicativo para revisar programas do mesmo fabricante. Último uso indisponível.";
    }

    void WindowsRowSelected(object sender, SelectionChangedEventArgs e)
    {
        if (WindowsDiagnosticsGrid.SelectedItem is not DiagnosticRow row) return;
        DetailTitle.Text = row.Category;
        DetailLocation.Text = string.IsNullOrWhiteSpace(row.Location) ? "Sem localização em disco." : row.Location;
        DetailStatus.Text = $"{row.Size} · {row.CountText} itens · {row.Status}";
        DetailImpact.Text = row.Impact; DetailRestrictions.Text = row.Restrictions; DetailSource.Text = row.Source;
        DetailBody.Visibility = Visibility.Visible;
    }

    void ExportWindows(object sender, RoutedEventArgs e)
    {
        if (maintenance == null) { MessageBox.Show("Execute o diagnóstico antes de exportar.", "Exportar diagnóstico"); return; }
        var dialog = new SaveFileDialog { FileName = "Respiro-Windows-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json", Filter = "Relatório JSON (*.json)|*.json" };
        if (dialog.ShowDialog() != true) return;
        try { SaveMaintenanceReport(dialog.FileName, true); Status.Text = "Relatório de manutenção exportado."; }
        catch (Exception ex) { MessageBox.Show("Falha ao salvar: " + ex.Message, "Exportar diagnóstico"); }
    }
    void SaveMaintenanceReport(string path, bool preserveWorkSnapshot = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var json = JsonSerializer.Serialize(new { Schema = 3, WindowsCleanup = new { Discovery = windowsDiscovery, Preview = windowsPlan?.Items, Execution = windowsExecution }, Maintenance = maintenance, CleanupPreview = cleanupPlan?.Items, CleanupResults = cleanupOutcomes, PeakWorkingSet = System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64 }, new JsonSerializerOptions { WriteIndented = true });
        // A recoverable working copy is kept only for an explicit UI export; never automatically cleaned.
        if (preserveWorkSnapshot) try { temporaryStore.CreateDiagnosticSnapshot(maintenance!); } catch (IOException ex) { Log("Cópia de trabalho não criada", ex.Message); }
        File.WriteAllText(path, json);
    }

    // ───── Limpeza manual ─────
    sealed record TemporaryRow(string Path, string Size, string Status);
    async void RefreshOwnedTemporary(object sender, RoutedEventArgs e)
    {
        if (cancellation != null) return;
        if (SelectedWindowsCategory != null) { await DiscoverWindowsCleanup(); return; }
        if (WindowsCleanupCategorySelector.SelectedItem == null) { CleanupSummary.Text = "Escolha explicitamente uma categoria."; return; }
        InvalidateCleanup();
        try
        {
            OwnedTemporaryGrid.ItemsSource = temporaryStore.Enumerate().Take(1000).Select(p => { var f = new FileInfo(p); return new TemporaryRow(p, Sizes.Format(f.Length), "Origem e uso serão verificados na prévia."); }).ToList();
            OwnedTemporaryGrid.UnselectAll(); OwnedTemporaryGrid.SelectedIndex = -1;
            CleanupSummary.Text = "Nenhum item selecionado automaticamente. Área: " + temporaryStore.Root;
        }
        catch (Exception ex) { CleanupSummary.Text = "Consulta inconclusiva: " + ex.Message; }
        UpdateOwnedSelectionText();
    }
    void OwnedSelectionChanged(object sender, SelectionChangedEventArgs e) { if ((cleanupPlan != null || windowsPlan != null) && cancellation == null) InvalidateCleanup(); UpdateOwnedSelectionText(); }
    void UpdateOwnedSelectionText()
    {
        int listed = OwnedTemporaryGrid.Items.Count, selected = OwnedTemporaryGrid.SelectedItems.Count;
        CleanupSelectionText.Text = OwnedTemporaryGrid.ItemsSource == null ? "Nenhum arquivo listado."
            : listed == 0 ? "Área vazia: nenhum temporário próprio encontrado."
            : $"{listed:N0} listado(s) · {selected:N0} selecionado(s) manualmente";
    }
    void InvalidateCleanup()
    {
        InvalidateWindowsPlan();
        cleanupPlan = null; cleanupOutcomes = null; CleanupPreviewGrid.ItemsSource = null; CleanupResultGrid.ItemsSource = null; CleanupApproval.IsChecked = false; CleanupConfirmation.Text = "";
        UpdateCleanupState();
    }
    void CleanupInputChanged(object sender, RoutedEventArgs e) => UpdateCleanupState();
    void CleanupConfirmationChanged(object sender, TextChangedEventArgs e) => UpdateCleanupState();

    /// <summary>Explica o que falta para remover. A interface só é mais restritiva; o CleanupService revalida tudo.</summary>
    void UpdateCleanupState()
    {
        if (RemoveButton == null || CleanupRequirements == null) return;
        if (SelectedWindowsCategory != null) { UpdateWindowsCleanupState(); return; }
        if (WindowsCleanupCategorySelector.SelectedItem == null) { RemoveButton.IsEnabled = false; SimulateCleanupButton.IsEnabled = false; CleanupRequirements.Text = "Escolha uma categoria explicitamente e gere a prévia da seleção."; return; }
        bool busy = cancellation != null;
        var missing = new List<string>();
        if (busy) missing.Add("aguardar a operação em andamento");
        if (cleanupPlan == null) missing.Add("gerar a prévia da seleção");
        else if (DateTime.UtcNow - cleanupPlan.Created > TimeSpan.FromMinutes(5)) missing.Add("a prévia expirou; gere outra");
        else if (cleanupPlan.EligibleCount == 0) missing.Add("a prévia não tem itens elegíveis");
        if (CleanupApproval.IsChecked != true) missing.Add("aprovar a categoria");
        if (cleanupPlan == null) missing.Add("digitar a confirmação indicada na prévia");
        else if (CleanupConfirmation.Text != cleanupPlan.Confirmation) missing.Add($"digitar exatamente {cleanupPlan.Confirmation}");
        RemoveButton.IsEnabled = missing.Count == 0;
        CleanupRequirements.Text = missing.Count == 0 ? "Requisitos atendidos. Uma caixa de confirmação final ainda será exibida." : "Para habilitar a remoção: " + string.Join(" · ", missing) + ".";
        CleanupConfirmationLabel.Text = cleanupPlan == null ? "Confirmação" : $"Digite: {cleanupPlan.Confirmation}";
        SimulateCleanupButton.IsEnabled = !busy && cleanupPlan != null;
        SimulateCleanupButton.ToolTip = cleanupPlan == null ? "Gere a prévia da seleção primeiro." : "Executa todas as verificações sem remover nada.";
        ToolTipService.SetShowOnDisabled(SimulateCleanupButton, true);
        PrepareButton.IsEnabled = ListOwnedButton.IsEnabled = !busy;
    }

    async void PrepareOwnedCleanup(object sender, RoutedEventArgs e)
    {
        if (cancellation != null) return;
        if (SelectedWindowsCategory != null) { await PrepareWindowsCleanup(); return; }
        if (WindowsCleanupCategorySelector.SelectedItem == null) { CleanupSummary.Text = "Escolha uma categoria."; return; }
        var selected = OwnedTemporaryGrid.SelectedItems.Cast<TemporaryRow>().Select(r => r.Path).ToList();
        InvalidateCleanup();
        if (selected.Count == 0) { CleanupSummary.Text = "Selecione explicitamente os arquivos que deseja avaliar."; return; }
        cancellation = new(); CancelButton.IsEnabled = true; AnalyzeButton.IsEnabled = false; WindowsDiagnoseButton.IsEnabled = false;
        BeginOperation($"Gerando prévia de {selected.Count:N0} temporário(s) próprio(s)");
        try
        {
            cleanupService ??= new(temporaryStore);
            cleanupPlan = await Task.Run(() => cleanupService.Prepare(selected, cancellation.Token));
            CleanupPreviewGrid.ItemsSource = cleanupPlan.Items;
            CleanupSummary.Text = $"Prévia: {cleanupPlan.EligibleCount} elegíveis, {Sizes.Format(cleanupPlan.EligibleBytes)} lógicos. Para remover, aprove a categoria e digite {cleanupPlan.Confirmation}. A prévia expira em cinco minutos. Remoção permanente, sem Lixeira.";
        }
        catch (Exception ex) { CleanupSummary.Text = "Prévia inconclusiva: " + ex.Message; }
        finally { cancellation.Dispose(); cancellation = null; CancelButton.IsEnabled = false; AnalyzeButton.IsEnabled = true; WindowsDiagnoseButton.IsEnabled = true; EndOperation(); }
    }
    async void SimulateOwnedCleanup(object sender, RoutedEventArgs e) => await RunOwnedCleanup(true);
    async void RemoveOwnedCleanup(object sender, RoutedEventArgs e) => await RunOwnedCleanup(false);
    async Task RunOwnedCleanup(bool simulate)
    {
        if (SelectedWindowsCategory != null) { await RunWindowsCleanup(simulate); return; }
        if (WindowsCleanupCategorySelector.SelectedItem == null) { CleanupSummary.Text = "Nenhuma categoria aprovada."; return; }
        if (cancellation != null || cleanupPlan == null || cleanupService == null) { CleanupSummary.Text = "Gere uma prévia válida da seleção antes de continuar."; return; }
        var plan = cleanupPlan; bool approved = CleanupApproval.IsChecked == true; string confirmation = CleanupConfirmation.Text;
        if (!simulate)
        {
            if (!approved || confirmation != plan.Confirmation || plan.EligibleCount == 0) { CleanupSummary.Text = "Aprove a categoria e digite a confirmação exata mostrada na prévia."; return; }
            if (MessageBox.Show($"Remover permanentemente {plan.EligibleCount} temporários próprios do Respiro ({Sizes.Format(plan.EligibleBytes)})?\n\nCada arquivo será revalidado. Falhas serão preservadas e registradas. Esta operação não usa a Lixeira.", "Confirmar remoção manual", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        }
        cancellation = new(); CancelButton.IsEnabled = true; AnalyzeButton.IsEnabled = false; WindowsDiagnoseButton.IsEnabled = false;
        BeginOperation(simulate ? "Simulando a limpeza — nada será removido" : "Removendo temporários próprios aprovados");
        try
        {
            cleanupOutcomes = await Task.Run(() => cleanupService.Run(plan, simulate, approved, confirmation, cancellation.Token));
            CleanupResultGrid.ItemsSource = cleanupOutcomes;
            CleanupSummary.Text = $"{(simulate ? "Simulação" : "Operação manual")} concluída. Removidos: {Sizes.Format(cleanupOutcomes.Sum(o => o.RemovedBytes))}. Consulte os resultados por arquivo e o registro cleanup.jsonl.";
            if (!simulate) { cleanupPlan = null; CleanupApproval.IsChecked = false; CleanupConfirmation.Text = ""; }
        }
        catch (Exception ex) { CleanupSummary.Text = "Operação bloqueada: " + ex.Message; }
        finally { cancellation.Dispose(); cancellation = null; CancelButton.IsEnabled = false; AnalyzeButton.IsEnabled = true; WindowsDiagnoseButton.IsEnabled = true; EndOperation(); UpdateDisk(); }
    }
}


