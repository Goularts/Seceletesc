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
| Validade | data de expiração; PUB pode ter validade indefinida |
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
- definir quais assuntos exigem intervenção humana.

## 5. Lacunas funcionais

Não presumir. Ver "Decisões pendentes" em `docs/project-context.md`:

- Papéis humanos: quem é "humano autorizado" e quem pode aprovar cada classificação.
- Política de retenção, auditoria e LGPD do histórico.
