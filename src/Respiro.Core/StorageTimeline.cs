using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
namespace Respiro.Core;
public sealed record TimelineFolder(string Path, long Occupied);
public sealed record StorageTimelineSample(DateTime At, string Volume, string Serial, long Capacity, long Free,
    string Scope, bool Complete, IReadOnlyList<TimelineFolder> Folders)
{
    public string When => At.ToLocalTime().ToString("g");
    public string Available => Sizes.Format(Free);
}
public sealed record StorageTimelineComparison(StorageTimelineSample Current, StorageTimelineSample? Previous, IReadOnlyList<TimelineFolder> Growth)
{
    public string Explanation
    {
        get
        {
            if (Previous == null) return $"Hoje: {Sizes.Format(Current.Free)} livres. Ainda não existe análise anterior comparável deste volume.";
            long used = Previous.Free - Current.Free;
            string variation = used > 0 ? $"menos {Sizes.Format(used)} livres" : $"mais {Sizes.Format(-used)} livres";
            string folders = Growth.Count == 0 ? "Nenhuma pasta crescente foi comprovada nas amostras disponíveis." :
                "Pastas com crescimento observado (não some pais e filhos): " + string.Join("; ", Growth.Take(5).Select(g => g.Path + " +" + Sizes.Format(g.Occupied)));
            return $"Hoje: {Sizes.Format(Current.Free)} livres; antes: {Sizes.Format(Previous.Free)} em {Previous.When} ({variation}). " +
                folders + " A origem das gravações permanece desconhecida; horário ou crescimento de pasta não identifica o programa responsável.";
        }
    }
}
/// <summary>Own append-only bounded history. Never rotates or deletes user reports or existing samples.</summary>
public static class StorageTimeline
{
    internal const int ByteLimit = 1024 * 1024, SampleLimit = 128;
    public static string FileName => "space-timeline.jsonl";
    static bool Valid(StorageTimelineSample? s) => s != null && s.At != default &&
        !string.IsNullOrWhiteSpace(s.Volume) && !string.IsNullOrWhiteSpace(s.Scope) &&
        s.Serial != null && s.Serial.Length == 8 && s.Serial.All(Uri.IsHexDigit) &&
        s.Capacity > 0 && s.Capacity <= long.MaxValue / 2 && s.Free >= 0 && s.Free <= s.Capacity &&
        s.Folders != null && s.Folders.Count <= 20 &&
        s.Folders.All(f => f != null && !string.IsNullOrWhiteSpace(f.Path) && f.Occupied >= 0 && f.Occupied <= long.MaxValue / 2);
    internal static IReadOnlyList<StorageTimelineSample> Parse(string jsonl)
    {
        if (Encoding.UTF8.GetByteCount(jsonl) > ByteLimit) throw new IOException("Histórico de espaço excede 1 MiB.");
        var rows = new List<StorageTimelineSample>();
        foreach (string line in jsonl.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var sample = JsonSerializer.Deserialize<StorageTimelineSample>(line);
                if (sample is not null && Valid(sample)) rows.Add(sample with { At = sample.At.ToUniversalTime() });
            }
            catch (JsonException) { }
            if (rows.Count > SampleLimit) throw new IOException("Histórico de espaço excede 128 amostras.");
        }
        return rows;
    }
    public static IReadOnlyList<StorageTimelineSample> Read(string path)
    {
        if (!File.Exists(path)) return [];
        using var file = LockedFile.Open(path);
        if (file.Stamp.Bytes > ByteLimit) throw new IOException("Histórico de espaço excede 1 MiB.");
        return Parse(RecoveryHistoryReader.Read(file));
    }
    public static StorageTimelineComparison Compare(StorageTimelineSample current, IEnumerable<StorageTimelineSample> previous)
    {
        if (!Valid(current)) throw new IOException("Amostra de espaço inválida.");
        var old = previous.Where(Valid).Where(s => s.At.ToUniversalTime() < current.At.ToUniversalTime() &&
            s.Volume.Equals(current.Volume, StringComparison.OrdinalIgnoreCase) &&
            s.Serial.Equals(current.Serial, StringComparison.OrdinalIgnoreCase) && s.Capacity == current.Capacity)
            .OrderByDescending(s => s.At).FirstOrDefault();
        var growth = new List<TimelineFolder>();
        if (old is { Complete: true } && current.Complete && old.Scope.Equals(current.Scope, StringComparison.OrdinalIgnoreCase))
            foreach (var folder in current.Folders)
            {
                var prior = old.Folders.FirstOrDefault(f => f.Path.Equals(folder.Path, StringComparison.OrdinalIgnoreCase));
                if (prior != null && folder.Occupied > prior.Occupied) growth.Add(new(folder.Path, folder.Occupied - prior.Occupied));
            }
        return new(current, old, growth.OrderByDescending(g => g.Occupied).ToArray());
    }
    public static void Append(string path, StorageTimelineSample sample)
    {
        if (!Valid(sample)) throw new IOException("Amostra inválida; nada registrado.");
        byte[] line = JsonSerializer.SerializeToUtf8Bytes(sample).Concat(new byte[] { 10 }).ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = LockedFile.Open(path, write: true, create: !File.Exists(path));
        if (file.Stamp.Bytes > 0 && file.Stamp.Bytes <= ByteLimit && file.ReadAt(file.Stamp.Bytes - 1, 1)[0] != 10)
            line = new byte[] { 10 }.Concat(line).ToArray();
        if (file.Stamp.Bytes > ByteLimit - line.Length || Parse(RecoveryHistoryReader.Read(file)).Count >= SampleLimit)
            throw new IOException("Histórico cheio; arquive manualmente antes de registrar novas análises. Nenhuma amostra apagada.");
        RandomAccess.Write(file.Handle, line, file.Stamp.Bytes); RandomAccess.FlushToDisk(file.Handle);
    }
    public static StorageTimelineSample Observe(ScanResult result)
    {
        string volume = Path.GetPathRoot(Path.GetFullPath(result.Root))!;
        var drive = new DriveInfo(volume);
        if (drive.DriveType != DriveType.Fixed || !GetVolumeInformation(volume, IntPtr.Zero, 0, out uint serial, out _, out _, IntPtr.Zero, 0))
            throw new IOException("Identidade do volume indisponível; comparação não registrada.");
        bool complete = !result.Partial && !result.FolderLimitReached && result.UnknownOccupied == 0;
        var folders = result.Folders.Where(f => !f.Partial && f.UnknownOccupied == 0 &&
            !f.Path.Equals(result.Root, StringComparison.OrdinalIgnoreCase)).OrderByDescending(f => f.KnownOccupied).Take(20)
            .Select(f => new TimelineFolder(f.Path, f.KnownOccupied)).ToArray();
        return new(DateTime.UtcNow, volume, serial.ToString("X8"), drive.TotalSize, drive.AvailableFreeSpace, result.Root, complete, folders);
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetVolumeInformationW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetVolumeInformation(string root, IntPtr volumeName, uint nameSize, out uint serial,
        out uint maximumComponentLength, out uint flags, IntPtr fileSystem, uint fileSystemSize);
}
