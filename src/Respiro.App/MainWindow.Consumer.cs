using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Respiro.Core;
namespace Respiro.App;

public partial class MainWindow
{
    sealed class QuickRow(QuickCleanupOption option)
    {
        public QuickCleanupOption Option { get; } = option;
        public string Name => Option.Name;
        public string Explanation => Option.Explanation;
        public string Consequences => Option.Consequences;
        public string Route => Option.Route;
        public QuickCleanupAnalysis? Analysis { get; set; }
        public string Amount => Analysis?.Amount ?? "Desconhecido — não contabilizado";
        public string Occupied => Analysis == null ? "Desconhecido" : Sizes.Format(Analysis.Discovery.KnownOccupied) + " conhecidos; " + (Analysis.Discovery.Truncated || Analysis.Discovery.Issues > 0 ? "medição parcial" : "escopo observado");
        public string Risk => Option.Kind == WindowsCleanupKind.Thumbnails ? "Baixo — exige recriação" : Option.Kind == WindowsCleanupKind.RecycleBin ? "Alto — permanente" : "Requer revisão";
        public string Permission => Option.Kind == WindowsCleanupKind.WindowsTemporary ? "Administrador já elevado" : Option.Kind is null || Option.Kind == WindowsCleanupKind.DirectXShaders ? "Definida pelo Windows" : "Usuário atual; áreas do sistema podem ser negadas";
        public string State { get; set; } = "Aguardando análise";
    }
    List<QuickRow> quickRows = [];
    void InitializeConsumerCleanup()
    {
        quickRows = QuickCleanupCatalog.Options.Select(o => new QuickRow(o)).ToList();
        QuickCleanupGrid.ItemsSource = quickRows;
    }
    void InvalidateQuickResults()
    {
        foreach (var row in quickRows) { row.Analysis = null; row.State = "Analise novamente"; }
        QuickCleanupGrid.Items.Refresh();
        HomeQuickSummary.Text = QuickCleanupSummary.Text = "Houve uma operação de limpeza. Analise novamente para atualizar o potencial; os valores anteriores foram descartados.";
        QuickReviewButton.IsEnabled = false;
    }
    void GoQuickCleanup(object sender, RoutedEventArgs e) => Navigate(Page.Cleanup);
    async void AnalyzeQuickCleanup(object sender, RoutedEventArgs e)
    {
        if (cancellation != null) return;
        InvalidateCleanup();
        foreach (var row in quickRows) { row.Analysis = null; row.State = "Aguardando análise"; }
        HomeQuickSummary.Text = QuickCleanupSummary.Text = "Analisando — nenhum arquivo foi selecionado ou autorizado.";
        QuickCleanupGrid.Items.Refresh();
        cancellation = new();
        QuickAnalyzeButton.IsEnabled = QuickCleanupGrid.IsEnabled = false;
        AnalyzeButton.IsEnabled = WindowsDiagnoseButton.IsEnabled = WindowsCleanupCategorySelector.IsEnabled = false;
        CancelButton.IsEnabled = true;
        BeginOperation("Analisando limpeza rápida — nenhuma remoção");
        try
        {
            foreach (var row in quickRows)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                if (row.Option.Kind is not { } kind || !WindowsCleanupCatalog.Categories.Single(c => c.Kind == kind).SupportsRemoval)
                { row.State = "Revisão no Windows — tamanho desconhecido"; continue; }
                row.State = "Verificando origem e uso"; QuickCleanupGrid.Items.Refresh();
                try
                {
                    var progress = new Progress<string>(s => OperationPath.Text = s);
                    row.Analysis = await Task.Run(() => new QuickCleanupAnalyzer().Analyze(kind, progress, cancellation.Token));
                    row.State = row.Analysis.State;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) { row.State = "Inconclusivo: " + ex.Message; }
                QuickCleanupGrid.Items.Refresh();
            }
            UpdateQuickSummary("Análise concluída");
        }
        catch (OperationCanceledException) { UpdateQuickSummary("Análise cancelada e parcial"); }
        finally
        {
            cancellation.Dispose(); cancellation = null;
            QuickAnalyzeButton.IsEnabled = QuickCleanupGrid.IsEnabled = true;
            AnalyzeButton.IsEnabled = WindowsDiagnoseButton.IsEnabled = WindowsCleanupCategorySelector.IsEnabled = true;
            CancelButton.IsEnabled = false;
            QuickCleanupGrid.Items.Refresh(); EndOperation(); UpdateDisk(); QuickCategorySelected(QuickCleanupGrid, null!);
        }
    }
    void UpdateQuickSummary(string state)
    {
        long total = QuickCleanupAnalyzer.Total(quickRows.Where(r => r.Analysis != null).Select(r => r.Analysis!));
        HomeQuickSummary.Text = QuickCleanupSummary.Text = state + $" às {DateTime.Now:HH:mm}: " + Sizes.Format(total) +
            " potencial estimado dos arquivos revalidados em simulação, sem somar o mesmo caminho duas vezes. " +
            "Categorias inconclusivas e encaminhadas ao Windows não entram no total. Nada autorizado.";
    }
    void QuickCategorySelected(object sender, SelectionChangedEventArgs e)
    {
        if (QuickCategoryDetail == null || QuickCleanupGrid.SelectedItem is not QuickRow row) return;
        QuickCategoryDetail.Text = row.Explanation + "\nRisco: " + row.Risk + "\nPermissão: " + row.Permission + "\nConsequências: " + row.Consequences + "\n" +
            (row.Analysis is { } a ? $"{a.EvaluatedFiles:N0} arquivos avaliados; {a.Verified.Count:N0} revalidados. " +
            "Até 1.000 arquivos por categoria; partes não verificadas não são estimadas.\n" +
            string.Join("\n", a.Discovery.Notices.Take(2)) : "O tamanho ainda é desconhecido.");
        QuickReviewButton.IsEnabled = cancellation == null && row.Analysis != null;
        QuickWindowsButton.IsEnabled = cancellation == null;
    }
    void ReviewQuickCategory(object sender, RoutedEventArgs e)
    {
        if (cancellation != null || QuickCleanupGrid.SelectedItem is not QuickRow { Analysis: { } analysis } row) return;
        WindowsCleanupCategorySelector.SelectedItem = WindowsCleanupCatalog.Categories.Single(c => c.Kind == row.Option.Kind);
        // Always discard any previous approval, even when the category did not change.
        InvalidateCleanup();
        OwnedTemporaryGrid.ItemsSource = analysis.Discovery.Files;
        OwnedTemporaryGrid.UnselectAll();
        CleanupSummary.Text = "Selecione os arquivos abaixo e clique em Gerar prévia. Nenhum arquivo foi selecionado pela análise rápida. " + row.Consequences;
        UpdateOwnedSelectionText(); UpdateCleanupState();
        OwnedTemporaryGrid.BringIntoView();
    }
    void QuickOpenWindows(object sender, RoutedEventArgs e)
    {
        if (cancellation != null || QuickCleanupGrid.SelectedItem is not QuickRow row) return;
        ShowOfficialChoice(row.Option.OfficialId);
    }
    void ShowOfficialChoice(string id)
    {
        Navigate(Page.Maintenance);
        OfficialOptionsGrid.SelectedItem = OfficialMaintenanceCatalog.Resolve(id);
        OfficialToolsTab.IsSelected = true;
        OfficialOptionsGrid.BringIntoView();
        Status.Text = "Leia as consequências e use Abrir página oficial. Nenhuma alteração foi executada.";
    }
    void OpenAppsSettings(object sender, RoutedEventArgs e)
    { if (cancellation == null) ShowOfficialChoice("apps"); }
    void AppSortChanged(object sender, SelectionChangedEventArgs e)
    { if (!initializing) ApplyAppFilter(); }

    void AppTypeChanged(object sender, SelectionChangedEventArgs e)
    { if (!initializing) ApplyAppFilter(); }
    void AppRelationshipSelected(object sender, SelectionChangedEventArgs e)
    {
        if (maintenance != null && InstalledAppsGrid.SelectedItem is InstalledApp selected)
            AppRelationship.Text = selected.Name + ": " + ApplicationInventoryView.Related(selected, maintenance.Applications) + "\n" + ApplicationUninstallService.Availability(selected);
        UpdateUninstallSelection();
    }

    static string? DownloadsFolder()
    {
        // Known Folder API respects relocated Downloads; never assume a fixed user path.
        var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        IntPtr path = IntPtr.Zero;
        try { return KnownFolder(ref id, 0, IntPtr.Zero, out path) == 0 ? System.Runtime.InteropServices.Marshal.PtrToStringUni(path) : null; }
        finally { if (path != IntPtr.Zero) System.Runtime.InteropServices.Marshal.FreeCoTaskMem(path); }
    }
    [System.Runtime.InteropServices.DllImport("shell32.dll", EntryPoint = "SHGetKnownFolderPath")]
    static extern int KnownFolder(ref Guid id, uint flags, IntPtr token, out IntPtr path);
    void PersonalFilterChanged(object sender, SelectionChangedEventArgs e)
    { if (!initializing) ApplyFilter(); }
    void PreviewPersonalFile(object sender, RoutedEventArgs e)
    {
        if (FilesGrid.SelectedItem is not FileRow file) { Status.Text = "Selecione um arquivo para ver os detalhes."; return; }
        MessageBox.Show($"{file.Path}\n\nTamanho: {file.Size}\nOcupado conhecido: {file.OccupiedText}\nÚltima alteração: {file.Modified:g}\n\n{file.Reason}\n\nNome/tamanho iguais não provam duplicação. Data de alteração não informa último uso. EXE também pode ser um programa, não um instalador. Nenhuma exclusão foi autorizada.", "Detalhes do arquivo", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    void RevealPersonalFile(object sender, RoutedEventArgs e)
    {
        if (cancellation != null || FilesGrid.SelectedItem is not FileRow file) return;
        if (!File.Exists(file.Path)) { Status.Text = "O arquivo mudou ou não está acessível. Analise novamente."; return; }
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        start.ArgumentList.Add("/select," + file.Path);
        try { Process.Start(start); Status.Text = "Arquivo localizado no Explorador. Se decidir excluí-lo, prefira a Lixeira; rede, mídia removível, tamanho e configurações podem impedir recuperação."; }
        catch (Exception ex) { Status.Text = "Não foi possível abrir o Explorador: " + ex.Message; }
    }
}
