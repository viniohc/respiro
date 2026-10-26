# Segurança e limitações

## Diagnóstico, simulação e operação

A análise e os inventários consultam arquivos e APIs. A simulação revalida elegibilidade sem remover itens. A limpeza é um fluxo separado com seleção manual, prévia, autorização específica e confirmação. Nenhuma varredura concede autorização de exclusão.

Uma pasta chamada Temp, target, build, node_modules ou cache não prova que seu conteúdo pode ser removido. Formatos desconhecidos, arquivos em uso, alterados, protegidos, inacessíveis ou com dependências inconclusivas devem ser preservados. Há verificações de links, junctions, integridade e identidade; não se presume espaço físico recuperável apenas pelo tamanho lógico.

Os adaptadores diretos aceitam somente subconjuntos comprovados. Miniaturas reconhecidas, minidumps elegíveis e itens individuais da Lixeira não representam autorização para apagar suas pastas inteiras. A confirmação de Lixeira tem aviso individual; não há promessa de recuperação de uma exclusão permanente.

Arquivos de atualização, componentes do Windows, instalações anteriores, caches de shaders e outras categorias sem mecanismo direto aprovado usam diagnóstico e/ou manutenção nativa. Abrir uma ferramenta oficial não produz um recibo de limpeza realizada.

## Proteções preservadas

- Sem seleção ou limpeza automática de arquivos pessoais.
- Sem exclusão de projetos, worktrees, bancos, credenciais, volumes Docker/WSL, SDKs necessários ou dados de agentes.
- Evidências E2.5 e os marcadores .limpapc-protect e LimpaPC.TEMP/1 continuam protegidos.
- Sem alteração automática de configurações, elevação administrativa, políticas corporativas ou mecanismos de segurança.
- Confirmação, revalidação e registro são requisitos das operações compatíveis.
- Tamanhos desconhecidos e diagnósticos incompletos devem continuar explícitos.
- Dados legados do LimpaPC são preservados; não há migração destrutiva automática.

## Otimização: somente diagnóstico

A política compilada BetaReleasePolicy mantém bloqueadas alteração/restauração de inicialização, solicitação de fechamento e desinstalação MSI. As restrições existem no núcleo e na interface. Relatórios, seleções e variáveis de ambiente não as desbloqueiam.

Faltam provas em VM descartável para Run, conflitos, permissões, recibos, processos que recusam fechamento ou não respondem, instalação/remoção MSI, cancelamentos e elevação. Não se usam aplicativos reais ou entradas reais do usuário como substituto de fixtures.

Desativar inicialização, fechar um processo e desinstalar um programa têm efeitos diferentes. Fechar um programa não impede sua inicialização futura e não comprova recuperação de espaço em disco.

## Dados e privacidade

Preferências, histórico e recibos são locais. Instalações legadas podem continuar usando diretórios de dados LimpaPC; os diretórios Respiro e LimpaPC existentes não são apagados nem mesclados automaticamente. Exportações e logs podem incluir nomes de arquivos e caminhos: revise-os antes de compartilhar.

Não envie dumps, hives, históricos, diagnósticos privados, credenciais ou inventários pessoais em issues. Ao relatar um erro, use dados fictícios e remova informações identificáveis. Algumas provas opcionais de cache npm podem fazer consultas HTTPS anônimas ao registro público; exigem opção explícita no fluxo correspondente.

## Distribuição

Nesta etapa, o repositório contém fontes e assets, sem executáveis ou instaladores. A publicação dos fontes não significa aprovação da Beta para distribuição binária. Execução sem SDK, DPI, teclado, temas e acessibilidade em Windows limpo permanecem pendentes. Windows 10 não está homologado.
