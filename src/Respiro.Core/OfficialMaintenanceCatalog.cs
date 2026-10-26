namespace Respiro.Core;

public sealed record OfficialMaintenanceOption(string Id, string Name, string Effect, string Review,
    string Consequences, string Target, string Source)
{
    public string Estimate => "Não estimado; verificar no mecanismo oficial";
    public string Destination => Target.StartsWith("ms-settings:", StringComparison.Ordinal) ? "Configurações do Windows" : "Documentação oficial";
    public string Purpose => Id switch
    {
        "startup" => "Escolher quais programas abrem ao entrar no Windows.",
        "sense" => "Definir uma política do Windows para evitar acúmulo de arquivos.",
        "power" => "Guardar sua sessão e permitir Inicialização Rápida.",
        "reserved" => "Separar espaço para atualizações e manutenção.",
        "restore" => "Recuperar o sistema e versões anteriores quando disponíveis.",
        "features" => "Ativar ou remover recursos opcionais usados por alguns programas.",
        "pagefile" => "Ajudar a memória e o diagnóstico de falhas.",
        "apps" => "Revisar aplicativos instalados e o procedimento oficial de desinstalação.",
        _ => "Revisar arquivos de manutenção e os critérios apresentados pelo Windows."
    };
    public string SpaceImpact => Id == "startup" ? "Desativar inicialização não libera espaço de instalação." :
        Id == "sense" ? "A economia depende da política e dos arquivos elegíveis, ainda não medida." :
        "Pode ocupar espaço; consulte o diagnóstico. Ocupação não é autorização de recuperação.";
    public string Reversibility => Id switch
    {
        "startup" => "Em geral é possível reativar a entrada no Windows.",
        "sense" => "A política pode ser alterada; arquivos já excluídos não voltam por desativá-la.",
        "power" => "Pode ser reativada pelo mecanismo oficial, voltando a ocupar espaço.",
        "features" => "Pode exigir download e reinicialização para reinstalar; depende da edição.",
        "apps" => "Reinstalação depende do fornecedor; dados locais podem não ser recuperáveis.",
        "pagefile" => "A configuração pode ser revista, mas uma falha ocorrida não é desfeita.",
        "reserved" => "Depende da versão, políticas e estado das atualizações; revisar documentação.",
        _ => "Arquivos, pontos de restauração e opções de retorno removidos podem ser irrecuperáveis."
    };
    public override string ToString() => Name;
}

