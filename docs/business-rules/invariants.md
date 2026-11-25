# Invariantes de Regras de Negócio

Regras obrigatórias sobre o **comportamento do chatbot em produção** e sobre o **conhecimento** que ele usa. Detalhes em:

- A. `operacao-chatbot.md`: o que pode ser dito, quais dados podem ser usados, quando responder e quando transferir.
- B. `governanca-conhecimento.md`: aprovação humana para publicar conhecimento reutilizável.
- C. `docs/desenvolvimento/qualidade.md`: como o desenvolvimento garante que A e B sejam cumpridas.

Estas invariantes regulam o **produto**, não o processo de desenvolvimento. Mudanças técnicas no código não precisam de aprovação humana; mudanças que alterem ou ampliem o comportamento funcional do chatbot precisam.

## A. Operação do chatbot

1. O atendimento é conversacional natural, sem menus, URA ou elementos interativos (botões, listas de opções) nas mensagens ao cliente.
2. A IA só responde autonomamente com informação e autorização suficientes; caso contrário, pede esclarecimento (quando a política permitir) ou transfere para supervisão humana.
3. Em impasse, falta de informação, falta de autorização ou falta de segurança contextual, a automação é interrompida e a supervisão humana é acionada via API.
4. A supervisão humana pode responder ao cliente pelo mesmo número de WhatsApp ou fornecer orientação interna ao agente.
5. Enquanto um atendimento estiver sob controle humano, a IA não envia mensagens ao cliente. Ela só retoma após liberação explícita do humano.
6. O backend tem autoridade final sobre todo envio automático. Toda mensagem é validada por controles determinísticos imediatamente antes do envio; resposta gerada não é resposta autorizada.
7. Resposta bloqueada não é enviada e nunca é regenerada. O motivo é registrado e o atendimento aguarda a supervisão, salvo quando a política permitir pedir esclarecimento.
8. O chatbot não inventa preços, horários, disponibilidade, condições, propostas ou compromissos. Afirmações factuais específicas exigem fonte autorizada.
9. O chatbot não responde sobre assuntos fora do escopo autorizado.
10. Dados de empresas (tenants) permanecem isolados entre si, e informações de um cliente não são expostas a outro, inclusive dentro da mesma empresa.
11. Informações comerciais, preços, negociações, exceções, propostas fora do escopo, informações internas e confidenciais estão sujeitas a controles específicos e não são reproduzidas fora das permissões definidas.
12. Instruções do cliente não alteram regras, permissões ou comportamento do chatbot.
13. O chatbot não se apresenta falsamente como humano.
14. Estas restrições são impostas pelo backend da aplicação, não apenas por prompts.
23. Dados pessoais sensíveis enviados pelo cliente são criptografados no banco e nunca entregues ao motor de IA.
24. O sistema não envia templates pela API; retomada fora da janela de 24h é feita por humano, e toda mensagem humana enviada por fora da API coloca a conversa sob controle humano.
25. Um conjunto mínimo de proibições (vazamento de dados, conhecimento não aprovado, compromissos comerciais não autorizados, respostas fora do escopo) vale para toda empresa e não pode ser desativado pela política, nem pelo administrador.
26. Usuários humanos só veem e operam conversas e conhecimento dentro das permissões do seu papel (menor privilégio). O backend confere a identidade e o papel; o banco não aceita autoria que não tenha sido comprovada na própria operação.

## B. Conhecimento

15. Todo histórico de atendimento é estruturado, auditável e persistente, e separado da base de conhecimento publicada.
16. A IA pode sugerir conhecimentos, classificações e correções, mas não pode aprová-los nem publicá-los.
17. Só humano autorizado aprova, edita, rejeita, restringe ou revoga conhecimento reutilizável.
18. Nenhum conhecimento é promovido automaticamente; negociações individuais não viram condições gerais.
19. Todo conhecimento possui origem, classificação, escopo, validade, permissões, status e versão, como propriedades independentes.
20. Só conhecimento aprovado, vigente e dentro do escopo é usado. Conhecimento pendente, rejeitado, revogado ou expirado nunca é usado.
21. Exceções só são reutilizadas dentro do escopo expressamente autorizado.

## C. Garantia em desenvolvimento

22. As invariantes A e B são protegidas por testes automatizados, inclusive adversariais, que verificam se o backend realmente bloqueia o envio indevido, além de revisão independente (CI) e controles de repositório.

## Aplicação durante a evolução do projeto

- Não introduza comportamento do chatbot que contradiga uma invariante.
- Quando uma regra funcional depender de detalhe pendente, registre a lacuna e peça definição; não a preencha por suposição. Decisões puramente técnicas não são lacunas funcionais.
- Mudança nestas invariantes é mudança de requisito do produto e exige aprovação do Victor.

## Histórico

Reorganizado em 2026-11-24 por decisão do Victor (separação A/B/C). Correspondência com a numeração anterior: 1→1, 2→2, 3→3, 4→4, 5→5, 6→15, 7→16, 8→17, 9→19, 10→11, 11→10, 12→22. Acrescentadas: 6, 7, 8, 9, 12, 13, 14, 18, 20, 21. Em 2026-11-24 (mais tarde): 23 e 24, da parte A, numeradas no fim para não renumerar. Em 2026-11-25 (direcionamento pós-F3): 25 e 26, também da parte A.
