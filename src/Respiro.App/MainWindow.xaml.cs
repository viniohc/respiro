using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using Respiro.Core;
using Microsoft.Win32;
namespace Respiro.App;

/// <summary>Análise de armazenamento, filtros, simulação e exportação. Regras de segurança vivem no Core.</summary>
public partial class MainWindow : Window
{
    ScanResult? result; PreviewResult? preview; CancellationTokenSource? cancellation; string? selectedFolder; bool initializing = true;
    readonly WindowsFileSystem fs = new();

    public MainWindow()
    {
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("pt-BR"); System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.CurrentCulture;
        InitializeComponent();
        LoadDrives();
        Disks.SelectedIndex = 0; ViewFilter.SelectedIndex = 0; CategoryFilter.ItemsSource = new[] { "Todas as categorias" }; CategoryFilter.SelectedIndex = 0;
        InitShell(); InitDockerDiagnostics(); InitOfficialMaintenance();
        initializing = false; UpdateDisk();
        Closing += (_, e) => { if (cancellation != null) { cancellation.Cancel(); e.Cancel = true; Status.Text = "Cancelando a operação. Feche a janela quando ela terminar."; } };
        Loaded += async (_, _) => await RunCommandLine();
    }

    // Argumentos de validação preservados: --scan, --report, --maintenance, --maintenance-report, --capture, --exit.
    async Task RunCommandLine()
    {
        var args = Environment.GetCommandLineArgs();
        var optimizationIndex = Array.IndexOf(args, "--optimization-report");
        if (optimizationIndex >= 0 && optimizationIndex + 1 < args.Length) await OptimizationDiagnostic(args[optimizationIndex + 1]);
        var index = Array.IndexOf(args, "--scan");
        if (index >= 0 && index + 1 < args.Length) { RootPath.Text = args[index + 1]; Navigate(Page.Analyze); await StartScan(); }
        if (args.Contains("--capture") && result != null)
        {
            preview = await Task.Run(() => new PreviewService(fs).Evaluate(result.LargestFiles.Take(3)));
            ShowPreview(preview);
        }
        var reportIndex = Array.IndexOf(args, "--report");
        if (reportIndex >= 0 && reportIndex + 1 < args.Length && result != null)
        {
            var report = Path.GetFullPath(args[reportIndex + 1]); Directory.CreateDirectory(Path.GetDirectoryName(report)!);
            File.WriteAllText(report, JsonSerializer.Serialize(new { Scan = result, Preview = preview, PeakWorkingSet = Process.GetCurrentProcess().PeakWorkingSet64 }, new JsonSerializerOptions { WriteIndented = true }));
        }
        var recoveryIndex=Array.IndexOf(args,"--investigate-storage");
        var priorityIndex=Array.IndexOf(args,"--investigate-priority");
        if(recoveryIndex>=0&&recoveryIndex+1<args.Length){await StartRecovery(true);SaveRecovery(args[recoveryIndex+1]);}
        if(priorityIndex>=0&&priorityIndex+1<args.Length){await StartRecovery(false);SaveRecovery(args[priorityIndex+1]);}
        var recoveryView=Array.IndexOf(args,"--view-recovery-report");
        if(recoveryView>=0&&recoveryView+1<args.Length)LoadRecoveryForReview(args[recoveryView+1]);
        var dockerIndex=Array.IndexOf(args,"--docker-diagnostic");
        if(dockerIndex>=0&&dockerIndex+1<args.Length){await StartDockerDiagnostic();if(dockerDiagnostic!=null)DockerDeepDiagnostic.Save(dockerDiagnostic,args[dockerIndex+1]);}
        var dockerView=Array.IndexOf(args,"--view-docker-report");
        if(dockerView>=0&&dockerView+1<args.Length)LoadDockerReport(args[dockerView+1]);
        var windowsSimulation = Array.IndexOf(args, "--windows-cleanup-simulate");
        if (windowsSimulation >= 0 && windowsSimulation + 1 < args.Length) await SimulateWindowsAdapters(args[windowsSimulation + 1]);
        if (args.Contains("--maintenance")) await StartMaintenance();
        var maintenanceIndex = Array.IndexOf(args, "--maintenance-report");
        if (maintenanceIndex >= 0 && maintenanceIndex + 1 < args.Length && maintenance != null) SaveMaintenanceReport(args[maintenanceIndex + 1]);
        if (args.Contains("--capture")) await CaptureAll(args.Contains("--maintenance"));
        if (args.Contains("--exit")) Close();
    }

