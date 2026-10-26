using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Respiro.Core;
namespace Respiro.App;

/// <summary>Navegação, layout adaptável, tema, visão geral, histórico e capturas de validação.</summary>
public partial class MainWindow
{
    enum Page { Home, Analyze, Categories, Preview, Maintenance, Apps, Cleanup, History, Settings, Optimization }

    public static readonly DependencyProperty IsCompactProperty = DependencyProperty.Register(nameof(IsCompact), typeof(bool), typeof(MainWindow), new PropertyMetadata(false));
    /// <summary>Barra lateral só com ícones (janelas estreitas ou escala alta).</summary>
    public bool IsCompact { get => (bool)GetValue(IsCompactProperty); set => SetValue(IsCompactProperty, value); }

    FrameworkElement[] pages = [];
    RadioButton[] navs = [];
    List<DriveCard> drives = [];
    UiSettings settings = new();
    Stopwatch operationClock = new();
    bool syncingDrive;

    void InitShell()
    {
        pages = [PageHome, PageAnalyze, PageCategories, PagePreview, PageMaintenance, PageApps, PageCleanup, PageHistory, PageSettings, PageOptimization];
        navs = [NavHome, NavAnalyze, NavCategories, NavPreview, NavMaintenance, NavApps, NavCleanup, NavHistory, NavSettings, NavOptimization];
        settings = UiSettings.Load();
        (settings.Theme switch { AppTheme.Light => ThemeLight, AppTheme.Dark => ThemeDark, _ => ThemeSystem }).IsChecked = true;
        SourceInitialized += (_, _) => ThemeManager.ApplyTitleBar(this);
        SizeChanged += (_, _) => ApplyLayout();
        PreviewKeyDown += ShellPreviewKeyDown;
        KeyDown += ShellKeyDown;

        InitializeWindowsCleanup();
        InitializeConsumerCleanup();
        InitializeOptimization();
        CleanupAreaText.Text = "Escolha uma categoria; nenhum arquivo é autorizado automaticamente.";
        BlockedReasons.ItemsSource = new[]
        {
            new InfoRow("Temporários genéricos e dados importantes", "Somente formatos e origens validados podem passar pela prévia. .tmp por nome ou idade permanece bloqueado."),
            new InfoRow("Lixeira com pastas ou origens protegidas", "Somente arquivos individuais de origem temporária comprovada podem ser confirmados; Downloads, dados pessoais, pastas e origens ausentes permanecem bloqueados."),
            new InfoRow("Shaders, Otimização de Entrega e caches genéricos", "Mecanismos globais não garantem inventário individual congelado. Miniaturas CMMM validadas têm adaptador separado."),
            new InfoRow("WinSxS, hibernação, paginação e restauração", "São configurações do sistema. O Respiro apenas mede e explica."),
            new InfoRow("Arquivos das pastas analisadas", "Classificações são heurísticas; a prévia geral sempre autoriza 0 B."),
        };
        SafetyRules.ItemsSource = new[]
        {
            "Executa como usuário atual (asInvoker), sem pedir privilégios de administrador.",
            "Análise de arquivos e diagnóstico do Windows são somente leitura.",
            "Remoção apenas manual por categoria e arquivo validados, com prévia, aprovação e confirmação dupla; Lixeira exige confirmação individual.",
            "Não existe limpeza automática nem agendada.",
            "Links, junctions e placeholders não são percorridos; scripts das pastas analisadas nunca são executados.",
            "Formatos protegidos (fontes, bancos, credenciais, configurações e conteúdo pessoal) prevalecem sobre qualquer indício de regeneração.",
        };
        UnavailableFeatures.ItemsSource = new[]
        {
            new FeatureRow("Parcial", "Histórico e comparação de análises", "Crescimento compara amostras físicas completas do mesmo escopo; consulte Recuperação e Desenvolvimento.", false),
            new FeatureRow("Disponível", "Alertas de espaço livre e crescimento", "Configure limites acima. Espaço livre a cada minuto com o aplicativo aberto; crescimento após diagnósticos, sem limpeza.", false),
            new FeatureRow("Não implementado", "Agendamento de análises", "Previsto inicialmente apenas para análise, nunca para remoção.", false),
            new FeatureRow("Parcial", "Limpeza do Windows por categoria", "Miniaturas, minidumps e Lixeira temporária individual têm validação; shaders, Entrega e logs genéricos seguem bloqueados.", false),
            new FeatureRow("Não implementado", "Inventário de aplicativos de todos os usuários", "A lista atual cobre o usuário atual e o registro da máquina.", false),
            new FeatureRow("Não implementado", "Diagnósticos que exigem administrador", "Aparecem como inconclusivos; não há elevação automática.", false),
            new FeatureRow("Ausente por segurança", "Limpeza automática", "Toda remoção exige seleção explícita e confirmação.", true),
            new FeatureRow("Parcial", "Desinstalação individual", "MSI compatível com confirmação; outros aplicativos são revisados no Windows.", false),
            new FeatureRow("Ausente por segurança", "Exclusão de arquivos das pastas analisadas", "A simulação existe para entender riscos, não para limpar.", true),
        };
        LimitsText.Text = $"Até {Scanner.FileLimit:N0} maiores arquivos por visão · {Scanner.FolderLimit:N0} pastas retidas · {Scanner.IdentityLimit:N0} identidades de arquivo para deduplicação · 1.000 ocorrências detalhadas · 2.000 marcadores Git. As contagens agregadas continuam completas quando uma lista atinge o limite; além do limite de identidades, ocupação e deduplicação ficam inconclusivas.";
        DataFolderText.Text = $"{DataFolder}\noperations.jsonl — registro de operações (≈ 1 MiB no máximo)\ncleanup.jsonl — registro da limpeza manual (4 MiB no máximo)\nwindows-cleanup.jsonl — registro por adaptador (4 MiB no máximo)\nScratch — temporários próprios gerados ao exportar diagnósticos\nsettings.json — tema escolhido";
        var version = typeof(MainWindow).Assembly.GetName().Version;
        AboutText.Text = $"Respiro {version?.ToString(3)} · .NET {Environment.Version} · {System.Runtime.InteropServices.RuntimeInformation.OSDescription}\nInterface WPF nativa, sem dependências externas.";
        UpdateCleanupState();
        ApplyLayout();
    }

