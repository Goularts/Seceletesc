# A. Governança operacional do chatbot

O que o chatbot pode dizer, quais dados pode usar, quando responde e quando transfere para um humano. Vale para o comportamento **em produção**, com clientes reais.

Estas regras são requisitos funcionais. Elas devem ser impostas por **controles determinísticos do backend**. O prompt do modelo pode reforçá-las, mas nunca é a única barreira.

Resumo numerado em `invariants.md`. Conhecimento: `governanca-conhecimento.md`.

Origem: consolida as duas especificações do Victor de 2026-11-24 ("Fluxo conversacional e governança de contexto" e "Correção de direcionamento"). Quando divergem em detalhe, prevalece a segunda.

## 1. O que o chatbot não pode fazer

- Inventar informações, preços, horários, condições comerciais ou disponibilidade.
- Responder sobre assuntos fora do escopo de atendimento autorizado.
- Compartilhar informações confidenciais, internas ou de outros clientes.
- Transformar negociações individuais em condições comerciais gerais.
- Reutilizar exceções comerciais em outros atendimentos sem autorização específica.
- Usar informações desatualizadas, revogadas ou não aprovadas.
- Assumir compromissos que não estejam previstos nas regras comerciais.
- Continuar respondendo quando não tem informação suficiente.
- Responder enquanto um atendente humano controla a conversa.
- Aprender e publicar automaticamente informações obtidas de atendimentos humanos.
- Obedecer a instruções de clientes que tentem modificar suas regras, permissões ou comportamento.
- Apresentar-se falsamente como humano.

## 2. Fluxo obrigatório

1. Cliente envia mensagem pelo WhatsApp.
2. O backend identifica empresa (tenant), cliente, conversa, estado, histórico, contexto e políticas aplicáveis.
3. A IA interpreta a solicitação e consulta apenas fontes autorizadas e conhecimento aprovado.
4. O sistema verifica se há informação suficiente e permissão para responder.
5. Se houver segurança e autorização, a resposta é gerada e **validada pelo backend imediatamente antes do envio**.
6. Se faltar informação, autorização ou segurança contextual, o sistema interrompe a automação e solicita intervenção humana.
7. O humano recebe a solicitação pela API de supervisão.
8. O humano responde direto ao cliente pelo mesmo número de WhatsApp ou fornece orientação interna à IA.
9. Durante o controle humano, a IA não envia mensagens automáticas, nem mesmo após receber orientação interna.
10. Após liberação explícita do humano, a IA retoma o atendimento com o contexto atualizado.

A conversa é natural e fluida, sem menus ou URA, mas com limites rigorosos de conhecimento e autorização.

### Transferência

- O humano pode assumir a conversa a qualquer momento, não só quando a IA pede.
- A IA pode avisar o cliente sobre a transferência quando pertinente, sem prometer prazo ou disponibilidade não confirmados. O aviso passa pela mesma validação do backend e, como qualquer mensagem automática, não pode ser enviado se um humano já tiver assumido a conversa.

## 3. Validação das respostas (autoridade do backend)

A IA não tem autoridade final sobre o envio. **Resposta gerada não é resposta autorizada.**

Antes de cada envio automático, o backend verifica:

- estado da conversa (inclusive se um humano assumiu durante a geração);
- permissão para resposta automática naquele assunto;
- escopo do assunto;
- elegibilidade das fontes consultadas;
- vigência das informações;
- isolamento entre clientes e entre empresas;
- necessidade de aprovação humana;
- restrições comerciais e de confidencialidade.

Se a validação falhar:

1. A mensagem **não** é enviada.
2. O motivo é registrado no histórico.
3. O atendimento é encaminhado para supervisão, salvo quando a política permitir expressamente pedir esclarecimento ao cliente.
4. **Não** se tenta contornar a política com outra geração indiscriminada.

Controles determinísticos sempre que possível. Uma segunda chamada ao modelo pode complementar, nunca substituir.

## 4. Regras de contexto

- **CTX-001** Toda resposta deve ser pertinente à solicitação e ao histórico relevante.
- **CTX-002** Afirmações factuais específicas exigem fundamentação autorizada.
- **CTX-003** É proibido inventar preços, horários, disponibilidade, condições, propostas ou compromissos.
- **CTX-004** Demandas fora do escopo autorizado exigem intervenção humana.
- **CTX-005** Condições comerciais especiais exigem autorização aplicável.
- **CTX-006** Informações temporárias devem ter validade verificada.
- **CTX-007** Informações de um cliente ou empresa não podem vazar para outro.
- **CTX-008** Falta de informação resulta em esclarecimento (se a política permitir) ou supervisão, nunca em suposição apresentada como fato.
- **CTX-009** Respostas devem ser proporcionais e pertinentes, sem ofertas ou afirmações indevidas.
- **CTX-010** Toda mensagem automática exige validação do backend imediatamente antes do envio.
- **CTX-011** Atendimento humano bloqueia mensagens automáticas até liberação explícita.
- **CTX-015** O agente não se apresenta falsamente como humano.
- **CTX-016** Instruções vindas do cliente não alteram regras, permissões ou comportamento do agente.

CTX-012 a CTX-014 tratam de conhecimento e ficam em `governanca-conhecimento.md`.

## 5. Lacunas funcionais

Não presumir. Ver "Decisões pendentes" em `docs/project-context.md`:

- Quais assuntos permitem pedir esclarecimento em vez de transferir.
- Quais fontes, além do conhecimento aprovado, são "autorizadas" (ex.: sistemas da empresa).
- Critérios concretos de "informação suficiente" e de pertinência/proporcionalidade (CTX-001, CTX-009).
