# Desenvolvimento e validação

## Ambiente

Use Windows com SDK .NET 10.0.402 e PowerShell. global.json fixa a família do SDK e permite atualização de patch. Os projetos usam net10.0-windows. O aplicativo usa WPF e a fixture operacional usa Windows Forms. NuGet.Config não habilita fontes externas; não há PackageReference nesta versão.

Execute build.ps1 na raiz. O parâmetro opcional -DotNetPath aceita um dotnet.exe já instalado. O script verifica 12 GiB livres no C: antes das etapas, conserva a reserva mínima de 10 GiB, usa compilação sem servidor compartilhado e executa o runner local. O limite não reserva espaço contra gravações de outros processos. Não contorne a guarda executando comandos diretos quando o disco estiver insuficiente.

O parâmetro -Publish gera apenas uma publicação local em artifacts/app e pode substituir essa pasta de saída. Para preservar builds locais anteriores, não utilize esse parâmetro sobre uma publicação que deseja manter. Não foi usado nesta publicação dos fontes.

Para executar um build, use src/Respiro.App/bin/Release/net10.0-windows/Respiro.App.exe. Um pacote framework-dependent requer o Desktop Runtime .NET 10 x64 instalado; a configuração de publicação usa EnvironmentVariable;Global para busca do runtime. Não se distribui o SDK com o aplicativo.

## Evidências existentes

- Compilação Release do aplicativo aprovada, sem avisos/erros.
- 298 testes locais: 294 existentes e 4 regressões de bloqueio de lançamento.
- 16 definições operacionais separadas: 14 preservadas e 2 novos casos de Run transacional próprio e processo já encerrado.
- Os 308 resultados históricos não significam 308 testes reexecutados nesta candidata.
- A execução operacional em VM e a execução sem SDK continuam pendentes.

A suíte local usa arquivos artificiais e fixtures isoladas; não autoriza limpeza de dados reais. O projeto Respiro.OperationalHost é exclusivamente um executável descartável de teste. Nunca o inclua no pacote público do aplicativo.

## Atestação privada e clones

tests/Respiro.Tests/Resources/GuestPreparationHost.sha256 identifica a máquina que prepara o kit operacional e é um arquivo privado, ignorado pelo Git. A referência ao recurso é condicional à sua existência. Clones compilam a suíte local sem esse arquivo. A ausência do recurso provoca recusa explícita no caminho operacional, mantendo o comportamento de segurança fechado.

Preparar um novo kit exige registrar privadamente a identidade do host, recompilar o runner, gerar manifestos/hashes e autorizar um convidado descartável diferente do host. Esses arquivos e os scripts locais de missão não integram esta publicação. Não use um identificador fictício para habilitar testes nem execute operações nativas no host. A confirmação de VM não substitui a autorização humana e o isolamento adequado.

## Homologação restante

| Área | Evidência necessária |
|---|---|
| Inicialização | Valor Run próprio, desativação, restauração, conflito, permissões e recibos em VM |
| Processos | Fechamento normal, recusa, cancelamento, encerramento prévio e janela sem resposta |
| MSI | Fixture própria: instalação, identidade inequívoca, desinstalação, cancelamento e permissões |
| Distribuição | Windows 11 limpo, Desktop Runtime sem SDK, dependências e abertura |
| Interface | Temas, teclado, foco, tamanho de janela, DPI e acessibilidade básica |
| Windows 10 | Convidado de edição/build suportados, ainda indisponível na validação anterior |

A política compilada do produto continua bloqueando as três operações avançadas. Um kit de teste bem-sucedido não altera o produto automaticamente. A aprovação exige revisão das evidências, integração ponta a ponta em ambiente autorizado e nova compilação/regressão.

## Política do repositório

A raiz usa uma lista explícita de arquivos públicos. Permanecem locais: artifacts, reports, tools de missões, .sdk, .cli, caches, backups, logs, identificadores e documentação de auditorias pessoais. Apenas docs/public é documentação pública.

Antes de um commit, revise git diff --cached e git diff --cached --name-only. Não force a inclusão de arquivos ignorados. Preserve os avisos OFL da Inter e não inclua chaves, bancos, hives, dados de agentes ou executáveis.

Duas fixtures de persistência usam subdiretórios temporários exclusivos, fora dos ancestrais Git protegidos: configuração de alertas e journal de Otimização. Essa adaptação permite testar clones sem enfraquecer a proteção de projetos. Não há mudança no código de produção.
