# Contexto do Projeto

## Objetivo

Preparar os requisitos para uma plataforma de atendimento inteligente por WhatsApp, baseada em C#/.NET, PostgreSQL, WhatsApp Cloud API e um motor de IA.

Nesta etapa, o escopo é exclusivamente de preparação do ambiente e consolidação de requisitos. A aplicação não deve ser implementada e dependências não devem ser instaladas.

## Decisões estabelecidas

1. O atendimento será conversacional e natural, sem menus ou URA.
2. A IA responderá autonomamente enquanto tiver informações e autorização suficientes.
3. Ao identificar um impasse, a IA transferirá o controle para supervisão humana por API.
4. A supervisão humana poderá responder pelo mesmo número de WhatsApp ou fornecer orientação interna ao agente.
5. Durante o atendimento humano, a IA não poderá enviar mensagens ao cliente.
6. Todo o histórico deverá ser estruturado, auditável e persistente.
7. A IA poderá sugerir novos conhecimentos e classificações, mas não poderá aprová-los nem publicá-los.
8. Somente humanos autorizados poderão aprovar conhecimento reutilizável.
9. Conhecimentos terão escopo, classificação, validade, versionamento e permissões.
10. Informações comerciais, preços, negociações, exceções e propostas fora do escopo exigirão controles específicos.
11. A plataforma começará com uma empresa, mas a arquitetura será preparada para multi-tenant.
12. O isolamento de dados por tenant é obrigatório.
13. Ruflo coordenará agentes de desenvolvimento, testes, segurança e revisão de regras de negócio.
14. Regras de negócio deverão ser protegidas por testes automatizados, revisão independente e controles de repositório.

## Limites desta etapa

- Não implementar a aplicação.
- Não instalar dependências.
- Não definir comportamentos, papéis, fluxos ou tecnologias além do que foi decidido.
- Aguardar aprovação explícita antes de avançar para qualquer etapa posterior.

## Decisões pendentes

Os itens abaixo ainda não foram definidos e não devem ser presumidos:

- Critérios que determinam se a IA possui informação e autorização suficientes para responder.
- Critérios de identificação de impasse e protocolo de transferência para supervisão humana.
- Contratos, autenticação e demais detalhes da API de supervisão.
- Papéis humanos, permissões e critérios de autorização para aprovar conhecimentos.
- Taxonomia de escopos, classificações, validades e permissões de conhecimento.
- Controles específicos para informações comerciais, preços, negociações, exceções e propostas fora do escopo.
- Modelo de tenants, estratégia técnica de isolamento e regras de acesso entre tenants.
- Modelo estruturado de histórico, política de auditoria e retenção.
- Fluxos de resposta humana e de orientação interna ao agente.
- Integrações e responsabilidades detalhadas entre WhatsApp Cloud API, motor de IA, PostgreSQL e Ruflo.
- Estratégia concreta de testes, revisão independente e controles de repositório.
