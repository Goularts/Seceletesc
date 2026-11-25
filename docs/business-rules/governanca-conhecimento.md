# B. Governança de conhecimento

Como uma informação passa a ser usada pelo chatbot. **Nenhuma sugestão é publicada na base de conhecimento usada pelo chatbot sem aprovação humana explícita.**

Resumo numerado em `invariants.md`. Comportamento em atendimento: `operacao-chatbot.md`.

## 1. Histórico e base de conhecimento

- Toda conversa possui histórico estruturado, auditável e persistente: mensagens, decisões de envio e bloqueio (com motivo), intervenções humanas, orientações internas, transferências e devoluções de controle, sugestões, revisões, aprovações, versões e revogações.
- O **histórico bruto** não é a **base de conhecimento publicada**. O chatbot só consulta a base publicada (e as demais fontes autorizadas), nunca o histórico de outras conversas como fonte de conhecimento.
- Nenhum conhecimento é promovido automaticamente.

## 2. Ciclo de vida

1. A IA analisa intervenções humanas e **sugere** conhecimentos, classificações, correções ou atualizações. Orientações internas dadas à IA também entram como sugestão.
2. A sugestão entra com status **REV** (pendente de revisão) e não pode ser usada pelo chatbot.
3. Um humano autorizado **aprova, edita, rejeita, restringe ou revoga**.
4. Só conhecimento aprovado, vigente e dentro do escopo é usado em atendimento.

Fluxo obrigatório (decisão do Victor, 2026-11-25):

1. A IA ou um humano sugere um conhecimento.
2. A sugestão é registrada como pendente (REV), com origem e contexto da conversa.
3. A IA pode sugerir classificação, escopo, validade e restrições; a sugestão não vale como permissão.
4. Um humano autorizado revisa: edita, aprova ou rejeita. Cada edição gera nova versão.
5. A aprovação gera uma versão com autoria da aprovação e permissões explícitas (classificação, escopo e validade definidos por quem aprova).
6. Só versões aprovadas e vigentes ficam disponíveis para o agente. Uma edição pendente de um conhecimento já aprovado não substitui a versão vigente até ser aprovada.
7. A revogação impede novas utilizações, inclusive de mensagens que ainda estão na fila.
8. Todo o processo é auditado. Rejeitado e revogado são finais: para voltar a usar, registra-se uma nova sugestão.

Nenhuma sugestão é publicada automaticamente, mesmo quando parece correta.

- **CTX-012** Nenhum conhecimento pode ser publicado sem aprovação humana.
- **CTX-013** Todo conhecimento reutilizado deve possuir origem, versão, escopo e autorização.
- **CTX-014** Conhecimentos rejeitados, revogados, expirados ou pendentes não podem ser usados.

## 3. Propriedades do conhecimento

Propriedades **independentes** entre si:

| Propriedade | Valores / regra |
|---|---|
| Classificação | PUB, INT, TMP, CLI, NEG, EXC, RES (podem coexistir) |
| Status | REV (pendente de revisão), aprovado, rejeitado, revogado |
| Escopo | empresa, cliente, proposta ou atendimento |
| Validade | data de expiração. Obrigatória para conhecimento não público; a política define prazos máximos por classificação (decisão do Victor, 2026-11-25), aplicados na aprovação e também ao que já foi aprovado: quando a política reduz o prazo, o conhecimento que passa do novo prazo deixa de ser usado e vai para reavaliação humana, com registro, sem exclusão silenciosa (decisão do Victor, 2026-11-25). PUB pode ter validade indefinida, mas continua sujeita a revisão e revogação |
| Autorização / permissões | quem pode usar e em que condições; restrições de reprodução |
| Origem | conversa, intervenção ou fonte de onde veio |
| Versão | toda edição gera nova versão; versões anteriores são preservadas |

### Classificações

- **PUB** informação pública.
- **INT** informação interna.
- **TMP** informação temporária.
- **CLI** informação específica do cliente.
- **NEG** negociação comercial.
- **EXC** exceção autorizada.
- **RES** informação restrita.

REV é status, não classificação.

### Regras específicas

