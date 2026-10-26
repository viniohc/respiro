using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Respiro.Core;
namespace Respiro.App;
public partial class MainWindow
{
    async void CompareDuplicateContents(object sender, RoutedEventArgs e)
    {
        if (cancellation != null || result == null || ViewFilter.SelectedIndex == 3) return;
        var files = FilesGrid.Items.OfType<FileRow>().Take(1500).ToArray();
        if (files.Length < 2) { Status.Text = "Exiba ao menos dois arquivos para comparar."; return; }
        if (MessageBox.Show("Ler o conteúdo dos arquivos desta lista para procurar cópias iguais?\n\nAté 1.500 arquivos, 8 GiB por arquivo e 16 GiB de leitura no total. Pode demorar e pode ser cancelado. Arquivos sem outro do mesmo tamanho não serão lidos.\n\nNada será excluído ou autorizado para limpeza.",
            "Comparar conteúdo — opcional", MessageBoxButton.YesNo, MessageBoxImage.Information, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        cancellation = new();
        AnalyzeButton.IsEnabled = WindowsDiagnoseButton.IsEnabled = QuickAnalyzeButton.IsEnabled = DuplicateCompareButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        BeginOperation("Comparando arquivos por conteúdo — sem remoção");
        try
        {
            var progress = new Progress<DuplicateProgress>(p =>
            {
                OperationPath.Text = p.Path;
                OperationDetail.Text = $"{p.ProcessedFiles:N0}/{p.TotalFiles:N0} arquivos · {Sizes.Format(p.ReadBytes)} lidos";
            });
            var report = await Task.Run(() => new DuplicateContentService().Compare(files, progress: progress, token: cancellation.Token));
            DuplicateResultsGrid.ItemsSource = report.Files;
            DuplicateSummary.Text = report.Summary + $" Comparação de {report.ComparedAt.ToLocalTime():g}; limitada à lista exibida, não ao computador inteiro.";
            DuplicateResultsTab.IsSelected = true;
            Status.Text = "Comparação disponível. Arquivos pessoais continuam protegidos; revise cada caminho no Explorador.";
        }
        catch (Exception ex) { DuplicateSummary.Text = "Comparação inconclusiva: " + ex.Message; DuplicateResultsGrid.ItemsSource = null; }
        finally
        {
            cancellation.Dispose(); cancellation = null;
            AnalyzeButton.IsEnabled = WindowsDiagnoseButton.IsEnabled = QuickAnalyzeButton.IsEnabled = DuplicateCompareButton.IsEnabled = true;
            CancelButton.IsEnabled = false; EndOperation();
        }
    }
    void RevealDuplicateFile(object sender, RoutedEventArgs e)
    {
        if (cancellation != null || DuplicateResultsGrid.SelectedItem is not DuplicateFileResult file) return;
        if (!System.IO.File.Exists(file.Path)) { Status.Text = "Arquivo indisponível. Compare novamente."; return; }
        var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        start.ArgumentList.Add("/select," + file.Path);
        try { Process.Start(start); Status.Text = "Arquivo localizado; nenhuma cópia removida. Se excluir manualmente, confirme a possibilidade de recuperação pela Lixeira."; }
        catch (Exception ex) { Status.Text = "Não foi possível localizar: " + ex.Message; }
    }
}
