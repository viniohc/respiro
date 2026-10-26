using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Respiro.Core;
using Microsoft.Win32;
namespace Respiro.App;
public partial class MainWindow
{
    DockerDeepReport? dockerDiagnostic;
    StorageAlertSettings alertSettings=new();
    readonly DispatcherTimer alertTimer=new(){Interval=TimeSpan.FromMinutes(1)};
    void InitDockerDiagnostics()
    {
        try{alertSettings=StorageAlertSettings.Load();}catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or JsonException){StorageAlertStatus.Text="Configuração inconclusiva; limites padrão em memória. Arquivo não alterado.";}
        StorageAlertsEnabled.IsChecked=alertSettings.Enabled;AlertFreeGiB.Text=(alertSettings.MinimumFreeBytes/(double)(1L<<30)).ToString("0.###");AlertGrowthGiB.Text=(alertSettings.GrowthBytes/(double)(1L<<30)).ToString("0.###");AlertPeriodHours.Text=alertSettings.PeriodHours.ToString();
        alertTimer.Tick+=(_,_)=>CheckStorageAlerts();alertTimer.Start();Closed+=(_,_)=>alertTimer.Stop();CheckStorageAlerts();
    }
    void CheckStorageAlerts()
    {
        long? free=null;try{free=new DriveInfo("C:\\").AvailableFreeSpace;}catch(IOException){}
        var alerts=StorageAlertEvaluator.EvaluateFree(alertSettings,free);string message=alerts.Count>0?string.Join("\n",alerts.Select(a=>a.Message)):alertSettings.Enabled?"Espaço livre acima do limite. Crescimento depende de amostras comparáveis.":"Alertas desativados pelo usuário.";
        if(StorageAlertEvaluator.BuildMarginWarning(free) is string margin)message+="\n"+margin;
        StorageAlertStatus.Text=message;if(alerts.Count>0&&cancellation==null)Status.Text=message;
    }
    void SaveStorageAlerts(object sender,RoutedEventArgs e)
    {
        try
        {
            if(!double.TryParse(AlertFreeGiB.Text,out double free)||!double.TryParse(AlertGrowthGiB.Text,out double growth)||!double.IsFinite(free)||!double.IsFinite(growth)||free<0||growth<0||free>1024||growth>1024||!int.TryParse(AlertPeriodHours.Text,out int hours))throw new IOException("Informe valores numéricos válidos; use vírgula decimal.");
            var value=new StorageAlertSettings(StorageAlertsEnabled.IsChecked==true,checked((long)(free*(1L<<30))),checked((long)(growth*(1L<<30))),hours);value.Save();alertSettings=value;CheckStorageAlerts();StorageAlertStatus.Text="Limites salvos. "+StorageAlertStatus.Text;Log("Alertas","Limites de diagnóstico atualizados; nenhuma limpeza agendada.");
        }
        catch(Exception ex)when(ex is IOException or UnauthorizedAccessException or JsonException or OverflowException){StorageAlertStatus.Text="Não foi possível salvar: "+ex.Message;}
    }
    async void DockerDiagnose(object sender,RoutedEventArgs e)=>await StartDockerDiagnostic();
    async Task StartDockerDiagnostic()
    {
        if(cancellation!=null)return;Navigate(Page.Maintenance);WindowsMaintenanceTabs.SelectedItem=DevelopmentTab;cancellation=new();CancelButton.IsEnabled=true;DockerDiagnoseButton.IsEnabled=AnalyzeButton.IsEnabled=WindowsDiagnoseButton.IsEnabled=InvestigateStorageButton.IsEnabled=InvestigatePrioritiesButton.IsEnabled=false;
        BeginOperation("Diagnóstico Docker/WSL — somente leitura");
        try
        {
            var progress=new Progress<string>(s=>OperationPath.Text=s);dockerDiagnostic=await Task.Run(()=>new DockerDeepDiagnostic().Collect(progress,cancellation.Token));ShowDockerDiagnostic();Log("Docker/WSL",$"Discos conhecidos={dockerDiagnostic.DockerKnownPhysicalBytes}; confirmado no C:=0; daemon={dockerDiagnostic.DaemonAvailable}; nenhuma operação.");
        }
        catch(OperationCanceledException){DockerSummary.Text="Diagnóstico cancelado; nenhum serviço ou arquivo foi alterado. Último diagnóstico concluído preservado.";}
        catch(Exception ex){DockerSummary.Text="Diagnóstico inconclusivo: "+ex.Message;}
        finally{cancellation.Dispose();cancellation=null;CancelButton.IsEnabled=false;DockerDiagnoseButton.IsEnabled=AnalyzeButton.IsEnabled=WindowsDiagnoseButton.IsEnabled=InvestigateStorageButton.IsEnabled=InvestigatePrioritiesButton.IsEnabled=true;EndOperation();Status.Text=DockerSummary.Text;UpdateDisk();}
    }
    void ShowDockerDiagnostic()
    {
        if(dockerDiagnostic==null)return;var r=dockerDiagnostic;
        DockerDisksGrid.ItemsSource=r.Disks;DockerResourcesGrid.ItemsSource=r.Resources;DockerWslGrid.ItemsSource=r.Distributions;DockerFoldersGrid.ItemsSource=r.FolderInvestigation?.Groups;
        DockerDisksGrid.UnselectAll();DockerResourcesGrid.UnselectAll();DockerFoldersGrid.UnselectAll();
        DockerSummary.Text=$"Docker: {Sizes.Format(r.DockerKnownPhysicalBytes)} físicos conhecidos · {r.DaemonState}\nCache interno potencial: {(r.GuestUnusedBuildCacheBytes is long n?Sizes.Format(n):"inconclusivo")} · Livre interno: inconclusivo · Recuperável confirmado no C: 0 B. Nenhuma operação autorizada/executada.";
        DockerProjectsText.Text=r.ProjectHints.Count>0?string.Join("\n\n",r.ProjectHints):"Nenhum marcador nos limites consultados; dependências continuam inconclusivas. Compose, bancos e credenciais não foram lidos.";
        DockerDetails.Text=string.Join("\n\n",r.Notices);DockerAlertSummary.Text=string.Join(" · ",r.Alerts.Select(a=>a.Message));
    }
    void DockerResourceSelected(object sender,SelectionChangedEventArgs e){if(DockerResourcesGrid.SelectedItem is DockerResource r)DockerDetails.Text=$"{r.Kind}: {r.Name}\n{r.Classification}\n{r.Reason}\nDependências: {r.Dependencies}\nProcedimento: {r.Procedure}\n{r.Approval}";}
    void DockerDiskSelected(object sender,SelectionChangedEventArgs e){if(DockerDisksGrid.SelectedItem is DockerDisk d)DockerDetails.Text=$"{d.Path}\nFísico: {d.Physical}; lógico: {d.Logical}; {d.State}.\nConteúdo persistente protegido. Livre interno e economia NTFS não comprovados. Não excluir, montar, compactar ou parar ambientes nesta missão.";}
    void DockerFolderSelected(object sender,SelectionChangedEventArgs e){if(DockerFoldersGrid.SelectedItem is RecoveryGroup g)DockerDetails.Text=$"{g.Path}\n{g.Scope.Reason}\n{g.Checks}\n{g.ProcessEvidence}\n{g.Growth}\nProcedimento: {g.Scope.Procedure}\nNenhuma limpeza; restrições E2.5 preservadas.\n{dockerDiagnostic?.ArtifactProfiles.FirstOrDefault(p=>p.Path==g.Path)?.Summary}";}
    void DockerExport(object sender,RoutedEventArgs e)
    {
        if(dockerDiagnostic==null){DockerSummary.Text="Execute o diagnóstico primeiro.";return;}
        var dialog=new SaveFileDialog{Filter="Relatório JSON|*.json",FileName="respiro-docker.json"};if(dialog.ShowDialog()==true)try{DockerDeepDiagnostic.Save(dockerDiagnostic,dialog.FileName);}catch(Exception ex){DockerSummary.Text="Exportação não concluída: "+ex.Message;}
    }
    void LoadDockerReport(string path)
    {
        if(new FileInfo(path).Length>2*1024*1024)throw new IOException("Relatório excede limite de 2 MiB.");dockerDiagnostic=JsonSerializer.Deserialize<DockerDeepReport>(File.ReadAllText(path))??throw new IOException("Relatório inválido");Navigate(Page.Maintenance);WindowsMaintenanceTabs.SelectedItem=DevelopmentTab;ShowDockerDiagnostic();Status.Text="Diagnóstico histórico; não concede autorização de operação.";
    }
}