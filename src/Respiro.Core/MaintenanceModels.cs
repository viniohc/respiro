namespace Respiro.Core;
public record DiagnosticRow(string Category, string Location, long? Bytes, long? Count, string Status, string Impact, string Restrictions, string Source)
{
    public string Size => Bytes is long n ? Sizes.Format(n) : "Não disponível";
    public string CountText => Count?.ToString("N0") ?? "—";
}
public record InstalledApp(string Name, string Publisher, string Version, long? Bytes, string Location, string Kind, string Notes)
{
    public string Size => Bytes is long n ? Sizes.Format(n) : "Não informado";
    public string? ProductCode { get; init; }
    public string? UninstallHive { get; init; }
    public string? UninstallView { get; init; }
    public string? UninstallKey { get; init; }
    public bool WindowsInstaller { get; init; }
    public bool SystemComponent { get; init; }
    public string? InstallerDate { get; init; }
    public string InstallationDateText => ApplicationInventoryView.InstallationDate(InstallerDate);
}
public record OptionalFeature(string Name, string State, string Source);
public sealed class MaintenanceReport
{
    public DateTime Started { get; init; } = DateTime.Now;
    public DateTime Finished { get; set; }
    public List<DiagnosticRow> Diagnostics { get; } = [];
    public List<InstalledApp> Applications { get; } = [];
    public List<OptionalFeature> Features { get; } = [];
    public List<string> Notices { get; } = [];
    public bool Cancelled { get; set; }
}
