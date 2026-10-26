namespace Respiro.Core;
public static class ApplicationInventoryView
{
    public static IEnumerable<InstalledApp> Filter(IEnumerable<InstalledApp> source, string search, int type) =>
        source.Where(a => (a.Name + " " + a.Publisher + " " + a.Kind).Contains(search.Trim(), StringComparison.OrdinalIgnoreCase) &&
            (type switch { 1 => a.Kind == "Tradicional", 2 => a.Kind.Contains("Store", StringComparison.OrdinalIgnoreCase), _ => true }));
    public static string InstallationDate(string? value) =>
        DateOnly.TryParseExact(value, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var date) ? date.ToString("dd/MM/yyyy") : "Não informada";
    public static string Related(InstalledApp selected, IEnumerable<InstalledApp> apps)
    {
        if (string.IsNullOrWhiteSpace(selected.Publisher) || selected.Publisher is "Não informado" or "Não informada" or "Unknown")
            return "Fabricante desconhecido: relação entre programas não comprovada.";
        var related = apps.Where(a => a != selected && a.Publisher.Equals(selected.Publisher, StringComparison.OrdinalIgnoreCase))
            .Select(a => a.Name).Distinct(StringComparer.OrdinalIgnoreCase).Order().Take(6).ToArray();
        return related.Length == 0 ? "Nenhum outro programa do mesmo fabricante foi encontrado nesta consulta." :
            "Mesmo fabricante: " + string.Join("; ", related) + ". É apenas uma pista, não prova de dependência nem recomendação de remoção.";
    }
}
