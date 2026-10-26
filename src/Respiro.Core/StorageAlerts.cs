using System.Text;
using System.Text.Json;
namespace Respiro.Core;
public sealed record StorageAlert(string Kind,string Path,string Message,long? ObservedBytes=null,double? ObservedHours=null);
public sealed record StorageAlertSettings(bool Enabled=true,long MinimumFreeBytes=10L*1024*1024*1024,long GrowthBytes=2L*1024*1024*1024,int PeriodHours=24)
{
    static string PathName=>Path.Combine(RespiroDataPaths.Root,"alerts.json");
    public void Validate(){if(MinimumFreeBytes<1024*1024||MinimumFreeBytes>1024L*1024*1024*1024||GrowthBytes<1024*1024||GrowthBytes>1024L*1024*1024*1024||PeriodHours<1||PeriodHours>2160)throw new IOException("Limites inválidos: 1 MiB–1 TiB e período de 1–2.160 horas.");}
    public static StorageAlertSettings Load()=>Read(PathName);
    internal static StorageAlertSettings Read(string path)
    {
        if(!File.Exists(path))return new();
        using var file=LockedFile.Open(path);if(file.Stamp.Bytes>16384)throw new IOException("Configuração de alertas excede 16 KiB.");
        var value=JsonSerializer.Deserialize<StorageAlertSettings>(file.ReadAt(0,(int)file.Stamp.Bytes))??throw new IOException("Configuração inválida");value.Validate();return value;
    }
    public void Save()=>Write(PathName);
    internal void Write(string path)
    {
        Validate();var bytes=JsonSerializer.SerializeToUtf8Bytes(this);Directory.CreateDirectory(Path.GetDirectoryName(path)!);using var file=LockedFile.Open(path,write:true,create:!File.Exists(path));RandomAccess.Write(file.Handle,bytes,0);RandomAccess.SetLength(file.Handle,bytes.Length);RandomAccess.FlushToDisk(file.Handle);
    }
}
public static class StorageAlertEvaluator
{
    internal sealed record Sample(DateTime At,string Path,string ScopeHash,long PhysicalBytes,bool Complete);
    public static List<StorageAlert> EvaluateFree(StorageAlertSettings settings,long? free)
    {
        settings.Validate();if(!settings.Enabled)return [];
        if(free==null||free<0)return [new("Inconclusivo","C:\\","Espaço livre não pôde ser consultado; nenhuma ação será executada.")];
        return free<settings.MinimumFreeBytes?[new("Espaço livre","C:\\",$"C: com {Sizes.Format(free.Value)} livres, abaixo do limite de {Sizes.Format(settings.MinimumFreeBytes)}. Somente alerta; nenhuma limpeza.",free)]:[];
    }
    public static string? BuildMarginWarning(long? free) => free is long bytes && bytes >= 0 && bytes < 12L*1024*1024*1024
        ? "Margem operacional insuficiente: builds exigem 12 GiB livres (10 GiB de reserva e 2 GiB de margem). Nenhuma limpeza automática."
        : null;
    public static List<StorageAlert> Evaluate(RecoveryReport current,StorageAlertSettings settings,long? free,DateTimeOffset now)
    {
        var path=Path.Combine(RespiroDataPaths.Root,"growth.jsonl");
        string text="";
        try
        {
            if(settings.Enabled&&File.Exists(path)) { using var file=LockedFile.Open(path); text=RecoveryHistoryReader.Read(file); }
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException)
        {
            var freeAlerts=EvaluateFree(settings,free);
            freeAlerts.Add(new("Histórico","", "Histórico indisponível: "+ex.Message+" O alerta de espaço livre continua independente."));
            return freeAlerts;
        }
        return EvaluateHistory(current,settings,free,now,text);
    }
    internal static List<StorageAlert> EvaluateHistory(RecoveryReport current,StorageAlertSettings settings,long? free,DateTimeOffset now,string jsonl)
    {
        var alerts=EvaluateFree(settings,free);if(!settings.Enabled)return alerts;
        var samples=RecoveryHistoryReader.Parse(jsonl);
        var cutoff=now.UtcDateTime.AddHours(-settings.PeriodHours);
        var oldest=cutoff.AddHours(-settings.PeriodHours);
        foreach(var g in current.Groups)
        {
            if(current.Cancelled||g.Partial||g.UnknownAllocation>0||g.PhysicalBytes<0)continue;
            // Filter scope before choosing the newest sample; incompatible samples cannot hide valid history.
            var old=RecoveryHistoryReader.Previous(samples,current,g,cutoff.AddTicks(1),oldest);
            if(old==null)continue;
            long delta=g.PhysicalBytes-old.PhysicalBytes;
            if(delta>=settings.GrowthBytes)alerts.Add(new("Crescimento",g.Path,
                $"{g.Origin}: +{Sizes.Format(delta)} em {(now.UtcDateTime-old.At).TotalHours:N1} h observadas. Revisar produtor e retenção; nenhuma limpeza automática.",
                delta,(now.UtcDateTime-old.At).TotalHours));
        }
        if(!alerts.Any(a=>a.Kind=="Crescimento"))alerts.Add(new("Histórico", "", "Nenhum crescimento acima do limite foi comprovado no período. Ausência de amostra completa/comparável não significa ausência de crescimento."));
        return alerts.OrderByDescending(a=>a.Kind=="Espaço livre").ThenByDescending(a=>a.ObservedBytes??-1).ToList();
    }
}
