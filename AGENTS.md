# Instruções para Agentes

Antes de qualquer alteração neste repositório, consulte integralmente:

1. `docs/project-context.md`
2. `docs/business-rules/invariants.md`

Esses documentos são a fonte de contexto e das regras obrigatórias já definidas para o projeto.

## Regras de trabalho

- Não invente requisitos, fluxos, permissões, critérios ou integrações que ainda não tenham sido definidos.
- Identifique e registre decisões pendentes separadamente; solicite definição quando elas forem necessárias para avançar.
- Preserve as invariantes de regras de negócio em toda alteração.
- Não permita que a IA aprove ou publique conhecimento reutilizável.
- Não permita que a IA envie mensagens ao cliente durante atendimento sob controle humano.
- Preserve o isolamento obrigatório de dados por tenant.
- Ao implementar regras de negócio em etapa autorizada, proteja-as com testes automatizados e submeta-as à revisão independente e aos controles de repositório aplicáveis.
- Nesta etapa de preparação, não implemente a aplicação e não instale dependências sem aprovação explícita do usuário.
