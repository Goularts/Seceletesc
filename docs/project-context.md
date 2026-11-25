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
6. O backend tem autoridade final sobre o envio, com controles determinísticos. Resposta bloqueada nunca é regenerada; vai para supervisão, salvo quando a política permitir pedir esclarecimento.
6a. Orientação interna não vira fonte direta: entra como sugestão (REV) e só é usada após avaliação e classificação humana.
6b. Após bloqueio, nunca há nova geração; aguarda-se a supervisão.
6c. Sem elementos interativos (botões, listas) nas mensagens ao cliente.
6d. Sem templates pela API. Fora da janela de 24h, a conversa é marcada no painel (status + resumo interno) e retomada por humano pelo celular.
6e. Telefone e texto das conversas ficam no histórico e a IA lê o texto. Dados sensíveis enviados pelo cliente são criptografados e nunca vão à IA.
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
18. Escopo inicial (2026-11-25): o chatbot funciona como **secretária de atendimento**, não como agente comercial. Não negocia nem trata propostas; conhecimento com escopo "proposta" continua fora de uso.
19. Dados protegidos (2026-11-25, revista na autorização da F3): tratamento proporcional ao risco, igual para respostas automáticas e humanas pela API. Dado comum (e-mail, CEP) só quando necessário, autorizado e pertinente: na resposta automática, só se constar numa fonte aprovada citada; na humana, o humano autoriza; o histórico guarda só o texto mascarado e o valor cifrado. Dado de identificação (CPF etc.) exige finalidade definida e acesso restrito; sem esse mecanismo, não sai em mensagem. Dado financeiro (cartão completo) nunca é reproduzido nem guardado em claro. Dados de outros clientes ou empresas: proibidos. A proteção cobre entrada, armazenamento, logs e auditoria. Detalhes em `operacao-chatbot.md`, seção 3a.
20. Termos de compromisso (2026-11-25): a lista padrão fica como está. Ela só restringe (na dúvida, transfere) e cada empresa a ajusta pela política, na API de supervisão. A calibragem é feita no piloto, olhando os bloqueios por motivo.
21. Papéis humanos (2026-11-25, F3): atendente, supervisor e administrador, conforme `governanca-conhecimento.md`, seção 3a. Toda autorização é validada pelo backend; o modelo não concede permissões.
22. Permissões comerciais (2026-11-25, F3): negociações, descontos, propostas especiais e exceções comerciais não são oferecidos autonomamente. Só o administrador aprova NEG, EXC, RES e CLI e altera a política que os libera; NEG e EXC exigem cliente ou atendimento definido e validade. Nenhuma funcionalidade de negociação é implementada.
23. Pendência de retomada pelo celular (2026-11-25, F3): ativa até encerramento explícito por supervisor ou administrador, com responsável, data e motivo; liberar a conversa não a encerra; enquanto ativa, a IA não afirma que o assunto foi resolvido.

## Etapa atual

Preparação. Antes de implementar funcionalidades, apresentar ao Victor um **plano técnico consolidado**. O que precisa de confirmação está em `docs/desenvolvimento/qualidade.md`, seção 2.

## Decisões pendentes (funcionais)

Não presumir. Decisões puramente técnicas (estrutura da solução, estratégia de isolamento, integrações, testes) cabem à equipe de desenvolvimento e entram no plano técnico.

- Critérios concretos de "informação suficiente", pertinência e proporcionalidade.
- Quais assuntos permitem pedir esclarecimento em vez de transferir.
- Quais fontes, além do conhecimento aprovado, são autorizadas (ex.: sistemas da empresa).
- Escopo de atendimento autorizado inicial (assuntos da primeira empresa).
- Política de retenção, auditoria e LGPD.
- Autenticação e usuários da API e do painel de supervisão. (Em desenvolvimento: chave de API por usuário, com papel, criada por linha de comando. Produção: a definir.)
- "Conversas autorizadas" do atendente: hoje ele vê todas as conversas da empresa. Haverá atribuição por atendente?
- Retomada após liberação: a IA responde mensagens recebidas durante o controle humano? (Padrão no plano: não.)
- Mensagens que não são texto (mídia, áudio, reação, edição): a IA trata? (Padrão no plano: vão para supervisão.)
- Esclarecimento: em quais assuntos é permitido e como é gerado? (Padrão no plano: desligado.)
- Cliente identificado pelo telefone: como tratar número compartilhado ou reciclado?
- Categorias de dados protegidos (criptografados e ocultos da IA). Padrão técnico inicial: CPF, CNPJ, RG, cartão de pagamento, e-mail, CEP/endereço. O Victor pode ampliar.
- Quem pode ver dados protegidos no painel.
- Termos de compromisso padrão como piso: hoje o administrador pode reduzir a lista pela política. Deve haver um mínimo que nem ele remove?
- Finalidade para dados de identificação (CPF etc.): qual mecanismo definirá a finalidade e quem pode ver o dado no painel? Até lá, esses dados não saem em mensagem.
