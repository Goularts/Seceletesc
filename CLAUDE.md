# CLAUDE.md

@AGENTS.md
@docs/project-context.md
@docs/business-rules/invariants.md

## Específico do Claude Code

Este arquivo não repete regras. As regras do projeto ficam em `AGENTS.md` e `docs/`; em caso de divergência, eles prevalecem.

- `AGENTS.md`, `CLAUDE.md`, `.claude/` e `docs/business-rules/**` são caminhos protegidos. Só altere com autorização explícita do Victor. O `.claude/settings.json` nega ou pede confirmação para essas edições.
- Comandos entregues ao usuário devem funcionar no CMD do Windows ou dizer explicitamente que são para PowerShell.
- Os subagentes do projeto ficam em `.claude/agents/`.
- Revisão independente é feita por humano ou CI. Um subagente do mesmo modelo, no mesmo repositório, não conta como independente.