    // ───── Navegação ─────
    void Navigate(Page page) => navs[(int)page].IsChecked = true;
    void NavChecked(object sender, RoutedEventArgs e)
    {
        if (pages.Length == 0) return;
        int index = Array.IndexOf(navs, sender);
        for (int i = 0; i < pages.Length; i++) pages[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;
        if (index == (int)Page.History) LoadHistory();
    }
    void GoAnalyze(object sender, RoutedEventArgs e) => Navigate(Page.Analyze);
    void GoCategories(object sender, RoutedEventArgs e) => Navigate(Page.Categories);
    void GoMaintenance(object sender, RoutedEventArgs e) => Navigate(Page.Maintenance);

    void ShellPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        if (e.Key is Key.D0 or Key.NumPad0) { Navigate(Page.Optimization); NavOptimization.Focus(); e.Handled = true; return; }
        int n = e.Key is >= Key.D1 and <= Key.D9 ? e.Key - Key.D1 : e.Key is >= Key.NumPad1 and <= Key.NumPad9 ? e.Key - Key.NumPad1 : -1;
        if (n < 0 || n >= navs.Length) return;
        Navigate((Page)n); navs[n].Focus(); e.Handled = true;
    }
    void ShellKeyDown(object sender, KeyEventArgs e)
    {
        // Esc só cancela; menus e listas abertos tratam o Esc antes (evento borbulhante).
        if (e.Key == Key.Escape && cancellation != null && OperationCancel.IsEnabled) { Cancel(this, e); e.Handled = true; }
    }

