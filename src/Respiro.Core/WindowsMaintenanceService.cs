using Microsoft.Win32;
using System.Runtime.InteropServices;
using System.Text.Json;
namespace Respiro.Core;
public sealed class WindowsMaintenanceService
{
    readonly IReadOnlyFileSystem fs;
    readonly string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    readonly string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    readonly string common = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    public WindowsMaintenanceService() : this(new WindowsFileSystem()) { }
    internal WindowsMaintenanceService(IReadOnlyFileSystem fs) { this.fs = fs; }
    public async Task<MaintenanceReport> CollectAsync(IProgress<string>? progress = null, CancellationToken token = default)
    {
        var report = new MaintenanceReport();
        try
        {
            progress?.Report("Consultando APIs e provedores oficiais do Windows…");
            var officialTask = OfficialWindowsQueries.Collect(token);
            await Task.Run(() =>
            {
                InventoryTraditional(report);
                foreach (var spec in Locations())
                {
                    token.ThrowIfCancellationRequested(); progress?.Report("Medindo " + spec.Name + "…");
                    report.Diagnostics.Add(Measure(spec.Name, spec.Path, spec.Impact, spec.Restrictions, spec.Filter, token));
                }
                MeasureBrowserCaches(report, token);
                var info = new RecycleInfo { Size = (uint)Marshal.SizeOf<RecycleInfo>() };
                int hr = SHQueryRecycleBin(null, ref info);
                report.Diagnostics.Add(new("Lixeira do usuário", "Lixeiras dos volumes disponíveis", hr == 0 ? info.Bytes : null, hr == 0 ? info.Items : null, hr == 0 ? "Consultado pela API do Shell" : $"Inconclusivo: HRESULT 0x{hr:X8}", "Esvaziar elimina a possibilidade de restaurar os itens pela Lixeira.", "Somente diagnóstico; nenhuma chamada de esvaziamento.", "SHQueryRecycleBinW"));
                var hiber = Path.Combine(Path.GetPathRoot(windows)!, "hiberfil.sys");
                report.Diagnostics.Add(MeasureFile("Hibernação / hiberfil.sys", hiber, "Desativar a hibernação pode liberar este arquivo, mas também afeta Inicialização Rápida.", "Somente diagnóstico. Nenhum powercfg de alteração é executado."));
                report.Diagnostics.Add(new("Inicialização Rápida", @"HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power", null, null, RegistryFlag(@"SYSTEM\CurrentControlSet\Control\Session Manager\Power", "HiberbootEnabled"), "Depende do arquivo de hibernação; não é consumo adicional independente.", "Valor configurado, não garantia de disponibilidade efetiva. Consulte também powercfg /a.", "Registro do Windows, somente leitura"));
            }, token);
            progress?.Report("Consolidando consultas oficiais e inventário…");
            var official = await officialTask;
            if (official.Success)
            {
                try { ApplyOfficial(report, official.Text); }
                catch (JsonException e) { report.Notices.Add("Resposta oficial inconclusiva: " + e.Message); AddUnavailableOfficialRows(report); }
            }
            else { report.Notices.Add("Consultas oficiais indisponíveis: " + official.Error); AddUnavailableOfficialRows(report); }
            report.Notices.Add("Os diagnósticos podem se sobrepor: não some todas as linhas. Valores são tamanhos observados, não espaço recuperável aprovado.");
            report.Notices.Add("Inventário Store: somente usuário atual. EstimatedSize dos aplicativos tradicionais é informado pelo instalador, pode estar desatualizado e omitir dados de usuário.");
        }
        catch (OperationCanceledException) { report.Cancelled = true; report.Notices.Add("Diagnóstico cancelado; dados parciais."); }
        finally { report.Finished = DateTime.Now; }
        return report;
    }
    record Spec(string Name, string Path, string Impact, string Restrictions, Func<DiskEntry, bool>? Filter = null);
    IEnumerable<Spec> Locations()
    {
        var systemDrive = Path.GetPathRoot(windows)!;
        yield return new("Temporários do usuário", Path.GetTempPath().TrimEnd('\\'), "Pode liberar resíduos, mas também pode conter documentos e estado ativo.", "Nenhum arquivo genérico autorizado; nome .tmp e idade não comprovam origem.");
        yield return new("Temporários do Windows", Path.Combine(windows, "Temp"), "Pode conter instaladores e serviços em execução.", "Acesso pode exigir administrador; sem elevação automática e sem remoção.");
        yield return new("Cache de miniaturas", Path.Combine(local, "Microsoft", "Windows", "Explorer"), "O Windows recria miniaturas; haverá trabalho adicional ao reabrir pastas.", "Tamanho observado não é autorização. Em Limpeza, somente bancos CMMM reconhecidos, sem uso e revalidados podem ser elegíveis.", e => Path.GetFileName(e.Path).StartsWith("thumbcache_", StringComparison.OrdinalIgnoreCase) && Path.GetExtension(e.Path).Equals(".db", StringComparison.OrdinalIgnoreCase));
        yield return new("Cache de shaders DirectX", Path.Combine(local, "D3DSCache"), "Recompilação pode causar pausas e lentidão temporária em jogos e aplicações.", "Cache gerenciado pelo sistema: somente diagnóstico; use as ferramentas de armazenamento do Windows.");
        yield return new("Otimização de Entrega — localização", Path.Combine(windows, "ServiceProfiles", "NetworkService", "AppData", "Local", "Microsoft", "Windows", "DeliveryOptimization", "Cache"), "A remoção oficial pode exigir novos downloads e reduzir o compartilhamento de atualizações.", "Medição por arquivos pode ser negada; a consulta oficial CacheSizeBytes aparece separadamente. Nenhuma remoção de cache do serviço.");
        foreach (var wer in new[] { Path.Combine(local, "Microsoft", "Windows", "WER"), Path.Combine(common, "Microsoft", "Windows", "WER") })
            yield return new("Relatórios de erro antigos (30 dias)", wer, "Eliminar relatórios reduz evidências para diagnóstico de falhas.", "Filtro por modificação; relatórios recentes e arquivos inacessíveis não entram no valor. Sem limpeza.", e => e.Modified < DateTime.UtcNow.AddDays(-30));
        yield return new("Dumps antigos do usuário (30 dias)", Path.Combine(local, "CrashDumps"), "Dumps podem conter dados sensíveis e evidências de falhas.", "Somente diagnóstico, sem leitura do conteúdo.", e => e.Modified < DateTime.UtcNow.AddDays(-30));
        yield return new("Minidumps antigos do Windows (30 dias)", Path.Combine(windows, "Minidump"), "Necessários para investigar telas azuis.", "Pode exigir privilégios para medição; não há remoção.", e => e.Modified < DateTime.UtcNow.AddDays(-30));
        yield return new("Resíduos de downloads do Windows Update", Path.Combine(windows, "SoftwareDistribution", "Download"), "Podem pertencer a uma atualização em andamento; rebaixar o cache pode exigir downloads.", "Não parar serviços nem remover manualmente; somente diagnóstico.");
        foreach (var n in new[] { "$WINDOWS.~BT", "$WINDOWS.~WS", "Windows.old" })
            yield return new(n == "Windows.old" ? "Instalação anterior — Windows.old" : "Resíduos de atualização — " + n, Path.Combine(systemDrive, n), "Pode conter dados migrados e recursos para reverter uma atualização.", "Preservar. Windows.old pode conter dados pessoais; limpeza somente por mecanismo oficial, fora desta missão.");
        yield return new("Logs antigos do Windows (30 dias)", Path.Combine(windows, "Logs"), "Reduz evidências de atualização, instalação e manutenção.", "Arquivos .log/.etl com pelo menos 30 dias; eventos e logs ativos não são apagados.", e => e.Modified < DateTime.UtcNow.AddDays(-30) && Path.GetExtension(e.Path).ToLowerInvariant() is ".log" or ".etl");
        yield return new("Componentes Windows — WinSxS (lógico)", Path.Combine(windows, "WinSxS"), "A medição lógica não equivale ao espaço recuperável; há hard links compartilhados com o restante do Windows.", "Nunca apagar WinSxS manualmente. O diagnóstico oficial DISM /AnalyzeComponentStore requer elevação; nenhum componente é alterado.");
        yield return new(OwnedTemporaryStore.Category, new OwnedTemporaryStore().Root, "Somente snapshots descartáveis produzidos pelo próprio Respiro, com comprovante e hash, podem ser limpos manualmente.", "Arquivos diretos com origem comprovada, idade mínima de sete dias, sem links/proteções/uso, mediante seleção e confirmação. Comprovantes são preservados.");
    }
    internal DiagnosticRow Measure(string name, string path, string impact, string restrictions, Func<DiskEntry, bool>? filter, CancellationToken token)
    {
        long bytes = 0, files = 0, errors = 0, links = 0; bool unknown = false;
        var ids = new HashSet<string>(StringComparer.Ordinal); var stack = new Stack<IEnumerator<DiskEntry>>();
        try
        {
            // File.Exists/Directory.Exists hide access-denied: Stat first, distinguish missing from unknown.
            var stat = fs.Stat(path);
            for (string? p = path; p != null; p = Path.GetDirectoryName(p))
                if (fs.Stat(p).Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Raiz/ancestral é reparse point; não percorrido.");
            if (!stat.Directory) throw new IOException("Localização esperada não é diretório.");
            stack.Push(fs.Enumerate(path).GetEnumerator());
            while (stack.Count > 0)
            {
                token.ThrowIfCancellationRequested(); var iterator = stack.Peek(); DiskEntry item;
                try { if (!iterator.MoveNext()) { stack.Pop().Dispose(); continue; } item = iterator.Current; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { errors++; stack.Pop().Dispose(); continue; }
                if (item.Attributes.HasFlag(FileAttributes.ReparsePoint)) { links++; continue; }
                if (item.Directory) { try { stack.Push(fs.Enumerate(item.Path).GetEnumerator()); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { errors++; } continue; }
                if (filter != null && !filter(item)) continue;
                var a = fs.Allocated(item.Path);
                if (a.Identity != null && ids.Contains(a.Identity)) continue;
                if (a.Identity != null && ids.Count < 250000) ids.Add(a.Identity); else unknown = true;
                bytes += item.Length; files++;
            }
            return new(name, path, files > 0 || errors + links == 0 ? bytes : null, files, errors + links > 0 || unknown ? $"Parcial: {errors} falhas, {links} links ignorados; identidade {(unknown ? "limitada" : "consultada")}" : "Medido — tamanho lógico", impact, restrictions, "Metadados Windows; aliases reconhecidos descontados nesta linha");
        }
        catch (FileNotFoundException) { return new(name, path, 0, 0, "Não encontrado", impact, restrictions, "Metadados Windows"); }
        catch (DirectoryNotFoundException) { return new(name, path, 0, 0, "Não encontrado", impact, restrictions, "Metadados Windows"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(name, path, files > 0 ? bytes : null, files, "Inconclusivo: " + e.Message, impact, restrictions, "Metadados Windows"); }
        finally { while (stack.Count > 0) stack.Pop().Dispose(); }
    }
    DiagnosticRow MeasureFile(string name, string path, string impact, string restrictions)
    {
        try { var f = fs.Stat(path); return new(name, path, f.Length, 1, "Encontrado", impact, restrictions, "Metadados do arquivo de sistema"); }
        catch (FileNotFoundException) { return new(name, path, 0, 0, "Não encontrado", impact, restrictions, "Metadados Windows"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(name, path, null, null, "Inconclusivo: " + e.Message, impact, restrictions, "Metadados Windows"); }
    }
    static string RegistryFlag(string key, string value)
    { try { using var k = Registry.LocalMachine.OpenSubKey(key); return k?.GetValue(value) is int n ? n == 0 ? "Configurado: desativado" : "Configurado: ativado" : "Inconclusivo: valor ausente"; } catch (Exception e) { return "Inconclusivo: " + e.Message; } }
    void MeasureBrowserCaches(MaintenanceReport r, CancellationToken token)
    {
        foreach (var basePath in new[] { Path.Combine(local, "Microsoft", "Edge", "User Data"), Path.Combine(local, "Google", "Chrome", "User Data") })
        {
            try
            {
                if (!Directory.Exists(basePath) || File.GetAttributes(basePath).HasFlag(FileAttributes.ReparsePoint)) continue;
                foreach (var profile in new DirectoryInfo(basePath).EnumerateDirectories().Take(40))
                {
                    if (profile.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                    foreach (var cache in new[] { "Cache", "Code Cache", "GPUCache" })
                    {
                        var path = Path.Combine(profile.FullName, cache); if (!Directory.Exists(path)) continue;
                        r.Diagnostics.Add(Measure("Cache de aplicativo — " + Path.GetFileName(Path.GetDirectoryName(basePath)) + "/" + profile.Name + "/" + cache, path, "O aplicativo poderá baixar ou recompilar dados novamente.", "Cookies, histórico, perfis e credenciais não são classificados como cache removível. Somente diagnóstico.", null, token));
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { r.Notices.Add("Caches de aplicativos: " + e.Message); }
        }
        var firefox = Path.Combine(local, "Mozilla", "Firefox", "Profiles");
        try { if (Directory.Exists(firefox)) foreach (var p in new DirectoryInfo(firefox).EnumerateDirectories().Take(40)) { var cache = Path.Combine(p.FullName, "cache2"); if (Directory.Exists(cache)) r.Diagnostics.Add(Measure("Cache Firefox", cache, "Novos downloads poderão ser necessários.", "Somente cache2 conhecido; dados de perfil preservados. Sem limpeza.", null, token)); } }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { r.Notices.Add("Cache Firefox: " + e.Message); }
        r.Diagnostics.Add(MeasureFile("Dump completo do Windows", Path.Combine(windows, "MEMORY.DMP"), "Pode ser necessário para investigar falhas graves.", "Somente diagnóstico; pode conter conteúdo de memória sensível."));
    }
    static void InventoryTraditional(MaintenanceReport r)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var registry = RegistryKey.OpenBaseKey(hive, view);
                using var key = registry.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"); if (key == null) continue;
                foreach (var sub in key.GetSubKeyNames())
                {
                    try
                    {
                        using var app = key.OpenSubKey(sub); string name = app?.GetValue("DisplayName") as string ?? ""; if (string.IsNullOrWhiteSpace(name)) continue;
                        string publisher = app?.GetValue("Publisher") as string ?? "Não informado", version = app?.GetValue("DisplayVersion") as string ?? "Não informado", location = app?.GetValue("InstallLocation") as string ?? "Não informada";
                        if (!seen.Add($"{hive}|{sub}|{name}|{version}|{location}")) continue;
                        long? bytes = app?.GetValue("EstimatedSize") switch { int n when n >= 0 => (long)n * 1024, long n when n >= 0 && n < long.MaxValue / 1024 => n * 1024, _ => null };
                        r.Applications.Add(new(name, publisher, version, bytes, location, "Tradicional", $"{hive}/{view}; tamanho estimado pelo instalador. Data informada pode corresponder a atualização ou reparo. Nenhum aplicativo marcado como desnecessário.") { InstallerDate = app?.GetValue("InstallDate") as string,
                            ProductCode = Guid.TryParseExact(sub, "B", out _) ? sub : null,
                            UninstallHive = hive.ToString(), UninstallView = view.ToString(),
                            UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + sub,
                            WindowsInstaller = app?.GetValue("WindowsInstaller") is int wi && wi == 1,
                            SystemComponent = (app?.GetValue("SystemComponent") is int sc && sc != 0) || app?.GetValue("ParentKeyName") != null });
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { r.Notices.Add("Entrada de aplicativo inacessível: " + sub); }
                }
            }
            catch (Exception e) { r.Notices.Add($"Inventário {hive}/{view}: {e.Message}"); }
        }
    }
    internal static void ApplyOfficial(MaintenanceReport r, string json)
    {
        using var doc = JsonDocument.Parse(json); var root = doc.RootElement;
        IEnumerable<JsonElement> Rows(string name) => root.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().ToArray() : [];
        string Text(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.ToString() : "Não informado";
        long? Number(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
        foreach (var e in Rows("Store")) r.Applications.Add(new(Text(e, "Name"), Text(e, "Publisher"), Text(e, "Version"), null, Text(e, "InstallLocation"), "Microsoft Store / MSIX", $"Usuário atual; framework={Text(e, "IsFramework")}; não removível={Text(e, "NonRemovable")}. Tamanho não publicado pela consulta."));
        foreach (var e in Rows("Features")) r.Features.Add(new(Text(e, "Caption") + " (" + Text(e, "Name") + ")", Number(e, "InstallState") switch { 1 => "Ativado", 2 => "Desativado", 3 => "Ausente", _ => "Desconhecido" }, "Win32_OptionalFeature / CIM"));
        foreach (var e in Rows("Startup")) r.Diagnostics.Add(new("Aplicativo na inicialização", Text(e, "Location"), null, null,
            Text(e, "Name") + " — registro de inicialização; estado habilitado/impacto não inferidos",
            "Revisar inicialização pode reduzir trabalho no login; não libera espaço de instalação.",
            "Comando, argumentos e conta não são coletados. Cobertura CIM não inclui todos os mecanismos; revisar nas Configurações.",
            "Win32_StartupCommand / CIM"));
        if (!Rows("Startup").Any()) r.Diagnostics.Add(new("Aplicativos na inicialização", "Configurações → Aplicativos → Inicialização", null, null,
            Rows("Errors").Select(x => x.ToString()).FirstOrDefault(x => x.StartsWith("Inicializacao:")) ?? "Nenhuma entrada reportada; cobertura CIM limitada.",
            "Impacto em login e desempenho; economia de disco não comprovada.", "Nenhuma entrada de Registro é alterada.", "Win32_StartupCommand / CIM"));
        foreach (var e in Rows("Pagefiles")) r.Diagnostics.Add(new("Arquivo de paginação", Text(e, "Name"), Number(e, "Bytes"), null, $"Uso atual {Text(e, "CurrentUsage")} MiB; pico {Text(e, "PeakUsage")} MiB", "Reduzir pode comprometer memória virtual, aplicações e geração de dumps.", "Somente diagnóstico. Não desativar nem redimensionar nesta missão.", "Win32_PageFileUsage / CIM"));
        foreach (var e in Rows("ShadowStorage")) r.Diagnostics.Add(new("Cópias de sombra — espaço usado", Text(e, "Volume"), Number(e, "UsedSpace"), null, $"Alocado={Text(e, "AllocatedSpace")} B; limite={Text(e, "MaxSpace")} B", "Excluir snapshots compromete restauração e pode afetar backups.", "Somente diagnóstico; não excluir nem reduzir o limite.", "Win32_ShadowStorage / CIM"));
        var restore = Rows("RestorePoints").ToList();
        var errors = Rows("Errors").Select(x => x.ToString()).ToList(); r.Notices.AddRange(errors);
        r.Diagnostics.Add(new("Pontos de restauração", "Proteção do Sistema", null, errors.Any(e => e.StartsWith("Restauracao:")) ? null : restore.Count, errors.Any(e => e.StartsWith("Restauracao:")) ? "Inconclusivo: " + errors.First(e => e.StartsWith("Restauracao:")) : restore.Count + " pontos reportados", "Pontos de restauração não possuem tamanho individual confiável nesta consulta.", "Somente diagnóstico. Espaço é compartilhado com a área de cópias de sombra; não somar novamente.", "SystemRestore / CIM root/default"));
        r.Diagnostics.Add(new("Otimização de Entrega — API", "Cache gerenciado pelo serviço", root.TryGetProperty("Delivery", out var delivery) && delivery.ValueKind == JsonValueKind.Object ? Number(delivery, "CacheSizeBytes") : null, null, errors.FirstOrDefault(e => e.StartsWith("Otimizacao")) ?? "Consultado; tamanho disponível quando publicado pelo provedor", "Limpeza oficial poderá exigir novos downloads.", "Somente diagnóstico; nenhuma chamada Delete-DeliveryOptimizationCache.", "Get-DeliveryOptimizationPerfSnap / CacheSizeBytes"));
        r.Diagnostics.Add(new("Armazenamento reservado", "Reserva do Windows para atualizações", null, null, root.TryGetProperty("Reserved", out var reserved) && reserved.ValueKind == JsonValueKind.Object ? Text(reserved, "ReservedStorageState") : "Inconclusivo: consulta pode exigir administrador", "Desativar altera a disponibilidade de espaço para atualizações; o estado não informa um tamanho recuperável.", "Não alterar reserva. O comando oficial pode exigir elevação; nenhuma elevação automática.", "Get-WindowsReservedStorageState"));
        r.Diagnostics.Add(new("Disponibilidade de hibernação e Inicialização Rápida", "Energia do sistema", null, null, string.Join(Environment.NewLine, Rows("Power").Select(x => x.ToString())), "Disponibilidade depende de hardware, drivers, políticas e hibernação.", "Consulta /a somente leitura; nenhuma alteração de energia.", "powercfg.exe /a"));
        r.Diagnostics.Add(new("Componentes Windows — análise oficial", "Armazenamento de componentes", null, null, root.TryGetProperty("ComponentStore", out var component) && component.ValueKind == JsonValueKind.Object ? Text(component, "Report") : "Inconclusivo: análise oficial indisponível", "O relatório oficial distingue tamanho real, compartilhamento e pacotes recuperáveis; a soma lógica do WinSxS não substitui esse relatório.", "Somente /AnalyzeComponentStore, quando o processo já possui token administrativo. Não há elevação, StartComponentCleanup ou ResetBase.", "DISM /Online /Cleanup-Image /AnalyzeComponentStore"));
        if (!Rows("Pagefiles").Any()) r.Diagnostics.Add(new("Arquivo de paginação", "Configuração do sistema", null, null, errors.FirstOrDefault(e => e.StartsWith("Paginacao:")) ?? "Nenhum arquivo reportado; não equivale a espaço zero comprovado", "Afeta memória virtual e dumps.", "Somente diagnóstico.", "Win32_PageFileUsage / CIM"));
        if (!Rows("ShadowStorage").Any()) r.Diagnostics.Add(new("Cópias de sombra", "Área de snapshots", null, null, errors.FirstOrDefault(e => e.StartsWith("Copias")) ?? "Nenhuma área reportada; não inferir ausência de backups", "Pode afetar restauração e backups.", "Somente diagnóstico; acesso ao provedor pode exigir administrador.", "Win32_ShadowStorage / CIM"));
    }
    static void AddUnavailableOfficialRows(MaintenanceReport r)
    { foreach (var name in new[] { "Arquivo de paginação", "Cópias de sombra", "Pontos de restauração", "Armazenamento reservado", "Otimização de Entrega — API" }) r.Diagnostics.Add(new(name, "Provedor oficial", null, null, "Inconclusivo: consulta indisponível", "Impactos devem ser avaliados antes de qualquer alteração.", "Sem elevação automática, sem mudança de configuração.", "Provedor Windows")); }
    [StructLayout(LayoutKind.Sequential, Pack = 8)] struct RecycleInfo { public uint Size; public long Bytes, Items; }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHQueryRecycleBinW")] static extern int SHQueryRecycleBin(string? root, ref RecycleInfo info);
}


