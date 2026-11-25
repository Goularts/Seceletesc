# CLAUDE.md

@AGENTS.md
@docs/project-context.md
@docs/business-rules/invariants.md

## Específico do Claude Code

Este arquivo não repete regras. As regras ficam em `AGENTS.md` e `docs/`; em caso de divergência, eles prevalecem.

- Detalhes das regras do chatbot: `docs/business-rules/operacao-chatbot.md` e `governanca-conhecimento.md`. Qualidade de desenvolvimento: `docs/desenvolvimento/qualidade.md`.
- `.claude/settings.json` pede confirmação para editar `docs/business-rules/**` (mudança de comportamento do chatbot) e nega leitura de arquivos de segredo. O resto é autonomia técnica.
- Comandos entregues ao usuário devem funcionar no CMD do Windows ou dizer explicitamente que são para PowerShell.
- Subagentes do projeto: `.claude/agents/`. Coordenação: Ruflo (MCP `claude-flow`, escopo do projeto).
- Revisão independente é feita pelo CI. Um subagente do mesmo modelo não conta como independente.