    // ───── Layout adaptável ─────
    void ApplyLayout()
    {
        double w = ActualWidth > 0 ? ActualWidth : Width;
        IsCompact = w < 1100;
        bool narrow = w < 1000;
        Sidebar.Width = IsCompact ? 68 : 232;
        var labels = IsCompact ? Visibility.Collapsed : Visibility.Visible;
        BrandName.Visibility = BrandTagline.Visibility = SidebarFooterText.Visibility = labels;
        NavGroup1.Visibility = NavGroup2.Visibility = NavGroup3.Visibility = labels;
        NavMaintenance.Margin = NavHistory.Margin = new Thickness(0, IsCompact ? 14 : 1, 0, 1);
        Pages.Margin = narrow ? new Thickness(20, 18, 20, 12) : new Thickness(32, 24, 32, 16);
        OperationPanel.Padding = narrow ? new Thickness(20, 10, 20, 10) : new Thickness(28, 12, 28, 12);

        Stack(HomeTop, HomeDrivesCard, narrow, 16);
        HomeMetrics.Columns = narrow ? 2 : 3;
        HomeModes.Columns = narrow ? 1 : 3;

        bool stackCategories = w < 1150;
        Stack(CategoriesGrid, ExtensionsCard, stackCategories, 16);
        CategoriesGrid.RowDefinitions[1].Height = stackCategories ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;

        bool stackDetail = w < 1180;
        Stack(MaintenanceSplit, MaintenanceDetailCard, stackDetail, 12);
        MaintenanceDetailCard.Height = stackDetail ? 200 : double.NaN;

        Stack(CleanupLayout, CleanupSide, w < 1150, 4);
        TreeColumn.Width = new GridLength(narrow ? 220 : 280);
        PreviewMetrics.Columns = w < 900 ? 1 : 3;
        // Janelas baixas (ou escala alta): a lista de arquivos tem prioridade sobre o cabeçalho.
        bool shortWindow = (ActualHeight > 0 ? ActualHeight : Height) < 760;
        AnalyzeHeader.Visibility = TreeCaption.Visibility = DiskRow.Visibility = shortWindow ? Visibility.Collapsed : Visibility.Visible;
        AnalyzeToolbar.Margin = new Thickness(0, shortWindow ? 0 : 18, 0, shortWindow ? 10 : 16);
        ViewFilter.Width = narrow ? 170 : 200; CategoryFilter.Width = narrow ? 180 : 220;
        ExportLabel.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
    }

    readonly Dictionary<Grid, (GridLength First, GridLength Gap, GridLength Second)> originalColumns = [];
    /// <summary>Move o segundo painel (coluna 2) para baixo do primeiro quando falta largura.</summary>
    void Stack(Grid grid, FrameworkElement second, bool stacked, double gap)
    {
        if (!originalColumns.TryGetValue(grid, out var o))
            originalColumns[grid] = o = (grid.ColumnDefinitions[0].Width, grid.ColumnDefinitions[1].Width, grid.ColumnDefinitions[2].Width);
        Grid.SetRow(second, stacked ? 1 : 0);
        Grid.SetColumn(second, stacked ? 0 : 2);
        Grid.SetColumnSpan(second, stacked ? 3 : 1);
        second.Margin = stacked ? new Thickness(0, gap, 0, 0) : new Thickness(0);
        grid.ColumnDefinitions[0].Width = stacked ? new GridLength(1, GridUnitType.Star) : o.First;
        grid.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : o.Gap;
        grid.ColumnDefinitions[2].Width = stacked ? new GridLength(0) : o.Second;
    }

