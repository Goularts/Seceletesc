# Ambiente de desenvolvimento e proteções do repositório

Registro da preparação do ambiente (2026-11-24). Este documento não define regras de negócio; elas ficam em `docs/business-rules/`.

## Estado do ambiente

- Repositório em `C:\dev\Seceletesc` (clone do original em OneDrive; a pasta antiga foi excluída).
- Remote: `https://github.com/Goularts/Seceletesc`, **público**. Decisão do Victor: repositório público no GitHub Free, porque nele a branch protection é gratuita.
- Autor dos commits: e-mail noreply do GitHub, configurado só neste repositório. O histórico foi reescrito antes do primeiro push para não expor o e-mail pessoal; por isso o commit inicial passou de `a02d02b` para `c28f353`, com conteúdo idêntico.
- Não versionar nada sensível: o repositório é público. Segredos ficam fora do Git (ver `.gitignore`).
- Claude Code lê as regras via `CLAUDE.md`, que importa `AGENTS.md` e `docs/`.
- GitHub Copilot (VS Code e cloud agent) lê `AGENTS.md` nativamente; por isso não há `.github/copilot-instructions.md`.
- Ruflo `3.80.6`, só no escopo do projeto.

## Ruflo: como foi instalado

Comando executado em `C:\dev\Seceletesc`, depois de inspecionar o pacote e ensaiar numa cópia isolada:

```cmd
npx -y ruflo@3.80.6 init --minimal --no-global --no-mods --no-plugin-install --no-skills-sh --no-codex-detect --no-signup --no-telemetry-prompt
```

| Flag | Evita |
|---|---|
| `--no-global` | bloco escrito em `~/.claude/CLAUDE.md` |
| `--no-mods --no-plugin-install` | `claude plugin install` (plugins de usuário) |
| `--no-skills-sh` | `npx skills add ruvnet/ruflo` |
| `--no-codex-detect` | configuração automática do Codex |
| `--no-signup --no-telemetry-prompt` | cadastro Cognitum e pergunta de telemetria |

Ajustes feitos depois do `init`:

- `.claude/settings.json`: removidos o `allow` amplo (`Bash(npx claude-flow*)` sem versão fixa e `mcp__claude-flow__*`) e `CLAUDE_CODE_EXPERIMENTAL_AGENT_TEAMS`.
- Skills: mantida só `swarm-orchestration`; removidas `hooks-automation`, `pair-programming`, `skill-builder`, `swarm-advanced` e `verification-quality`.
- `CLAUDE_FLOW_AUTO_UPDATE=false`: evita checagem de atualização e escrita em `~/.claude-flow`.
- `RUFLO_FUNNEL=0`: desliga as promoções e eventos de marketing (`funnel.ruv.io`), que vêm ligados por padrão. A telemetria anônima já é desligada por padrão (depende de consentimento).
- `RUFLO_DAEMON_AUTOSTART=0`: qualquer comando do Ruflo, inclusive o `mcp start`, sobe por padrão um daemon residente com workers agendados (map, audit, optimize, testgaps, backup...). Desligado. Para usar, rode `ruflo daemon start` de forma explícita.
- Na instalação (2026-11-24), nenhum hook foi instalado e a memória ficou em modo não persistente. **Na F4.5 (2026-11-25)** o Ruflo passou a participar do fluxo por hooks do Claude Code que chamam scripts versionados (`ferramentas/portao/`), com memória persistente pela linha de comando (`.swarm/memory.db`). Ver `docs/desenvolvimento/portao.md`.

Arquivos do Ruflo no repositório: `.mcp.json`, `.claude-flow/config.yaml`, `.claude-flow/CAPABILITIES.md`, `.claude-flow/.gitignore`, `.claude/skills/swarm-orchestration/`. Estado de runtime fica fora do Git (ver `.gitignore`).

Ao rodar o Ruflo direto no CMD, fora do Claude Code, defina antes:

```cmd
set RUFLO_FUNNEL=0
set CLAUDE_FLOW_AUTO_UPDATE=false
set RUFLO_DAEMON_AUTOSTART=0
```

Limitações conhecidas:

- `ruflo@3.80.6` está fixado, mas as dependências de `@claude-flow/cli` usam faixas (`^`). O `npx` pode resolver versões diferentes com o tempo.
- O servidor MCP anuncia cerca de 394 ferramentas (~78 mil tokens de schema). O Claude Code carrega sob demanda, mas vale monitorar.
- A memória do servidor MCP aparece como `memory` na configuração, mas na prática (Windows) MCP e linha de comando gravam e leem o mesmo `.swarm/memory.db`, persistente (verificado na F4.5).

## Governança (revisada em 2026-11-24)

Por decisão do Victor, a aprovação humana vale para **conhecimento e comportamento do chatbot em produção**, não para o desenvolvimento. Os agentes têm autonomia técnica (ver `docs/desenvolvimento/qualidade.md`). Por isso foram descartados: aprovação manual de PR, conta GitHub separada para agentes, hook de pre-commit bloqueando caminhos e caminhos de desenvolvimento protegidos.

## Permissões do Claude Code (`.claude/settings.json`)

| Ação | Resultado |
|---|---|
| Editar `docs/business-rules/**` (regras do chatbot) | pede confirmação |
| Ler `.env`, `.env.*`, `secrets*` | negado |
| Demais edições | livres |

## CODEOWNERS

`.github/CODEOWNERS` cobre só `docs/business-rules/`. Sem branch protection, ele não bloqueia merge: apenas marca o Victor como revisor quando um PR muda regra do chatbot, para dar visibilidade.

## CI

GitHub Actions. Deve existir antes do primeiro merge de código da aplicação, rodando build, testes e testes de regras A/B (incluindo os adversariais). É a revisão independente do desenvolvimento. O workflow será definido no plano técnico, junto com a estrutura .NET.

## Agentes

Ver `docs/desenvolvimento/qualidade.md`. Os agentes somente leitura não executam `git diff`; quem os chama inclui o diff no pedido.

## Decisões pendentes de ambiente

1. Nível do Ruflo: manter mínimo ou habilitar memória persistente/vetorial (avaliar pelo piloto).
2. Configuração do Codex: não há nenhuma; o Codex lê `AGENTS.md` nativamente.
3. Terminal padrão: comandos seguem em CMD.

Piloto do Ruflo: `docs/setup/ruflo-piloto-qa.md`. Pendências funcionais: `docs/project-context.md`.