/// <summary>Fixed destinations only. No command lines, scripts, cleanup flags or imported targets.</summary>
public static class OfficialMaintenanceCatalog
{
    const string SettingsSource = "https://learn.microsoft.com/en-us/windows/apps/develop/launch/launch-settings";
    public static IReadOnlyList<OfficialMaintenanceOption> Options { get; } = Array.AsReadOnly(new[]
    {
        new OfficialMaintenanceOption("temporary", "Temporários, DirectX e relatórios de erros", "Armazenamento",
            "Abrir Armazenamento → Arquivos temporários. Revisar cada categoria e o tamanho calculado pelo Windows; não selecionar Downloads ou Lixeira por padrão.",
            "Shaders e miniaturas exigirão regeneração; relatórios removidos deixam de servir ao diagnóstico. Instalações e arquivos em uso dependem da elegibilidade do Windows.",
            "ms-settings:storagesense", SettingsSource),
        new OfficialMaintenanceOption("updates", "Resíduos de instalações e Windows Update", "Armazenamento e recuperação",
            "Revisar Recomendações de limpeza. A elegibilidade depende da versão do Windows, de atualizações pendentes e da possibilidade de voltar à instalação anterior.",
            "Remover instalações anteriores pode impedir retorno à versão anterior. Não excluir SoftwareDistribution, Installer ou WinSxS manualmente.",
            "ms-settings:storagerecommendations", SettingsSource),
        new OfficialMaintenanceOption("sense", "Sensor de Armazenamento", "Prevenção e retenção",
            "Revisar frequência, Lixeira, Downloads e conteúdo da nuvem antes de habilitar ou alterar a política.",
            "O mecanismo oficial pode excluir conteúdo automaticamente conforme a política escolhida. Abrir a página não habilita o Sensor.",
            "ms-settings:storagepolicies", SettingsSource),
        new OfficialMaintenanceOption("startup", "Aplicativos na inicialização", "Desempenho no login",
            "Cruzar o inventário CIM com a lista oficial; nem toda entrada reportada pelo CIM está habilitada. Revisar a finalidade de cada aplicativo.",
            "Desabilitar inicialização não desinstala nem libera o espaço do aplicativo. Não desabilitar proteção, backup ou sincronização sem entender sua função.",
            "ms-settings:startupapps", SettingsSource),
        new OfficialMaintenanceOption("apps", "Programas e aplicativos nativos opcionais", "Armazenamento e recursos",
            "Revisar uso, dados locais, dependências e possibilidade de reinstalação. Tamanhos do instalador são estimativas; nenhum aplicativo é marcado como dispensável.",
            "Desinstalação pode remover dados, integrações ou dependências compartilhadas; decisão e confirmação pertencem ao usuário.",
            "ms-settings:appsfeatures", SettingsSource),
        new OfficialMaintenanceOption("features", "Recursos opcionais do Windows", "Recursos e armazenamento",
            "Conferir o inventário, dependências e necessidade dos recursos. A disponibilidade da página depende da versão e das políticas do Windows.",
            "Remoção pode exigir reinicialização ou download para reinstalar. Recursos usados por desenvolvimento ou virtualização precisam ser preservados.",
            "ms-settings:optionalfeatures", SettingsSource),
        new OfficialMaintenanceOption("components", "Armazenamento de componentes", "Armazenamento e recuperação",
            "Consultar o relatório de AnalyzeComponentStore já disponível no diagnóstico quando autorizado pelo token atual; ler as alternativas oficiais antes de agir.",
            "ResetBase impede a desinstalação de atualizações incorporadas. Nenhum comando de limpeza é executado pelo Respiro.",
            "https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/clean-up-the-winsxs-folder", "Microsoft Learn — Component Store"),
        new OfficialMaintenanceOption("power", "Hibernação e Inicialização Rápida", "Energia e armazenamento",
            "Conferir hiberfil.sys, powercfg /a e Inicialização Rápida no diagnóstico. Ler a documentação antes de decidir.",
            "Desativar hibernação também afeta Inicialização Rápida e suspensão híbrida. O tamanho observado não é economia aprovada.",
            "https://learn.microsoft.com/en-us/windows-hardware/design/device-experiences/powercfg-command-line-options", "Microsoft Learn — powercfg"),
        new OfficialMaintenanceOption("pagefile", "Arquivo de paginação", "Estabilidade e memória virtual",
            "Consultar uso e pico no diagnóstico. Avaliar memória e requisitos de dumps; não tratar pagefile.sys como cache.",
            "Reduzir ou desativar pode causar falhas de alocação e impedir dumps. Nenhuma economia é contabilizada.",
            "https://learn.microsoft.com/en-us/troubleshoot/windows-client/performance/introduction-to-the-page-file", "Microsoft Learn — Page file"),
        new OfficialMaintenanceOption("restore", "Restauração e cópias de sombra", "Recuperação e backups",
            "Revisar a medição de cópias de sombra e pontos de restauração; espaço compartilhado não pode ser somado duas vezes.",
            "Excluir cópias pode impedir restauração e prejudicar backups. Nenhuma exclusão ou redução de limite é oferecida.",
            "https://learn.microsoft.com/en-us/windows-server/storage/file-server/volume-shadow-copy-service", "Microsoft Learn — VSS"),
        new OfficialMaintenanceOption("reserved", "Armazenamento reservado", "Atualizações e disponibilidade",
            "Conferir o estado da reserva no diagnóstico. Estado habilitado/desabilitado não informa espaço recuperável.",
            "Alterações afetam o espaço disponível para manutenção do Windows. A reserva de segurança do build do Respiro permanece independente.",
            "https://learn.microsoft.com/en-us/windows-hardware/manufacture/desktop/dism-storage-reserve", "Microsoft Learn — Reserved storage")
    });
    public static OfficialMaintenanceOption Resolve(string id) =>
        Options.FirstOrDefault(o => o.Id == id) ?? throw new IOException("Destino de manutenção não reconhecido.");
}
