# Contexto do Projeto

## Objetivo

Plataforma de atendimento inteligente por WhatsApp (Seceletesc), em C#/.NET e PostgreSQL, com WhatsApp Cloud API, motor de IA desacoplado, API e painel de supervisão humana, histórico estruturado e governança de conhecimento. SaaS multi-tenant (decisão 34): cada empresa cliente conecta a própria conta do WhatsApp (WABA) e os próprios números, com a Seceletesc operando como Independent Tech Provider da Meta.

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
20. Termos de compromisso (2026-11-25): a lista padrão fica como está. Ela só restringe (na dúvida, transfere) e cada empresa a ajusta pela política, na API de supervisão. A calibragem é feita no piloto, olhando os bloqueios por motivo. *(Substituída em parte pela decisão 25: os termos padrão não podem ser removidos, só ampliados.)*
21. Papéis humanos (2026-11-25, F3): atendente, supervisor e administrador, conforme `governanca-conhecimento.md`, seção 3a. Toda autorização é validada pelo backend; o modelo não concede permissões.
22. Permissões comerciais (2026-11-25, F3): negociações, descontos, propostas especiais e exceções comerciais não são oferecidos autonomamente. Só o administrador aprova NEG, EXC, RES e CLI e altera a política que os libera; NEG e EXC exigem cliente ou atendimento definido e validade. Nenhuma funcionalidade de negociação é implementada.
23. Pendência de retomada pelo celular (2026-11-25, F3): ativa até encerramento explícito por supervisor ou administrador, com responsável, data e motivo; liberar a conversa não a encerra; enquanto ativa, a IA não afirma que o assunto foi resolvido.
24. Visibilidade (2026-11-25, pós-F3): menor privilégio. Atendente não vê conhecimento restrito, interno confidencial nem dados de clientes fora da sua autorização; supervisor respeita as permissões específicas. Detalhe em `governanca-conhecimento.md`, seção 3a.
25. Políticas fundamentais (2026-11-25, pós-F3): um piso de proibições que nem o administrador desativa (vazamento de dados, conhecimento não aprovado, compromissos comerciais não autorizados, respostas fora do escopo). A política só acrescenta restrições. Regras estruturadas, não só palavras proibidas. Detalhe em `operacao-chatbot.md`, seção 3.
26. Documentos pessoais (2026-11-25, pós-F3): CPF e equivalentes ficam bloqueados no MVP até existir fluxo seguro com finalidade, autorização, controle de acesso e retenção.
27. Atribuição de conversas (2026-11-25, pós-F3): atendente vê as conversas atribuídas a ele e a fila de atendimentos disponíveis; supervisor e administrador gerenciam a fila da empresa.
28. Validade (2026-11-25, pós-F3): obrigatória para conhecimento não público, com prazos máximos configuráveis por classificação na política. PUB pode ser indefinida, sempre sujeita a revisão e revogação.
29. Orientações e dados pessoais (2026-11-25, pós-F3): dado pessoal nunca é promovido a conhecimento; só e-mails institucionais listados na política são da empresa.
30. Segurança antes da exposição externa (2026-11-25, pós-F3): autenticação confiável, controle de identidade, autorização e HTTPS antes de disponibilizar a API fora do ambiente local. O banco não confia na identidade informada pela API.
31. WhatsApp (2026-11-25, pós-F3): não presumir coexistência (app + Cloud API); alternativa é o atendimento humano pelo painel ligado à API, sempre com o mesmo número. Nenhum número real conectado nem mensagem externa enviada sem autorização do Victor.
32. Decisões da F4 (2026-11-25, F4.5): sem coexistência, nada é enviado fora da janela até existir fluxo de templates autorizado (aguarda o cliente); INT e RES nunca divulgados automaticamente; atendente assume pela fila ou por atribuição autorizada; liberar não apaga histórico nem pendências externas; conhecimento aprovado é reavaliado pela nova política de validade, sem exclusão silenciosa; endereço institucional pode ser PUB aprovado, endereço de cliente é protegido; mensagens consecutivas podem ser agrupadas; fora de ordem são reconciliadas, ambiguidades vão à supervisão; atraso gera monitoramento e escalonamento com limites configuráveis; falha de entrega gera registro e supervisão; reações são registradas sem intervenção. Sem regras que gerem transferências desnecessárias.
33. Ambiente (2026-11-25, F4.5): nada contratado por enquanto; demonstração local e checklist de configuração (`docs/setup/GUIA-ADMINISTRADOR.md`). O Ruflo faz parte da estratégia e não deve ser removido.
34. SaaS multi-tenant como Independent Tech Provider (2026-11-27): cada empresa cliente tem a própria WABA, os próprios números e a própria credencial (business token), isolados por empresa. A verificação empresarial da Meta corre em paralelo e não bloqueia o desenvolvimento. (a) No início, só **operadores da plataforma** cadastram e ativam clientes; autocadastro fica para depois. (b) Coexistência com o WhatsApp Business App continua desligada, sem impedir a implementação futura. (c) Todo número fica com o envio **desligado** até autorização explícita de um operador. (d) Conexão da WABA, autorização operacional e ativação do envio são etapas separadas. (e) Envio com resultado incerto nunca é repetido; só há nova tentativa quando a repetição é comprovadamente segura. (f) Nenhuma conexão ou envio real durante o desenvolvimento. Plano em `docs/desenvolvimento/plano-tecnico.md`, seção 4.13.

