using System.IO;
using System.Windows;
using Respiro.Core;
namespace Respiro.App;
public partial class MainWindow
{
    void RecordStorageTimeline()
    {
        if (result == null) return;
        try
        {
            var sample = StorageTimeline.Observe(result);
            var previous = RespiroDataPaths.ExistingRoots.SelectMany(root => StorageTimeline.Read(Path.Combine(root, StorageTimeline.FileName))).ToArray();
            HomeSpaceTimeline.Text = StorageTimeline.Compare(sample, previous).Explanation +
                " Para recuperar espaço, revise Limpeza rápida e os maiores arquivos; não remova dados pessoais por idade.";
            try { StorageTimeline.Append(Path.Combine(DataFolder, StorageTimeline.FileName), sample); }
            catch (Exception ex) { HomeSpaceTimeline.Text += " Nova amostra não gravada: " + ex.Message; }
        }
        catch (Exception ex) { HomeSpaceTimeline.Text = "Comparação histórica indisponível: " + ex.Message + " Os totais da análise continuam disponíveis."; }
    }
    void LoadStorageTimeline()
    {
        var samples = new List<StorageTimelineSample>();
        var notes = new List<string>();
        foreach (string root in RespiroDataPaths.ExistingRoots)
        {
            try { samples.AddRange(StorageTimeline.Read(Path.Combine(root, StorageTimeline.FileName))); }
            catch (Exception ex) { notes.Add(Path.GetFileName(root) + ": " + ex.Message); }
        }
        SpaceTimelineGrid.ItemsSource = samples.OrderByDescending(s => s.At).ToArray();
        SpaceTimelineSummary.Text = "Espaço livre após análises; crescimento de pastas exige amostras completas do mesmo escopo. A origem das gravações pode continuar desconhecida. " +
            "Limite: 128 amostras / 1 MiB por histórico, sem exclusão automática. " + string.Join(" · ", notes);
    }
}
