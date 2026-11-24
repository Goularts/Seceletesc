# Ambiente de desenvolvimento e proteções do repositório

Registro da preparação do ambiente (2026-11-24). Este documento não define regras de negócio; elas ficam em `docs/business-rules/`.

## Estado do ambiente

- Repositório em `C:\dev\Seceletesc` (clone do original em OneDrive, mesmo commit `a02d02b`). Sem remote.
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
- Nenhum hook do Ruflo foi instalado.

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
- A memória do Ruflo está em modo `memory` (não persistente). Ver pendência 4.

## Permissões do Claude Code (`.claude/settings.json`)

Validado em 2026-11-24 com `claude -p --permission-mode acceptEdits`:

| Ação | Resultado |
|---|---|
| Editar `AGENTS.md`, `CLAUDE.md`, `.claude/**`, `.mcp.json` | pede confirmação |
| Editar `docs/business-rules/**` | negado |
| Ler `.env`, `.env.*`, `secrets*` | negado |

Essas regras valem só para as ferramentas de edição e leitura do Claude Code. Um comando de shell ainda consegue alterar arquivos. A proteção efetiva precisa vir do repositório e do CI (abaixo).

## Agentes (`.claude/agents/`)

| Agente | Ferramentas | Papel |
|---|---|---|
| `arquiteto` | Read, Grep, Glob | propõe arquitetura; não edita |
| `desenvolvedor` | Read, Grep, Glob, Edit, Write, Bash | implementa em etapa autorizada |
| `testes` | Read, Grep, Glob, Edit, Write, Bash | testes de invariantes e regressão |
| `seguranca` | Read, Grep, Glob | revisão de segurança; não corrige |
| `guardiao-regras` | Read, Grep, Glob | confere mudança contra invariantes; bloqueia |

Os agentes somente leitura não executam `git diff`; quem os chama deve incluir o diff no pedido. O parecer de um agente não é revisão independente.

## Proteções propostas (ainda não aplicadas)

Revisão independente é feita por humano ou CI.

### 1. CODEOWNERS (`.github/CODEOWNERS`)

Depende das pendências 1 e 2. Rascunho:

```
/AGENTS.md              @<usuario-github>
/CLAUDE.md              @<usuario-github>
/.claude/               @<usuario-github>
/.mcp.json              @<usuario-github>
/docs/business-rules/   @<usuario-github>
/.github/               @<usuario-github>
```

### 2. Branch protection na branch principal

Depende de remote. Proposta: PR obrigatório, 1 aprovação de code owner, status checks do CI obrigatórios, sem push direto e sem force push, regras aplicadas também a administradores.

### 3. CI

Depende da pendência 3 (provedor) e da 7 (estrutura .NET). Esqueleto para GitHub Actions:

```yaml
name: ci
on: [pull_request]
jobs:
  invariantes:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
      - uses: actions/setup-dotnet@v4
        with: { dotnet-version: '<definir>' }
      - run: dotnet test --filter "Category=Invariante"
  caminhos-protegidos:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v4
        with: { fetch-depth: 0 }
      - name: Sinaliza mudança em caminho protegido
        run: |
          git diff --name-only origin/${{ github.base_ref }}...HEAD \
            | grep -E '^(AGENTS\.md|CLAUDE\.md|\.claude/|\.mcp\.json|docs/business-rules/)' \
            && echo "::warning::PR altera caminho protegido; exige aprovação do code owner" || true
```

O bloqueio de verdade vem do CODEOWNERS e da branch protection; o job só sinaliza.

### 4. Hook de pre-commit (local)

Bloqueia commit que toca caminho protegido, a menos que `SECELETESC_PERMITIR_PROTEGIDO=1` esteja definido. É só uma barreira local: `git commit --no-verify` contorna.

```sh
#!/bin/sh
# .githooks/pre-commit  (ativar com: git config core.hooksPath .githooks)
protegidos='^(AGENTS\.md|CLAUDE\.md|\.claude/|\.mcp\.json|docs/business-rules/)'
alterados=$(git diff --cached --name-only | grep -E "$protegidos")
if [ -n "$alterados" ] && [ "$SECELETESC_PERMITIR_PROTEGIDO" != "1" ]; then
  echo "Commit bloqueado: caminhos protegidos alterados:"
  echo "$alterados"
  echo "Com autorização do Victor, rode no CMD: set SECELETESC_PERMITIR_PROTEGIDO=1"
  exit 1
fi
```

## Decisões pendentes de ambiente

1. Remote privado (GitHub?) para backup e branch protection. Hoje o repositório só existe localmente.
2. Usuários do GitHub no CODEOWNERS.
3. Provedor de CI.
4. Nível do Ruflo: manter mínimo? Habilitar memória persistente ou vetorial?
5. Manter ou remover configuração do Codex neste repositório (hoje não há nenhuma).
6. Terminal padrão (CMD ou PowerShell).
7. Estrutura da solução .NET.
8. Ativar o hook de pre-commit proposto.

As decisões pendentes de negócio estão em `docs/project-context.md`.
