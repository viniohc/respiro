using System.Collections.Generic;
namespace Respiro.Core;

public enum Nature { Protegido, PotencialmenteRegeneravel, Desconhecido }
public record Classification(string Category, Nature Nature, string Reason);
public record DiskEntry(string Path, bool Directory, FileAttributes Attributes, long Length, DateTime Modified);
public record Allocation(long? Bytes, string? Identity, uint Links=1);
public record FileRow(string Path, long Bytes, long? Occupied, string Category, Nature Nature, string Reason, DateTime Modified)
{
    public string Size => Sizes.Format(Bytes);
    public string OccupiedText => Occupied is long n ? Sizes.Format(n) : "Inconclusivo";
    public string Protection => Nature == Nature.Protegido ? "Protegido" : Nature == Nature.PotencialmenteRegeneravel ? "Possivelmente regenerável" : "Desconhecido";
}
public class FolderRow
{
    public string Path { get; set; } = "";
    public string? Parent { get; set; }
    public long Bytes { get; set; }
    public long Files { get; set; }
    public long KnownOccupied { get; set; }
    public long UnknownOccupied { get; set; }
    public bool Partial { get; set; }
    public string Size => Sizes.Format(Bytes);
    public string State => Partial ? "Parcial" : "Concluída";
}
public record AggregateRow(string Name, long Bytes, long Count)
{
    public string Size => Sizes.Format(Bytes);
}
public record ScanIssue(string Path, string Reason);
public record ScanProgress(long Files, long Folders, long Bytes, string Current);
public class ScanResult
{
    public string Root { get; set; } = "";
    public DateTime Started { get; set; } = DateTime.Now;
    public DateTime Finished { get; set; }
    public long Files { get; set; }
    public long FolderCount { get; set; }
    public long Bytes { get; set; }
    public long KnownOccupied { get; set; }
    public long UnknownOccupied { get; set; }
    public long SkippedLinks { get; set; }
    public long DuplicateLinks { get; set; }
    public long IssueCount { get; set; }
    public bool Cancelled { get; set; }
    public bool FolderLimitReached { get; set; }
    public List<FileRow> LargestFiles { get; set; } = [];
    public List<FileRow> Candidates { get; set; } = [];
    public List<FileRow> Protected { get; set; } = [];
    public List<FolderRow> Folders { get; set; } = [];
    public List<AggregateRow> Categories { get; set; } = [];
    public List<AggregateRow> Extensions { get; set; } = [];
    public List<ScanIssue> Findings { get; set; } = [];
    public List<ScanIssue> Issues { get; set; } = [];
    public bool Partial => Cancelled || IssueCount > 0 || SkippedLinks > 0;
}
public static class Sizes
{
    public static string Format(long n) { string[] units = ["B", "KiB", "MiB", "GiB", "TiB"]; double v=n; int i=0; while(v>=1024 && i<4){v/=1024;i++;} return $"{v:N1} {units[i]}"; }
}
public interface IReadOnlyFileSystem
{
    DiskEntry Stat(string path);
    IEnumerable<DiskEntry> Enumerate(string path);
    bool Exists(string path);
    Allocation Allocated(string path);
    string ProbeUse(string path);
}

