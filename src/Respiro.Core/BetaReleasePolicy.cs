namespace Respiro.Core;

public enum BetaOperation { StartupChange, NormalClose, MsiUninstall }
public sealed record BetaMechanismReadiness(string Mechanism, string State, string EvidenceNeeded);

/// <summary>Build-time release decision. Reports, environment variables and UI selection cannot unlock it.</summary>
public static class BetaReleasePolicy
{
    public const string CandidateId = "Respiro Beta — candidata diagnóstica";
    public static bool Allows(BetaOperation operation) => false;
    public static string Reason(BetaOperation operation) => operation switch
    {
        BetaOperation.StartupChange => "Somente diagnóstico nesta candidata. Alteração e reposição de inicialização aguardam validação completa em VM descartável.",
        BetaOperation.NormalClose => "Somente diagnóstico nesta candidata. Fechamento de aplicativos aguarda validação final em convidado isolado.",
        BetaOperation.MsiUninstall => "Somente diagnóstico nesta candidata. Instalação, desinstalação, cancelamento e permissões MSI aguardam validação em VM.",
        _ => "Operação não aprovada para esta candidata."
    };
    public static void Demand(BetaOperation operation)
    {
        if (!Allows(operation)) throw new IOException(Reason(operation));
    }
    public static IReadOnlyList<BetaMechanismReadiness> Mechanisms { get; } = new[]
    {
        new BetaMechanismReadiness("Análise e inventários de leitura", "Validado para Beta — base existente", "Empacotamento em instalação limpa continua pendente."),
        new BetaMechanismReadiness("Alterar/repor inicialização", "Disponível somente para diagnóstico", "VM: round trip transacional, conflitos, permissões e interface."),
        new BetaMechanismReadiness("Solicitar fechamento", "Disponível somente para diagnóstico", "VM: fechar, recusar, cancelar, já encerrado e sem resposta."),
        new BetaMechanismReadiness("Desinstalação MSI", "Disponível somente para diagnóstico", "VM: instalação, identidade, remoção, cancelamento, falhas e elevação."),
        new BetaMechanismReadiness("Windows 10", "Bloqueado para homologação de lançamento", "Convidado e edição suportada não disponíveis.")
    };
}