## Etapa atual

Preparação. Antes de implementar funcionalidades, apresentar ao Victor um **plano técnico consolidado**. O que precisa de confirmação está em `docs/desenvolvimento/qualidade.md`, seção 2.

## Decisões pendentes (funcionais)

Não presumir. Decisões puramente técnicas (estrutura da solução, estratégia de isolamento, integrações, testes) cabem à equipe de desenvolvimento e entram no plano técnico.

- Critérios concretos de "informação suficiente", pertinência e proporcionalidade.
- Quais assuntos permitem pedir esclarecimento em vez de transferir.
- Quais fontes, além do conhecimento aprovado, são autorizadas (ex.: sistemas da empresa).
- Escopo de atendimento autorizado inicial (assuntos da primeira empresa).
- Política de retenção, auditoria e LGPD.
- Autenticação e usuários da API e do painel de supervisão. (Em desenvolvimento: chave de API por usuário, com papel, criada por linha de comando. Produção: a definir.) Inclui a **autenticação do operador da plataforma** (decisão 34a): hoje o operador age pela linha de comando com a conexão do dono e o nome registrado no histórico é declarado, não comprovado; pré-requisito antes de número real (M3).
- Retomada após liberação: a IA responde mensagens recebidas durante o controle humano? (Padrão no plano: não.)
- Endereço particular sem CEP (ex.: "Rua X, 100") não é detectado como dado protegido: entra como texto. Ampliar o detector para endereços? (limite registrado na F4.5)
- Mensagem que chega depois da liberação com horário anterior a ela: hoje é registrada e a IA não a trata (não pertence ao período da IA), sem acionar ninguém. Acionar a supervisão nesse caso? (F4.5)
- Mensagens que não são texto (mídia, áudio, edição): a IA trata? (Padrão no plano: vão para supervisão. Reações: decididas, só registro.)
- Esclarecimento: em quais assuntos é permitido e como é gerado? (Padrão no plano: desligado.)
- Cliente identificado pelo telefone: como tratar número compartilhado ou reciclado?
- Categorias de dados protegidos (criptografados e ocultos da IA). Padrão técnico inicial: CPF, CNPJ, RG, cartão de pagamento, e-mail, CEP/endereço. O Victor pode ampliar.
- Quem pode ver dados protegidos no painel.
- Empresa com vários números (M1): hoje a conversa é uma por cliente na empresa e a resposta (da IA ou humana) sai pelo número em que o cliente escreveu por último. Conversa por cliente ou por número? Por qual número a resposta sai? (revisão da M1, G1)
- Mensagem recebida em número sem autorização operacional, com envio desligado ou com a conta desconectada: hoje é registrada e tratada normalmente; a resposta é cancelada no envio e a conversa vai para supervisão. A IA deve processar essas mensagens? (revisão da M1, G2/S6; atenção à decisão 32 sobre transferências desnecessárias)
- Desconectar a conta (WABA) revoga também a autorização operacional dos números (padrão conservador adotado na M1): ao reconectar, a autorização é refeita. Manter? (revisão da M1, S4/G8)
- Finalidade para dados de identificação (CPF etc.): qual fluxo definirá finalidade, autorização, acesso e retenção? Até lá, bloqueados (decisão 26).
