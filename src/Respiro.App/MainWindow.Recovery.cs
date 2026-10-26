using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Respiro.Core;
using Microsoft.Win32;
namespace Respiro.App;
public partial class MainWindow
{
    RecoveryReport? recovery;
    IReadOnlyList<StorageAlert> recoveryAlerts = [];
    async void InvestigatePriorities(object sender,RoutedEventArgs e)=>await StartRecovery(false);
    async void InvestigateStorage(object sender,RoutedEventArgs e)=>await StartRecovery(true);
    async Task StartRecovery(bool wholeDisk)
    {
        if(cancellation!=null)return;
        Navigate(Page.Maintenance);WindowsMaintenanceTabs.SelectedItem=RecoveryTab;
        cancellation=new();CancelButton.IsEnabled=true;AnalyzeButton.IsEnabled=WindowsDiagnoseButton.IsEnabled=InvestigateStorageButton.IsEnabled=InvestigatePrioritiesButton.IsEnabled=RecoveryPublicNpmApproval.IsEnabled=false;
        BeginOperation("Investigando armazenamento — somente leitura e simulação");
        try
        {
            bool online=RecoveryPublicNpmApproval.IsChecked==true||Environment.GetCommandLineArgs().Contains("--verify-public-npm");
            var progress=new Progress<string>(s=>OperationPath.Text=s);
            recovery=await Task.Run(()=>new RecoveryInvestigationService(fs).Investigate(wholeDisk,progress,cancellation.Token,online));
            recoveryAlerts=StorageAlertEvaluator.Evaluate(recovery,alertSettings,recovery.Cancelled ? null : recovery.VolumeFree,new DateTimeOffset(recovery.Finished));
            ShowRecovery();
            Log("Investigação de recuperação",$"Confirmado={recovery.ConfirmedBytes}; revisão={recovery.PendingReviewBytes}; nenhum arquivo removido");
        }
        catch(OperationCanceledException){RecoverySummary.Text="Investigação cancelada. Nenhuma limpeza foi executada; a última investigação concluída permanece disponível.";}
        catch(Exception ex){RecoverySummary.Text="Investigação inconclusiva: "+ex.Message;}
        finally{cancellation.Dispose();cancellation=null;CancelButton.IsEnabled=false;AnalyzeButton.IsEnabled=WindowsDiagnoseButton.IsEnabled=InvestigateStorageButton.IsEnabled=InvestigatePrioritiesButton.IsEnabled=RecoveryPublicNpmApproval.IsEnabled=true;EndOperation();Status.Text=RecoverySummary.Text;UpdateDisk();}
    }
    void ShowRecovery()
    {
        if(recovery==null)return;
        RecoveryGrid.ItemsSource=RecoveryGrowthOrder.IsChecked==true ? recovery.Groups.OrderByDescending(g=>g.GrowthBytes).ThenBy(g=>g.Path).ToList() : recovery.Groups;RecoveryGrid.UnselectAll();RecoveryGrid.SelectedIndex=-1;
        RecoverySummary.Text=$"Identificado: {Sizes.Format(recovery.IdentifiedPhysicalBytes)} físicos conhecidos · Recuperável confirmado em simulação: {Sizes.Format(recovery.ConfirmedBytes)} · Pendente de revisão: {Sizes.Format(recovery.PendingReviewBytes)} · Protegido/inconclusivo: {Sizes.Format(recovery.ProtectedBytes)} ({recovery.ProtectedFileCount:N0} arquivos). Grupos sem sobreposição; medições parciais são limites inferiores. Nenhuma remoção.";
        if(recovery.Cancelled)RecoverySummary.Text="CANCELADA — resultado parcial. "+RecoverySummary.Text;
        RecoverySummary.Text+=" "+string.Join(" ",recoveryAlerts.Select(a=>a.Message));
        RecoveryDetail.Text=string.Join(Environment.NewLine,recovery.Notices);
    }
    void RecoveryGrowthChanged(object sender, RoutedEventArgs e)
    {
        if (RecoveryGrid != null && recovery != null) ShowRecovery();
    }
    void RecoverySelected(object sender,SelectionChangedEventArgs e)
    {
        if(RecoveryGrid.SelectedItem is not RecoveryGroup group)return;
        RecoveryDetail.Text=$"{group.Path}\n{group.Classification}\nMotivo: {group.Scope.Reason}\nVerificações: {group.Checks}\nProcessos: {group.ProcessEvidence}\nImpacto: {group.Scope.Impact}\nProcedimento: {group.Scope.Procedure}\nCrescimento: {group.Growth}\nÚltima gravação observada: {group.LastActivity}; não prova ausência de uso. Arquivos protegidos/inconclusivos={group.ProtectedFileCount}; falhas={group.Issues}; links ignorados={group.SkippedLinks}; alocação desconhecida={group.UnknownAllocation}; hard links observados={group.SharedFiles}.";
    }
    void SaveRecovery(string path)
    {
        if(recovery==null)return;path=Path.GetFullPath(path);Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllText(path,JsonSerializer.Serialize(recovery,new JsonSerializerOptions{WriteIndented=true}));
    }
    void ExportRecovery(object sender,RoutedEventArgs e)
    {
        if(recovery==null){RecoverySummary.Text="Execute a investigação antes de exportar.";return;}
        var dialog=new SaveFileDialog{Filter="Relatório JSON|*.json",FileName="respiro-recuperacao.json"};if(dialog.ShowDialog()==true){try{SaveRecovery(dialog.FileName);Log("Exportação de recuperação",dialog.FileName);}catch(Exception ex){RecoverySummary.Text="Falha de exportação: "+ex.Message;}}
    }
    void LoadRecoveryForReview(string path)
    {
        if(new FileInfo(path).Length>32*1024*1024)throw new IOException("Relatório excede o limite de revisão.");
        recoveryAlerts=[];
        recovery=JsonSerializer.Deserialize<RecoveryReport>(File.ReadAllText(path))??throw new IOException("Relatório inválido.");Navigate(Page.Maintenance);WindowsMaintenanceTabs.SelectedItem=RecoveryTab;ShowRecovery();Status.Text="Relatório histórico carregado para revisão; valores precisam ser revalidados antes de qualquer operação.";
    }
}