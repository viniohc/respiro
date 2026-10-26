using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Respiro.Core;
namespace Respiro.App;

public partial class MainWindow
{
    void InitOfficialMaintenance() => OfficialOptionsGrid.ItemsSource = OfficialMaintenanceCatalog.Options;
    void OfficialOptionSelected(object sender, SelectionChangedEventArgs e)
    {
        if (OfficialOpenButton == null || OfficialOptionDetail == null) return;
        OfficialOpenButton.IsEnabled = OfficialOptionsGrid.SelectedItem is OfficialMaintenanceOption;
        if (OfficialOptionsGrid.SelectedItem is not OfficialMaintenanceOption item) return;
        OfficialOpenButton.Content = item.Destination == "Documentação oficial" ? "Ler orientação oficial…" : "Abrir página oficial…";
        OfficialOptionDetail.Text = $"{item.Name}\nPara que serve: {item.Purpose}\nEspaço: {item.SpaceImpact}\nReversão: {item.Reversibility}\nEfeito: {item.Effect}\n\nRevisão: {item.Review}\n\nConsequências: {item.Consequences}\n\nEstimativa: {item.Estimate}\nFonte: {item.Source}\nDestino: {item.Target}\n\nO Respiro apenas abre esse destino. Estimativa, seleção, confirmação e execução de uma limpeza oficial acontecem no Windows; voltar e investigar novamente para medir a diferença.";
    }
    void OpenOfficialOption(object sender, RoutedEventArgs e)
    {
        if (cancellation != null || OfficialOptionsGrid.SelectedItem is not OfficialMaintenanceOption selected) return;
        try
        {
            // Resolve from the fixed catalogue; never execute a target from a report or editable text.
            var item = OfficialMaintenanceCatalog.Resolve(selected.Id);
            Process.Start(new ProcessStartInfo(item.Target) { UseShellExecute = true });
            Log("Encaminhamento oficial", item.Id + "; destino aberto, nenhuma limpeza confirmada pelo Respiro");
            Status.Text = "Destino oficial aberto. Qualquer alteração exige sua decisão nessa ferramenta.";
        }
        catch (Exception ex) { Status.Text = "Não foi possível abrir o destino oficial: " + ex.Message; }
    }
}