    void DiskChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RootPath == null) return;
        RootPath.Text = Disks.SelectedItem?.ToString() ?? "C:\\";
        SyncDriveList(); UpdateDisk();
    }

    void UpdateDisk()
    {
        try
        {
            var d = new DriveInfo(Path.GetPathRoot(RootPath.Text)!);
            long used = d.TotalSize - d.TotalFreeSpace;
            DiskSummary.Text = $"{d.Name.TrimEnd('\\')}  ·  Total {Sizes.Format(d.TotalSize)}  ·  Usado {Sizes.Format(used)}  ·  Livre {Sizes.Format(d.AvailableFreeSpace)}";
            DiskBar.Value = 100.0 * used / d.TotalSize;
            UpdateHomeDisk(d);
        }
        catch { DiskSummary.Text = "Informações do volume indisponíveis."; DiskBar.Value = 0; UpdateHomeDisk(null); }
    }

    void ChooseFolder(object sender, RoutedEventArgs e) => PickFolder();
    bool PickFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Selecione a pasta para analisar" };
        if (dialog.ShowDialog() != true) return false;
        RootPath.Text = dialog.FolderName; UpdateDisk(); return true;
    }

    async void Analyze(object sender, RoutedEventArgs e) => await StartScan();

    async Task StartScan()
    {
        if (cancellation != null) return; var root = RootPath.Text.Trim();
        if (!Directory.Exists(root)) { MessageBox.Show("Selecione um diretório existente e acessível.", "Diretório indisponível"); return; }
        DuplicateResultsGrid.ItemsSource = null; DuplicateSummary.Text = "Nova análise: compare a lista novamente.";
        result = null; preview = null; selectedFolder = null; FolderTree.Items.Clear(); FilesGrid.ItemsSource = null; FoldersGrid.ItemsSource = null; CategoryChart.Children.Clear(); ExtensionsGrid.ItemsSource = null; IssuesGrid.ItemsSource = null; FindingsGrid.ItemsSource = null; PreviewGrid.ItemsSource = null;
        ResetPreview("Nenhuma seleção simulada nesta análise."); ShowScanResult();
        cancellation = new(); AnalyzeButton.IsEnabled = false; CancelButton.IsEnabled = true; Disks.IsEnabled = false; RootPath.IsEnabled = false;
        // Estimativa só quando a raiz é o volume inteiro: bytes lidos ÷ espaço usado. Nunca chega a 100% antes de terminar.
        long estimate = 0;
        try { var d = new DriveInfo(Path.GetPathRoot(root)!); if (string.Equals(Path.GetFullPath(root).TrimEnd('\\'), d.RootDirectory.FullName.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) estimate = d.TotalSize - d.TotalFreeSpace; } catch { }
        BeginOperation("Analisando " + root);
        ScanSummary.Text = "Análise em andamento. Os contadores abaixo são reais; Cancelar preserva o resultado parcial.";
        Log("Análise iniciada", root);
        var progress = new Progress<ScanProgress>(p =>
        {
            OperationDetail.Text = $"{p.Files:N0} arquivos · {p.Folders:N0} pastas · {Sizes.Format(p.Bytes)} · {Elapsed()}";
            OperationPath.Text = p.Current;
            if (estimate > 0) { double pct = Math.Min(99, 100.0 * p.Bytes / estimate); ScanBar.IsIndeterminate = false; ScanBar.Value = pct; OperationPercent.Text = $"≈ {pct:N0}% do espaço usado do volume"; }
            else OperationPercent.Text = "Total da pasta desconhecido até concluir";
        });
        try
        {
            result = await Task.Run(() => new Scanner(fs).Scan(root, progress, cancellation.Token));
            CategoryFilter.ItemsSource = new[] { "Todas as categorias" }.Concat(result.Categories.Select(c => c.Name)); CategoryFilter.SelectedIndex = 0;
            BuildTree(); ApplyFilter(); IssuesGrid.ItemsSource = result.Issues; FindingsGrid.ItemsSource = result.Findings;
            ExtensionsGrid.ItemsSource = result.Extensions.Select(x => new ExtensionRow(x.Name, x.Bytes, x.Count, result.Bytes == 0 ? 0 : 100.0 * x.Bytes / result.Bytes)).ToList();
            BuildCategoryChart();
            var state = result.Cancelled ? "Cancelada — resultado parcial" : result.Partial ? "Concluída com áreas não analisadas" : "Concluída";
            ScanSummary.Text = $"{state} · {result.Files:N0} arquivos · {result.FolderCount:N0} pastas · lógico {Sizes.Format(result.Bytes)} · ocupado conhecido {Sizes.Format(result.KnownOccupied)} · {Elapsed()}";
            Status.Text = $"{result.IssueCount:N0} ocorrências · {result.SkippedLinks:N0} links ignorados · {result.DuplicateLinks:N0} hard links descontados · {result.UnknownOccupied:N0} ocupações inconclusivas";
            ShowScanResult();
            RecordStorageTimeline();
            Log(state, $"Arquivos={result.Files}; Bytes={result.Bytes}; Ocorrências={result.IssueCount}");
        }
        catch (Exception ex) { Status.Text = "Falha na análise: " + ex.Message; ScanSummary.Text = "Análise interrompida: " + ex.Message; Log("Falha", ex.Message); }
        finally { cancellation.Dispose(); cancellation = null; AnalyzeButton.IsEnabled = true; CancelButton.IsEnabled = false; Disks.IsEnabled = true; RootPath.IsEnabled = true; EndOperation(); UpdateDisk(); }
    }

    void BuildCategoryChart()
    {
        CategoryChart.Children.Clear(); HomeTopCategories.Children.Clear();
        if (result == null) return;
        foreach (var c in result.Categories.OrderByDescending(c => c.Bytes)) CategoryChart.Children.Add(CategoryRow(c, result.Bytes, true));
        foreach (var c in result.Categories.OrderByDescending(c => c.Bytes).Take(5)) HomeTopCategories.Children.Add(CategoryRow(c, result.Bytes, false));
    }

    /// <summary>Linha de categoria com medidor; cores por referência de recurso para acompanhar o tema.</summary>
    static FrameworkElement CategoryRow(AggregateRow c, long total, bool detailed)
    {
        double pct = total == 0 ? 0 : 100.0 * c.Bytes / total;
        var group = new StackPanel { Margin = new Thickness(0, 0, 0, detailed ? 18 : 12) };
        var head = new DockPanel();
        var size = new TextBlock { Text = detailed ? c.Size : $"{c.Size} · {pct:N1}%", FontWeight = FontWeights.SemiBold, Margin = new Thickness(12, 0, 0, 0) };
        size.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        DockPanel.SetDock(size, Dock.Right);
        var name = new TextBlock { Text = c.Name, TextTrimming = TextTrimming.CharacterEllipsis };
        name.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Text");
        head.Children.Add(size); head.Children.Add(name); group.Children.Add(head);
        var bar = new ProgressBar { Maximum = Math.Max(1, total), Value = c.Bytes, Margin = new Thickness(0, 7, 0, 5), Height = detailed ? 8 : 6 };
        bar.SetResourceReference(StyleProperty, "Meter");
        group.Children.Add(bar);
        if (detailed)
        {
            var caption = new TextBlock { Text = $"{c.Count:N0} arquivos · {pct:N1}% do tamanho analisado" };
            caption.SetResourceReference(StyleProperty, "Text.Caption");
            group.Children.Add(caption);
        }
        AutomationProperties.SetName(group, $"{c.Name}: {c.Size}, {pct:N1}% do tamanho analisado, {c.Count:N0} arquivos");
        group.Focusable = detailed;
        return group;
    }

    async void AnalyzeFolder(object sender, RoutedEventArgs e) { if (selectedFolder != null && cancellation == null) { RootPath.Text = selectedFolder; await StartScan(); } }
    void Cancel(object sender, RoutedEventArgs e) { cancellation?.Cancel(); CancelButton.IsEnabled = false; OperationCancel.IsEnabled = false; OperationTitle.Text = "Cancelando… o resultado parcial será preservado."; Status.Text = "Cancelamento solicitado…"; }

    Dictionary<string, List<FolderRow>> children = new(StringComparer.OrdinalIgnoreCase);
    void BuildTree()
    {
        FolderTree.Items.Clear();
        children = result!.Folders.Where(f => f.Parent != null).GroupBy(f => f.Parent!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.OrderByDescending(f => f.Bytes).ToList(), StringComparer.OrdinalIgnoreCase);
        var root = result.Folders.FirstOrDefault();
        if (root != null) { var item = TreeItem(root); FolderTree.Items.Add(item); item.IsExpanded = true; }
        TreeEmpty.Visibility = FolderTree.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    TreeViewItem TreeItem(FolderRow folder)
    {
        var name = Path.GetFileName(folder.Path.TrimEnd('\\')); if (string.IsNullOrEmpty(name)) name = folder.Path;
        var header = new DockPanel();
        var size = new TextBlock { Text = folder.Size, Margin = new Thickness(10, 0, 4, 0), FontSize = 12 };
        size.SetResourceReference(TextBlock.ForegroundProperty, "Brush.Muted");
        DockPanel.SetDock(size, Dock.Right);
        header.Children.Add(size);
        header.Children.Add(new TextBlock { Text = folder.Partial ? name + " · parcial" : name, TextTrimming = TextTrimming.CharacterEllipsis });
        var item = new TreeViewItem { Header = header, Tag = folder, ToolTip = folder.Path };
        AutomationProperties.SetName(item, $"{name}, {folder.Size}{(folder.Partial ? ", parcial" : "")}");
        if (children.ContainsKey(folder.Path)) { item.Items.Add("Carregando…"); item.Expanded += Expand; }
        return item;
    }
    void Expand(object sender, RoutedEventArgs e)
    {
        if (sender is not TreeViewItem item || e.OriginalSource != item || item.Tag is not FolderRow folder) return;
        if (item.Items.Count == 1 && item.Items[0] is string) { item.Items.Clear(); foreach (var f in children[folder.Path]) item.Items.Add(TreeItem(f)); }
    }
    void FolderSelected(object sender, RoutedPropertyChangedEventArgs<object> e) { if (e.NewValue is TreeViewItem item && item.Tag is FolderRow f) { selectedFolder = f.Path; ApplyFilter(); } }
    void ClearFolder(object sender, RoutedEventArgs e) { selectedFolder = null; ApplyFilter(); }
    void FilterChanged(object sender, SelectionChangedEventArgs e) { if (!initializing) ApplyFilter(); }
    void ApplyFilter()
    {
        if (result == null) return; bool folders = ViewFilter.SelectedIndex == 3; FoldersGrid.Visibility = folders ? Visibility.Visible : Visibility.Collapsed; FilesGrid.Visibility = folders ? Visibility.Collapsed : Visibility.Visible;
        CategoryFilter.IsEnabled = PersonalFilter.IsEnabled = PersonalMinimum.IsEnabled = DuplicateCompareButton.IsEnabled = !folders; SimulateButton.IsEnabled = !folders;
        bool InScope(string p) => selectedFolder == null || p.Equals(selectedFolder, StringComparison.OrdinalIgnoreCase) || p.StartsWith(selectedFolder.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        FoldersGrid.ItemsSource = result.Folders.Where(f => InScope(f.Path)).OrderByDescending(f => f.Bytes).ToList();
        var source = ViewFilter.SelectedIndex == 1 ? result.Candidates : ViewFilter.SelectedIndex == 2 ? result.Protected : result.LargestFiles;
        var category = CategoryFilter.SelectedItem?.ToString();
        var scoped = source.Where(f => InScope(f.Path) && (category == null || category == "Todas as categorias" || f.Category == category));
        long minimum = PersonalMinimum.SelectedIndex switch { 1 => 100L * 1024 * 1024, 2 => 1024L * 1024 * 1024, _ => 0 };
        FilesGrid.ItemsSource = PersonalFileExplorer.Filter(scoped, (PersonalFileKind)Math.Max(0, PersonalFilter.SelectedIndex), minimum, DateTime.UtcNow, DownloadsFolder());
        ScopeLabel.Text = $"{(selectedFolder ?? result.Root)} · {(folders ? "Totais acumulados: não somar pais e filhos. Simulação disponível apenas para arquivos." : "Filtro aplicado aos 1.500 maiores desta visão; não é inventário completo. Mesmo nome/tamanho não prova duplicação; última alteração não informa uso. Dados pessoais exigem revisão no Explorador; recuperação pela Lixeira depende do volume, tamanho e configurações.")}" + (result.FolderLimitReached ? " Limite de pastas atingido." : "");
        UpdateSelectionSummary();
    }
    void FilesSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelectionSummary();
    void UpdateSelectionSummary()
    {
        var files = FilesGrid.SelectedItems.OfType<FileRow>().ToList();
        if (ViewFilter.SelectedIndex == 3) SelectionSummary.Text = "A simulação de pastas inteiras não está habilitada. Escolha uma visão de arquivos.";
        else if (files.Count == 0) SelectionSummary.Text = "Selecione arquivos com Ctrl ou Shift para simular. A simulação não remove nada.";
        else
        {
            int protectedCount = files.Count(f => f.Nature == Nature.Protegido);
            SelectionSummary.Text = $"{files.Count:N0} arquivo(s) selecionado(s) · {Sizes.Format(files.Sum(f => f.Bytes))} lógicos" + (protectedCount > 0 ? $" · {protectedCount:N0} protegido(s), que permanecem bloqueados" : "") + ". Simular não remove nada.";
        }
    }

    async void Simulate(object sender, RoutedEventArgs e)
    {
        var files = FilesGrid.SelectedItems.Cast<FileRow>().ToList();
        if (result == null || cancellation != null || ViewFilter.SelectedIndex == 3 || files.Count == 0) { MessageBox.Show("Selecione um ou mais arquivos após a análise. A simulação de pastas inteiras não está habilitada.", "Seleção necessária"); return; }
        Navigate(Page.Preview); ResetPreview("Verificando metadados, uso e contexto Git…");
        var button = (Button)sender; button.IsEnabled = false; cancellation = new(); AnalyzeButton.IsEnabled = false; CancelButton.IsEnabled = true;
        BeginOperation($"Simulando {files.Count:N0} arquivo(s) — nada será removido");
        try
        {
            preview = await Task.Run(() => new PreviewService(fs).Evaluate(files, cancellation.Token));
            ShowPreview(preview);
            Log("Simulação", $"Itens={preview.Items.Count}; Autorizado=0");
        }
        catch (Exception ex) { ResetPreview("Verificação inconclusiva: " + ex.Message); }
        finally { button.IsEnabled = true; cancellation.Dispose(); cancellation = null; AnalyzeButton.IsEnabled = true; CancelButton.IsEnabled = false; EndOperation(); }
    }

    void ResetPreview(string message)
    {
        PreviewSummary.Text = message; PreviewEmpty.Visibility = Visibility.Visible; PreviewContent.Visibility = Visibility.Collapsed;
    }
    void ShowPreview(PreviewResult p)
    {
        PreviewGrid.ItemsSource = p.Items;
        PreviewSelectedValue.Text = p.Items.Count.ToString("N0");
        PreviewSelectedBytes.Text = Sizes.Format(p.SelectedBytes) + " selecionados";
        PreviewPotentialValue.Text = Sizes.Format(p.CandidateBytes);
        PreviewAuthorizedValue.Text = Sizes.Format(p.AuthorizedBytes);
        PreviewDetail.Text = $"Seleção: {p.Items.Count:N0} arquivos · teto lógico potencial: {Sizes.Format(p.CandidateBytes)} · autorizado para remoção: {Sizes.Format(p.AuthorizedBytes)}. O valor selecionado não é promessa de espaço recuperável.";
        ProcessSummary.Text = "Processos relacionados detectados (sem atribuição garantida a arquivos): " + (string.IsNullOrEmpty(p.Processes) ? "nenhum nesta consulta" : p.Processes);
        PreviewEmpty.Visibility = Visibility.Collapsed; PreviewContent.Visibility = Visibility.Visible;
    }

    void Export(object sender, RoutedEventArgs e)
    {
        if (result == null || cancellation != null) { MessageBox.Show("Conclua ou cancele uma análise antes de exportar.", "Exportar relatório"); return; }
        var dialog = new SaveFileDialog { FileName = "Respiro-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), Filter = "Relatório JSON (*.json)|*.json|Maiores arquivos CSV (*.csv)|*.csv" };
        if (dialog.ShowDialog() != true) return;
        try
        {
            if (dialog.FilterIndex == 1) File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(new { Schema = 1, Scan = result, Preview = preview, Limits = new { FilesPerList = Scanner.FileLimit, Folders = Scanner.FolderLimit, Identities = Scanner.IdentityLimit }, Warning = "Somente leitura. Listas limitadas; totais agregados. Nomes de caminhos podem ser pessoais." }, new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            else { using var w = new StreamWriter(dialog.FileName, false, new UTF8Encoding(true)); w.WriteLine("Caminho;Bytes;Ocupado;Categoria;Proteção;Motivo"); foreach (var f in result.LargestFiles) w.WriteLine(string.Join(';', new[] { f.Path, f.Bytes.ToString(), f.Occupied?.ToString() ?? "inconclusivo", f.Category, f.Protection, f.Reason }.Select(Csv))); }
            Status.Text = "Relatório exportado: " + dialog.FileName; Log("Exportação", dialog.FilterIndex == 1 ? "JSON" : "CSV");
        }
        catch (Exception ex) { MessageBox.Show("Não foi possível salvar: " + ex.Message, "Exportar relatório"); }
    }
    static string Csv(string s) => "\"" + ((s.Length > 0 && "=+-@\t\r".Contains(s[0])) ? "'" : "") + s.Replace("\"", "\"\"") + "\"";
    static void Log(string action, string detail) { try { var dir = DataFolder; Directory.CreateDirectory(dir); var path = Path.Combine(dir, "operations.jsonl"); if (File.Exists(path) && new FileInfo(path).Length > 1024 * 1024) return; File.AppendAllText(path, JsonSerializer.Serialize(new { At = DateTime.Now, Action = action, Detail = detail }) + Environment.NewLine); } catch {/* A logging failure never changes scanned files. */} }
    static string DataFolder => RespiroDataPaths.Root;
}

public sealed record ExtensionRow(string Name, long Bytes, long Count, double Share)
{
    public string Size => Sizes.Format(Bytes);
    public string CountText => Count.ToString("N0");
    public string ShareText => $"{Share:N1}%";
}

