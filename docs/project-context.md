# Contexto do Projeto

## Objetivo

Plataforma de atendimento inteligente por WhatsApp (Seceletesc), em C#/.NET e PostgreSQL, com WhatsApp Cloud API, motor de IA desacoplado, API e painel de supervisão humana, histórico estruturado e governança de conhecimento. Começa com uma empresa e é preparada para multi-tenant.

**O objetivo:** autonomia no desenvolvimento e controle rigoroso do comportamento do chatbot ao atender clientes reais. O projeto pode evoluir tecnicamente; o chatbot não pode inventar, vazar informações, extrapolar autorizações ou responder fora do contexto.

## Organização das regras

| Parte | Documento | Controla |
|---|---|---|
| A. Operação do chatbot | `docs/business-rules/operacao-chatbot.md` | o que o chatbot diz, que dados usa, quando responde e quando transfere |
| B. Conhecimento | `docs/business-rules/governanca-conhecimento.md` | aprovação humana para publicar conhecimento reutilizável |
| C. Desenvolvimento | `docs/desenvolvimento/qualidade.md` | Ruflo coordenando desenvolvimento, revisão, testes e segurança, com autonomia técnica |

Resumo numerado das regras A e B: `docs/business-rules/invariants.md`.

**Aprovação humana** é exigida para conhecimento e comportamento do chatbot em produção, **não** para mudanças no software.

## Decisões estabelecidas

1. Atendimento conversacional e natural, sem menus ou URA.
2. A IA responde autonomamente só com informação e autorização suficientes.
3. Sem informação, autorização ou segurança contextual, a automação é interrompida e um humano é acionado pela API de supervisão.
4. O humano responde pelo mesmo número de WhatsApp ou orienta internamente a IA.
5. Durante o controle humano, a IA não envia mensagens; retoma só após liberação explícita, com contexto atualizado. Isso vale também após orientação interna.
6. O backend tem autoridade final sobre o envio, com controles determinísticos. Resposta bloqueada não é regenerada para contornar a política; vai para supervisão, salvo quando a política permitir pedir esclarecimento.
7. Aviso de transferência ao cliente é permitido quando pertinente, sem prometer prazo ou disponibilidade não confirmados.
8. Histórico estruturado, auditável e persistente, separado da base de conhecimento publicada.
9. A IA sugere conhecimento; só humano autorizado aprova, edita, rejeita, restringe ou revoga. Nada é promovido automaticamente.
10. Classificações: PUB, INT, TMP, CLI, NEG, EXC, RES. REV é status de revisão, não classificação. Classificação, escopo, validade, autorização e status são propriedades independentes.
11. Escopo de conhecimento: empresa, cliente, proposta ou atendimento.
12. EXC só é reutilizada dentro do escopo expressamente autorizado. PUB pode ter validade indefinida, mas permite revisão e revogação.
13. Isolamento obrigatório entre empresas (tenants) e entre clientes.
14. Painel de supervisão com as funções listadas em `governanca-conhecimento.md`, inclusive controle dos assuntos que o agente responde sozinho e dos que exigem humano.
15. O Ruflo coordena agentes de desenvolvimento, revisão, testes e segurança. Os agentes têm autonomia técnica; o Ruflo não substitui os controles do backend.
16. Regras A e B protegidas por testes automatizados (inclusive adversariais, verificando a decisão do backend), CI e controles de repositório.
17. Repositório público no GitHub (`Goularts/Seceletesc`); CI no GitHub Actions. Sem aprovação manual de PR nem conta separada para agentes neste momento.

## Etapa atual

Preparação. Antes de implementar funcionalidades, apresentar ao Victor um **plano técnico consolidado**. O que precisa de confirmação está em `docs/desenvolvimento/qualidade.md`, seção 2.

## Decisões pendentes (funcionais)

Não presumir. Decisões puramente técnicas (estrutura da solução, estratégia de isolamento, integrações, testes) cabem à equipe de desenvolvimento e entram no plano técnico.

- Papéis humanos: quem é "humano autorizado" e quem pode aprovar cada classificação.
- Critérios concretos de "informação suficiente", pertinência e proporcionalidade.
- Quais assuntos permitem pedir esclarecimento em vez de transferir.
- Quais fontes, além do conhecimento aprovado, são autorizadas (ex.: sistemas da empresa).
- Escopo de atendimento autorizado inicial (assuntos da primeira empresa).
- Política de retenção, auditoria e LGPD.
- Autenticação e usuários da API e do painel de supervisão.
- Orientação interna como fonte: ao retomar, a IA pode usar a orientação do humano como fonte autorizada naquele atendimento? (Ela não é conhecimento aprovado nem reutilizável.)
- Regeneração após bloqueio: é sempre proibida, ou é permitida uma nova tentativa controlada? Até decidir, vale: bloqueio → supervisão.
- Botões e listas interativas do WhatsApp: são permitidos ou contam como "menu" (invariante 1)?
- Fora da janela de 24h do WhatsApp só é possível enviar templates aprovados pela Meta: quais mensagens (aviso, resposta humana, retomada) podem usar template?
- Provedor do motor de IA: enviar dados de clientes a um provedor externo é exposição de dados e depende da política de LGPD.