    // ───── Operação em andamento ─────
    void BeginOperation(string title)
    {
        operationClock = Stopwatch.StartNew();
        OperationTitle.Text = title; OperationDetail.Text = ""; OperationPath.Text = ""; OperationPercent.Text = "";
        ScanBar.Value = 0; ScanBar.IsIndeterminate = true; OperationCancel.IsEnabled = true;
        OperationPanel.Visibility = Visibility.Visible;
        DriveList.IsEnabled = false; HomeAnalyzeButton.IsEnabled = false;
        Status.Text = title;
        UpdateCleanupState();
    }
    void EndOperation()
    {
        OperationPanel.Visibility = Visibility.Collapsed; ScanBar.IsIndeterminate = false;
        DriveList.IsEnabled = true; HomeAnalyzeButton.IsEnabled = true;
        UpdateCleanupState();
    }
    string Elapsed() { var t = operationClock.Elapsed; return t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min {t.Seconds:00} s" : $"{t.TotalSeconds:N0} s"; }

    // ───── Discos e visão geral ─────
    void LoadDrives()
    {
        drives = [];
        foreach (var d in DriveInfo.GetDrives())
        {
            try { if (d.IsReady) drives.Add(new DriveCard(d.Name, SafeLabel(d), d.TotalSize, d.TotalFreeSpace, d.AvailableFreeSpace)); }
            catch { /* Unidade indisponível no momento: fica fora da lista. */ }
        }
        Disks.ItemsSource = drives.Select(d => d.Name).ToList();
        DriveList.ItemsSource = drives;
    }
    static string SafeLabel(DriveInfo d) { try { return d.VolumeLabel; } catch { return ""; } }

    void SyncDriveList()
    {
        if (DriveList == null) return;
        syncingDrive = true;
        DriveList.SelectedItem = drives.FirstOrDefault(d => string.Equals(d.Name, Disks.SelectedItem?.ToString(), StringComparison.OrdinalIgnoreCase));
        syncingDrive = false;
    }
    void DriveListChanged(object sender, SelectionChangedEventArgs e)
    {
        if (syncingDrive || DriveList.SelectedItem is not DriveCard card || cancellation != null) return;
        Disks.SelectedItem = card.Name;
    }

    void UpdateHomeDisk(DriveInfo? d)
    {
        if (HomeRing == null) return;
        if (d == null)
        {
            HomeDriveName.Text = "Volume indisponível"; HomeDriveLabel.Text = ""; HomeTotal.Text = HomeUsed.Text = HomeFree.Text = HomeRingPercent.Text = "—"; HomeRing.Value = 0;
            HomeFreeWarning.Visibility = Visibility.Collapsed; return;
        }
        long used = d.TotalSize - d.TotalFreeSpace; double fraction = d.TotalSize > 0 ? (double)used / d.TotalSize : 0;
        string label = SafeLabel(d);
        HomeDriveName.Text = string.IsNullOrWhiteSpace(label) ? $"Disco {d.Name.TrimEnd('\\')}" : $"{label} ({d.Name.TrimEnd('\\')})";
        HomeDriveLabel.Text = $"{d.DriveFormat} · {d.DriveType switch { DriveType.Fixed => "unidade fixa", DriveType.Removable => "unidade removível", DriveType.Network => "unidade de rede", _ => "unidade" }}";
        HomeTotal.Text = Sizes.Format(d.TotalSize); HomeUsed.Text = Sizes.Format(used); HomeFree.Text = Sizes.Format(d.AvailableFreeSpace);
        HomeRing.Value = fraction; HomeRingPercent.Text = $"{fraction * 100:N0}%";
        System.Windows.Automation.AutomationProperties.SetName(HomeRing, $"{fraction * 100:N0}% ocupado");
        HomeAnalyzeText.Text = $"Analisar {d.Name.TrimEnd('\\')}";
        double freeShare = d.TotalSize > 0 ? (double)d.AvailableFreeSpace / d.TotalSize : 1;
        HomeFreeWarning.Visibility = freeShare < 0.10 ? Visibility.Visible : Visibility.Collapsed;
        HomeFreeWarningText.Text = $"Pouco espaço: {freeShare * 100:N1}% disponível";
        var card = drives.FindIndex(x => string.Equals(x.Name, d.Name, StringComparison.OrdinalIgnoreCase));
        if (card >= 0) { drives[card] = new DriveCard(d.Name, label, d.TotalSize, d.TotalFreeSpace, d.AvailableFreeSpace); DriveList.Items.Refresh(); SyncDriveList(); }
    }

    async void HomeAnalyze(object sender, RoutedEventArgs e)
    {
        if (cancellation != null) return;
        var root = Path.GetPathRoot(RootPath.Text) ?? Disks.SelectedItem?.ToString();
        if (root == null) return;
        RootPath.Text = root; Navigate(Page.Analyze); await StartScan();
    }
    void HomeChooseFolder(object sender, RoutedEventArgs e)
    {
        if (cancellation != null) return;
        if (PickFolder()) { Navigate(Page.Analyze); AnalyzeButton.Focus(); }
    }

    void ShowScanResult()
    {
        bool has = result != null;
        LastScanEmpty.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        LastScanPanel.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        CategoriesEmpty.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        CategoriesGrid.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        if (result == null) { HomeScanState.Text = "Nenhuma nesta sessão"; HomeScanStateBadge.Style = (Style)FindResource("Badge"); HomeScanRoot.Text = ""; return; }
        HomeScanState.Text = result.Cancelled ? "Cancelada · parcial" : result.Partial ? "Concluída com áreas não analisadas" : "Concluída";
        HomeScanStateBadge.Style = (Style)FindResource(result.Cancelled || result.Partial ? "Badge.Warning" : "Badge.Accent");
        HomeScanRoot.Text = $"{result.Root} · {result.Finished:dd/MM/yyyy HH:mm}";
        HomeFiles.Text = result.Files.ToString("N0"); HomeFolders.Text = result.FolderCount.ToString("N0");
        HomeLogical.Text = Sizes.Format(result.Bytes); HomeOccupied.Text = Sizes.Format(result.KnownOccupied);
        HomeIssues.Text = result.IssueCount.ToString("N0"); HomeUnknown.Text = result.UnknownOccupied.ToString("N0");
        CategoriesSummary.Text = $"{result.Root} · {Sizes.Format(result.Bytes)} lógicos em {result.Files:N0} arquivos. Totais de todos os arquivos enumerados, descontando hard links reconhecidos.";
    }

    // ───── Tema ─────
    void ThemeChoice(object sender, RoutedEventArgs e)
    {
        var mode = sender == ThemeLight ? AppTheme.Light : sender == ThemeDark ? AppTheme.Dark : AppTheme.System;
        if (initializing && mode == ThemeManager.Mode) return;
        settings.Theme = mode; ThemeManager.Apply(mode);
        if (!initializing) settings.Save();
    }

    void OpenDataFolder(object sender, RoutedEventArgs e)
    {
        if (!Directory.Exists(DataFolder)) { MessageBox.Show("A pasta de dados ainda não foi criada. Ela surge após a primeira análise ou exportação.", "Dados locais"); return; }
        try { Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { DataFolder }, UseShellExecute = false }); }
        catch (Exception ex) { MessageBox.Show("Não foi possível abrir a pasta: " + ex.Message, "Dados locais"); }
    }
    void OpenWindowsStorage(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:storagesense") { UseShellExecute = true }); }
        catch (Exception ex) { MessageBox.Show("Não foi possível abrir as Configurações do Windows: " + ex.Message, "Armazenamento do Windows"); }
    }

    // ───── Histórico (leitura dos registros locais) ─────
    sealed record HistoryRow(DateTime At, string Action, string Detail, string Path, string Size) { public string When => At.ToString("dd/MM/yyyy HH:mm:ss"); }
    void RefreshHistory(object sender, RoutedEventArgs e) => LoadHistory();
    void LoadHistory()
    {
        // Leitura síncrona e curta: o registro de limpeza nunca fica aberto enquanto uma remoção grava nele.
        if (cancellation != null) { HistorySummary.Text = "Aguarde a operação em andamento terminar para ler os registros."; return; }
        LoadStorageTimeline();
        var notes = new List<string>();
        var operations = new List<HistoryRow>(); var cleanup = new List<HistoryRow>();
        foreach (string root in RespiroDataPaths.ExistingRoots)
        {
            string source = Path.GetFileName(root);
            operations.AddRange(ReadJsonl(Path.Combine(root, "operations.jsonl"), e =>
                new HistoryRow(Date(e, "At"), Text(e, "Action"), Text(e, "Detail"), "", ""), source + " operações", notes));
            foreach (string journal in new[] { "cleanup.jsonl", "windows-cleanup.jsonl" })
                cleanup.AddRange(ReadJsonl(Path.Combine(root, journal), e =>
                    new HistoryRow(Date(e, "At"), Text(e, "Action"), journal == "cleanup.jsonl" ? Text(e, "detail") : Text(e, "Detail"),
                        Text(e, "Path"), e.TryGetProperty("Bytes", out var b) && b.TryGetInt64(out var n) ? Sizes.Format(n) : ""),
                    source + " " + journal, notes));
        }
        OperationsGrid.ItemsSource = operations.OrderByDescending(e => e.At).ToList();
        CleanupJournalGrid.ItemsSource = cleanup.OrderByDescending(e => e.At).ToList();
        HistorySummary.Text = string.Join(" · ", notes);
    }
    static List<HistoryRow> ReadJsonl(string path, Func<JsonElement, HistoryRow> parse, string name, List<string> notes)
    {
        var rows = new List<HistoryRow>();
        if (!File.Exists(path)) { notes.Add($"Registro de {name}: vazio"); return rows; }
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            int invalid = 0;
            for (string? line = reader.ReadLine(); line != null; line = reader.ReadLine())
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try { using var doc = JsonDocument.Parse(line); rows.Add(parse(doc.RootElement)); } catch (JsonException) { invalid++; }
            }
            rows.Reverse();
            notes.Add($"Registro de {name}: {rows.Count:N0} evento(s)" + (invalid > 0 ? $", {invalid} linha(s) ilegível(is)" : ""));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { notes.Add($"Registro de {name} indisponível: {ex.Message}"); }
        return rows;
    }
    static string Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString() : "";
    static DateTime Date(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetDateTime(out var d) ? (d.Kind == DateTimeKind.Utc ? d.ToLocalTime() : d) : DateTime.MinValue;

    // ───── Capturas de validação (--capture) ─────
    async Task CaptureAll(bool withMaintenance)
    {
        var originalTheme = ThemeManager.Mode;
        if (FilesGrid.Items.Count > 2) { FilesGrid.SelectedItems.Clear(); for (int i = 0; i < 3; i++) FilesGrid.SelectedItems.Add(FilesGrid.Items[i]); }
        try { if (WindowsCleanupCategorySelector.SelectedItem is string) OwnedTemporaryGrid.ItemsSource = temporaryStore.Enumerate().Take(1000).Select(p => new TemporaryRow(p, Sizes.Format(new FileInfo(p).Length), "Origem e uso serão verificados na prévia.")).ToList(); } catch { }
        UpdateOwnedSelectionText();
        var names = Enum.GetNames<Page>();
        foreach (var theme in new[] { AppTheme.Dark, AppTheme.Light })
        {
            ThemeManager.Apply(theme);
            Width = 1280; Height = 840;
            for (int p = 0; p < pages.Length; p++) { Navigate((Page)p); await Settle(); Capture($"{theme.ToString().ToLowerInvariant()}-{p}-{names[p].ToLowerInvariant()}"); }
            Navigate(Page.Optimization);
            for (int tab = 0; tab < OptimizationTabs.Items.Count; tab++) { OptimizationTabs.SelectedIndex = tab; await Settle(); Capture($"{theme.ToString().ToLowerInvariant()}-optimization-{tab}"); }
            OptimizationTabs.SelectedIndex = 0;
            if (withMaintenance)
            {
                Navigate(Page.Maintenance); WindowsDiagnosticsGrid.SelectedIndex = 0;
                for (int child = 0; child < WindowsMaintenanceTabs.Items.Count; child++) { WindowsMaintenanceTabs.SelectedIndex = child; await Settle(); Capture($"{theme.ToString().ToLowerInvariant()}-maintenance-{child}"); }
                WindowsMaintenanceTabs.SelectedIndex = 0;
            }
            for (int tab = 1; tab < AnalyzeTabs.Items.Count; tab++) { Navigate(Page.Analyze); AnalyzeTabs.SelectedIndex = tab; await Settle(); Capture($"{theme.ToString().ToLowerInvariant()}-analyze-tab{tab}"); }
            AnalyzeTabs.SelectedIndex = 0;
            for (int tab = 0; tab < HistoryTabs.Items.Count; tab++)
            {
                Navigate(Page.History); HistoryTabs.SelectedIndex = tab; await Settle();
                Capture($"{theme.ToString().ToLowerInvariant()}-history-tab{tab}");
            }
        }
        // Janelas menores: equivalem, em pixels independentes, a telas com escala 125–200%.
        foreach (var (w, h, tag) in new[] { (1024, 700, "1024"), (820, 600, "820") })
        {
            Width = w; Height = h;
            foreach (var p in new[] { Page.Home, Page.Analyze, Page.Categories, Page.Maintenance, Page.Apps, Page.Cleanup, Page.History, Page.Settings, Page.Optimization })
{
    Navigate(p);
    if (p == Page.History) HistoryTabs.SelectedIndex = 1;
    await Settle();
    Capture($"small-{tag}-{(int)p}-{names[(int)p].ToLowerInvariant()}");
}
        }
        Width = 1280; Height = 840; Navigate(Page.Home);
        ThemeManager.Apply(originalTheme);
    }
    async Task Settle() { UpdateLayout(); await Task.Delay(180); UpdateLayout(); }
    void Capture(string name)
    {
        UpdateLayout();
        // Renderiza a área cliente real (sem escalar), sobre o fundo do tema.
        var root = (FrameworkElement)Content; double w = root.ActualWidth, h = root.ActualHeight; var area = new Rect(0, 0, w, h);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(w), (int)Math.Ceiling(h), 96, 96, PixelFormats.Pbgra32);
        var surface = new DrawingVisual();
        var brush = new VisualBrush(root) { Stretch = Stretch.None, ViewboxUnits = BrushMappingMode.Absolute, Viewbox = area, ViewportUnits = BrushMappingMode.Absolute, Viewport = area, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
        using (var dc = surface.RenderOpen()) { dc.DrawRectangle((Brush)FindResource("Brush.Bg"), null, area); dc.DrawRectangle(brush, null, area); }
        bitmap.Render(surface);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var dir = Path.Combine(AppContext.BaseDirectory, "captures"); Directory.CreateDirectory(dir);
        using var stream = File.Create(Path.Combine(dir, name + ".png")); encoder.Save(stream);
    }
}

public sealed record DriveCard(string Name, string Label, long Total, long Free, long Available)
{
    public double UsedPercent => Total > 0 ? 100.0 * (Total - Free) / Total : 0;
    public string UsedPercentText => $"{UsedPercent:N0}% usado";
    public string Title => string.IsNullOrWhiteSpace(Label) ? $"Disco {Name.TrimEnd('\\')}" : $"{Label} ({Name.TrimEnd('\\')})";
    public string FreeText => $"{Sizes.Format(Available)} disponíveis de {Sizes.Format(Total)}";
}
public sealed record InfoRow(string Title, string Reason);
public sealed record FeatureRow(string State, string Title, string Reason, bool Intentional);


