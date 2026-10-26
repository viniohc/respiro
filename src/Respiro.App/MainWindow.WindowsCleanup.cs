using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Respiro.Core;
namespace Respiro.App;
public partial class MainWindow
{
    WindowsCleanupService? windowsCleanup;
    WindowsCleanupPlan? windowsPlan;
    WindowsCleanupDiscovery? windowsDiscovery;
    WindowsCleanupResult? windowsExecution;
    WindowsCleanupCategory? SelectedWindowsCategory => WindowsCleanupCategorySelector.SelectedItem as WindowsCleanupCategory;
    void InitializeWindowsCleanup()
    {
        WindowsCleanupCategorySelector.ItemsSource = new object[] { "Temporários próprios do Respiro" }.Concat(WindowsCleanupCatalog.Categories.Where(c => c.Kind != WindowsCleanupKind.NpmCache).Cast<object>()).Concat(WindowsCleanupCatalog.Categories.Where(c => c.Kind == WindowsCleanupKind.NpmCache).Cast<object>()).ToArray();
        WindowsCleanupCategorySelector.SelectedIndex = -1;
    }
    void WindowsCleanupCategoryChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OwnedTemporaryGrid == null) return;
        if (cancellation != null) { windowsPlan = null; cleanupPlan = null; CleanupApproval.IsChecked = false; CleanupConfirmation.Text = ""; UpdateCleanupState(); return; }
        windowsPlan = null; windowsDiscovery = null; windowsExecution = null; InvalidateCleanup();
        OwnedTemporaryGrid.ItemsSource = null;
        var category = SelectedWindowsCategory;
        CleanupApproval.Content = category == null ? "Aprovo a remoção desta categoria de temporários próprios do Respiro" : "Aprovo somente os itens elegíveis de " + category.Name;
        CleanupAreaText.Text = category == null ? "Área própria: " + temporaryStore.Root : category.Source + Environment.NewLine + category.Restriction;
        CleanupSummary.Text = category == null ? "Escolha explicitamente uma categoria e liste seus arquivos. Nenhuma categoria ou arquivo é selecionado automaticamente." : category.Name + ": " + category.Restriction + " Risco: " + category.Risk;
        UpdateCleanupState();
    }
    async Task DiscoverWindowsCleanup()
    {
        var category = SelectedWindowsCategory; if (category == null || cancellation != null) return;
        windowsPlan = null; windowsExecution = null; InvalidateCleanup();
        cancellation = new(); CancelButton.IsEnabled = true; AnalyzeButton.IsEnabled = false; WindowsDiagnoseButton.IsEnabled = false; WindowsCleanupCategorySelector.IsEnabled = false; NpmPublicProbeApproval.IsEnabled = false;
        BeginOperation("Listando " + category.Name + " — nenhuma remoção");
        try
        {
            windowsCleanup ??= new(NpmPublicProbeApproval.IsChecked==true);
            windowsDiscovery = await Task.Run(() => windowsCleanup.Discover(category.Kind, new Progress<string>(s => Dispatcher.InvokeAsync(() => OperationPath.Text = s)), cancellation.Token));
            OwnedTemporaryGrid.ItemsSource = windowsDiscovery.Files; OwnedTemporaryGrid.UnselectAll(); OwnedTemporaryGrid.SelectedIndex = -1;
            CleanupSummary.Text = $"{category.Name}: {windowsDiscovery.SeenFiles:N0} arquivos observados, {Sizes.Format(windowsDiscovery.SeenBytes)} lógicos; {windowsDiscovery.Issues:N0} ocorrências. Total observado não é espaço aprovado. {category.Restriction}";
            if (windowsDiscovery.Cancelled) CleanupSummary.Text += " Listagem cancelada e parcial.";
            CleanupAreaText.Text = category.Source + Environment.NewLine + category.Risk + Environment.NewLine + string.Join(Environment.NewLine, windowsDiscovery.Notices.Take(5));
        }
        catch (Exception ex) { CleanupSummary.Text = "Listagem inconclusiva: " + ex.Message; }
        finally { cancellation.Dispose(); cancellation = null; CancelButton.IsEnabled = false; AnalyzeButton.IsEnabled = true; WindowsDiagnoseButton.IsEnabled = true; WindowsCleanupCategorySelector.IsEnabled = true; NpmPublicProbeApproval.IsEnabled = true; EndOperation(); Status.Text = CleanupSummary.Text; UpdateOwnedSelectionText(); }
    }
    void NpmPublicProbeChanged(object sender,RoutedEventArgs e)
    {
        if(initializing||cancellation!=null)return;
        InvalidateWindowsPlan();windowsCleanup=null;CleanupApproval.IsChecked=false;CleanupConfirmation.Text="";CleanupPreviewGrid.ItemsSource=null;CleanupResultGrid.ItemsSource=null;UpdateCleanupState();
    }
    void InvalidateWindowsPlan() { windowsPlan = null; windowsExecution = null; }
    void UpdateWindowsCleanupState()
    {
        if (RemoveButton == null) return;
        var missing = new List<string>();
        if (cancellation != null) missing.Add("aguardar a operação");
        if (windowsPlan == null) missing.Add("gerar a prévia da seleção");
        else if (DateTime.UtcNow - windowsPlan.Created > TimeSpan.FromMinutes(5)) missing.Add("gerar nova prévia: a anterior expirou");
        else if (SelectedWindowsCategory?.Kind != windowsPlan.Category.Kind) missing.Add("categoria mudou; gere nova prévia");
        else if (windowsPlan.EligibleCount == 0) missing.Add("nenhum item elegível nesta prévia");
        if (CleanupApproval.IsChecked != true) missing.Add("aprovar esta categoria");
        if (windowsPlan == null || CleanupConfirmation.Text != windowsPlan.Confirmation) missing.Add(windowsPlan == null ? "digitar a confirmação indicada" : "digitar exatamente " + windowsPlan.Confirmation);
        RemoveButton.IsEnabled = missing.Count == 0;
        CleanupRequirements.Text = missing.Count == 0 ? "Confirmação final ainda obrigatória; itens da Lixeira exigem confirmação individual." : string.Join(" · ", missing);
        CleanupConfirmationLabel.Text = windowsPlan == null ? "Confirmação" : "Digite: " + windowsPlan.Confirmation;
        SimulateCleanupButton.IsEnabled = cancellation == null && windowsPlan != null;
        PrepareButton.IsEnabled = ListOwnedButton.IsEnabled = cancellation == null;
    }
    async Task PrepareWindowsCleanup()
    {
        if (cancellation != null || SelectedWindowsCategory is not { } category) return;
        var selected = OwnedTemporaryGrid.SelectedItems.OfType<WindowsCleanupFile>().Select(f => f.Path).ToArray();
        windowsPlan = null; InvalidateCleanup();
        if (selected.Length == 0) { CleanupSummary.Text = "Selecione explicitamente arquivos da categoria antes de gerar a prévia."; return; }
        cancellation = new(); CancelButton.IsEnabled = true; AnalyzeButton.IsEnabled = false; WindowsDiagnoseButton.IsEnabled = false; WindowsCleanupCategorySelector.IsEnabled = false; NpmPublicProbeApproval.IsEnabled = false;
        BeginOperation("Revalidando origem, conteúdo, alocação e uso de " + selected.Length + " arquivos");
        try
        {
            windowsCleanup = new(NpmPublicProbeApproval.IsChecked==true);
            var progress=new Progress<string>(s=>OperationPath.Text=s);
            windowsPlan = await Task.Run(() => windowsCleanup.Prepare(category.Kind, selected, cancellation.Token,progress));
            CleanupPreviewGrid.ItemsSource = windowsPlan.Items;
            CleanupSummary.Text = $"Prévia de {category.Name}: {windowsPlan.EligibleCount} elegíveis; {Sizes.Format(windowsPlan.EstimatedRecoverable)} potencialmente recuperáveis (alocação). Digite {windowsPlan.Confirmation} após revisar origem, riscos, permissões e motivos. Validade: cinco minutos.";
        }
        catch (Exception ex) { CleanupSummary.Text = "Prévia inconclusiva: " + ex.Message; }
        finally { cancellation.Dispose(); cancellation = null; CancelButton.IsEnabled = false; AnalyzeButton.IsEnabled = true; WindowsDiagnoseButton.IsEnabled = true; WindowsCleanupCategorySelector.IsEnabled = true; NpmPublicProbeApproval.IsEnabled = true; EndOperation(); Status.Text = CleanupSummary.Text; }
    }
    async Task RunWindowsCleanup(bool simulate)
    {
        if (cancellation != null || windowsPlan == null || windowsCleanup == null) { CleanupSummary.Text = "Gere uma prévia válida da seleção."; return; }
        if (SelectedWindowsCategory?.Kind != windowsPlan.Category.Kind) { CleanupSummary.Text = "Categoria mudou; gere nova prévia."; return; }
        var plan = windowsPlan; bool approved = CleanupApproval.IsChecked == true; string confirmation = CleanupConfirmation.Text;
        var individual = new HashSet<string>(StringComparer.Ordinal);
        if (!simulate)
        {
            if (!approved || confirmation != plan.Confirmation || plan.EligibleCount == 0) { CleanupSummary.Text = "Aprovação da categoria e confirmação exata obrigatórias."; return; }
            if (MessageBox.Show($"Remover permanentemente {plan.EligibleCount} arquivos de {plan.Category.Name}?\nPotencial estimado: {Sizes.Format(plan.EstimatedRecoverable)}.\n\n{plan.Category.Risk}\n\nArquivos serão revalidados individualmente. Sem Lixeira e sem alteração de permissões.", "Confirmar limpeza da categoria", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            if (plan.Category.Kind == WindowsCleanupKind.RecycleBin)
                foreach (var item in plan.Items.Where(i => i.Eligible))
                {
                    if (MessageBox.Show($"Excluir definitivamente este item da Lixeira?\n\nOrigem original: {item.OriginalPath}\nArquivo: {item.Path}\nOcupado: {item.OccupiedText}\n\nEle não poderá mais ser restaurado pela Lixeira.", "Confirmação individual da Lixeira", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) { CleanupSummary.Text = "Não executada: confirmação individual recusada; nenhum item deste lote foi removido."; return; }
                    individual.Add(item.Path);
                }
        }
        cancellation = new(); CancelButton.IsEnabled = true; AnalyzeButton.IsEnabled = false; WindowsDiagnoseButton.IsEnabled = false; WindowsCleanupCategorySelector.IsEnabled = false; NpmPublicProbeApproval.IsEnabled = false;
        BeginOperation(simulate ? "Simulando verificações — nenhuma remoção" : "Limpando a seleção aprovada de " + plan.Category.Name);
        try
        {
            windowsExecution = await Task.Run(() => windowsCleanup.Run(plan, simulate, approved, confirmation, cancellation.Token, individual));
            CleanupResultGrid.ItemsSource = windowsExecution.Items;
            CleanupSummary.Text = simulate ? $"{windowsExecution.State}. Nenhum arquivo removido. Potencial da prévia: {Sizes.Format(plan.EstimatedRecoverable)}; consulte a revalidação de cada item." : $"{windowsExecution.State}. Alocação estimada dos itens removidos: {Sizes.Format(windowsExecution.EstimatedReleasedBytes)}. Variação medida: {windowsExecution.FreeDeltaText}. Consulte os resultados e windows-cleanup.jsonl.";
            if (!simulate) { windowsPlan = null; CleanupApproval.IsChecked = false; CleanupConfirmation.Text = ""; InvalidateQuickResults(); }
        }
        catch (Exception ex) { CleanupSummary.Text = "Não executada: " + ex.Message; }
        finally { cancellation.Dispose(); cancellation = null; CancelButton.IsEnabled = false; AnalyzeButton.IsEnabled = true; WindowsDiagnoseButton.IsEnabled = true; WindowsCleanupCategorySelector.IsEnabled = true; NpmPublicProbeApproval.IsEnabled = true; EndOperation(); Status.Text = CleanupSummary.Text; UpdateDisk(); }
    }
    async Task SimulateWindowsAdapters(string reportPath)
    {
        cancellation = new(); CancelButton.IsEnabled = true; AnalyzeButton.IsEnabled = false; WindowsDiagnoseButton.IsEnabled = false;
        BeginOperation("Validando adaptadores em simulação no Windows real — sem limpeza");
        var reports = new List<object>(); var unique = new Dictionary<string, long>(StringComparer.Ordinal);
        try
        {
            foreach (var category in WindowsCleanupCatalog.Categories)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var service = new WindowsCleanupService();
                var discovery = await Task.Run(() => service.Discover(category.Kind, new Progress<string>(s => Dispatcher.InvokeAsync(() => OperationPath.Text = s)), cancellation.Token));
                var candidates = discovery.Files.OrderByDescending(f => f.Bytes).Take(1000).Select(f => f.Path).ToArray();
                var plan = await Task.Run(() => service.Prepare(category.Kind, candidates, cancellation.Token));
                var execution = await Task.Run(() => service.Run(plan, true, token: cancellation.Token));
                var verified = execution.Items.Where(i => i.Status == "Simulado").Select(i => i.Path).ToHashSet(StringComparer.Ordinal);
                foreach (var item in plan.Items.Where(i => i.Eligible && verified.Contains(i.Path) && Path.GetPathRoot(i.Path)!.TrimEnd(Path.DirectorySeparatorChar).Equals("C:", StringComparison.OrdinalIgnoreCase))) unique[item.Path] = item.EstimatedRecoverable;
                reports.Add(new { Category = category, Discovery = discovery, Preview = plan.Items, EligibleFiles = plan.EligibleCount, Potential = plan.EstimatedRecoverable, Simulation = execution });
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new { At = DateTime.Now, Mode = "SIMULATION_ONLY", RealDeletionExecuted = false, FreeBytesC = new DriveInfo("C").AvailableFreeSpace, PotentialRecoverableC = unique.Values.Sum(), EligibleFilesC = unique.Count, Categories = reports, PeakWorkingSet = System.Diagnostics.Process.GetCurrentProcess().PeakWorkingSet64 }, new JsonSerializerOptions { WriteIndented = true }));
            CleanupSummary.Text = $"Simulação concluída: {unique.Count} arquivos verificados no C:, {Sizes.Format(unique.Values.Sum())} potencial. Nenhuma categoria selecionada ou autorizada na interface.";
        }
        finally { cancellation.Dispose(); cancellation = null; CancelButton.IsEnabled = false; AnalyzeButton.IsEnabled = true; WindowsDiagnoseButton.IsEnabled = true; EndOperation(); Status.Text = CleanupSummary.Text; }
    }
}