- **Exceções (EXC)** só são reutilizadas dentro do escopo expressamente autorizado.
- **Negociações individuais (NEG)** não viram condição comercial geral.
- **PUB** pode não ter expiração, mas sempre pode ser revisada e revogada.
- Informação **confidencial** (INT, CLI, NEG, RES) não é reproduzida fora das permissões definidas.
- **Permissões comerciais** (decisão do Victor, 2026-11-25): o agente é uma secretária de atendimento, não um agente comercial. Negociações, descontos, propostas especiais e exceções comerciais não são oferecidos autonomamente. Só o administrador aprova conhecimento NEG, EXC, RES ou CLI e altera a política que libera essas categorias. Mesmo com autorização do administrador, NEG e EXC só valem para um cliente ou atendimento definido e com validade. O que estiver fora das permissões vai para o atendimento humano.
- **Dados pessoais não viram conhecimento** (decisão do Victor, 2026-11-25). Nenhum dado pessoal é promovido a conhecimento, nem por sugestão, orientação, edição ou aprovação. E-mail só entra em conhecimento se for um endereço institucional autorizado, listado na política da empresa; qualquer outro e-mail é tratado como pessoal e recusado. Endereço institucional da empresa pode ser conhecimento público aprovado (decisão do Victor, 2026-11-25): só os CEPs listados como institucionais na política. Endereço particular de cliente é dado protegido e nunca entra em conhecimento.

## 3a. Papéis humanos

Decisão do Victor (2026-11-25). Toda autorização é validada pelo backend; o modelo de IA não concede nem altera permissões.

| Papel | Pode | Não pode |
|---|---|---|
| **Atendente** | ver as conversas atribuídas a ele e a fila de atendimentos disponíveis; pegar um atendimento da fila; assumir e responder os atendimentos atribuídos a ele; registrar orientações e sugestões de conhecimento | ver conversas atribuídas a outros; aprovar conhecimento; alterar a política; liberar a conversa para a IA; encerrar pendências de retomada |
| **Supervisor** | o que o atendente faz, em todas as conversas da empresa; gerenciar a fila (atribuir, reatribuir, retirar); liberar conversas e encerrar pendências de retomada; revisar, editar, classificar e rejeitar sugestões; aprovar conhecimento operacional comum (PUB, INT, TMP) | aprovar NEG, EXC, RES ou CLI; revogar; alterar a política |
| **Administrador** | o que o supervisor faz; gerenciar usuários, papéis e a política; aprovar conhecimento de maior risco; revogar conhecimento publicado; definir permissões e restrições | dispensar auditoria, isolamento de dados ou aprovação humana obrigatória |

### Visibilidade (menor privilégio)

Decisão do Victor (2026-11-25): cada papel vê só o que precisa. Detalhamento técnico, a confirmar pelo Victor:

- **Atendente:** conhecimento aprovado PUB e TMP de escopo da empresa ou de cliente/atendimento de conversa atribuída a ele, e as sugestões que ele mesmo registrou. Não vê INT, CLI, NEG, EXC nem RES, nem sugestões ou edições pendentes de outros usuários.
- **Supervisor:** conhecimento aprovado PUB, INT, TMP e CLI e todas as sugestões e edições pendentes (é quem revisa). Conhecimento aprovado como NEG, EXC ou RES só o administrador vê.
- **Administrador:** todo o conhecimento da empresa.
- Um item é visto só se **todas** as versões aprovadas dele estiverem dentro do que o papel pode ver. A classificação sugerida numa proposta não esconde nem libera nada: vale a que foi aprovada.
- O isolamento entre empresas vale para todos os papéis.

## 4. Painel de supervisão

O painel deve permitir:

- visualizar sugestões de conhecimento;
- consultar a origem e o contexto da informação;
- aprovar, editar, rejeitar ou revogar conhecimentos;
- definir permissões e restrições de uso;
- definir escopo por empresa, cliente, proposta ou atendimento;
- estabelecer validade e expiração;
- impedir a reprodução de informações confidenciais;
- controlar quais assuntos o agente pode responder autonomamente;
- definir quais assuntos exigem intervenção humana;
- acompanhar as conversas com marcação de retomada humana (fora da janela de 24h), com status e resumo para consulta interna.

## 5. Lacunas funcionais

Não presumir. Ver "Decisões pendentes" em `docs/project-context.md`:

- Política de retenção, auditoria e LGPD do histórico.
