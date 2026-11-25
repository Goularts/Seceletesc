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

A conversa é natural e fluida, como com um humano: sem menus, sem URA e sem elementos interativos (botões, listas de opções) nas mensagens ao cliente, mas com limites rigorosos de conhecimento e autorização.

### Orientação interna

A orientação interna pode conter informação específica. Por isso ela não vira fonte da IA diretamente: entra como sugestão de conhecimento (status REV) e passa por avaliação e classificação humana (`governanca-conhecimento.md`). Só depois de aprovada, com escopo definido (que pode ser apenas aquele atendimento), a IA pode usá-la.

### Transferência

- O humano pode assumir a conversa a qualquer momento, não só quando a IA pede, dentro das permissões do papel dele.
- **Atribuição** (decisão do Victor, 2026-11-25): atendimentos que precisam de humano formam uma fila. O atendente vê as conversas atribuídas a ele e a fila de atendimentos disponíveis. Supervisor e administrador gerenciam a fila da empresa. O isolamento entre empresas vale sempre.
  - Detalhamento técnico, **a confirmar pelo Victor**: o atendente só atua (lê as mensagens, assume, responde, orienta) nas conversas atribuídas a ele; da fila, vê só o resumo, com o telefone parcialmente oculto, e pega o atendimento ao assumir; não assume conversa que está com a IA fora da fila; liberar a conversa para a IA tira a atribuição.
- A IA pode avisar o cliente sobre a transferência quando pertinente, sem prometer prazo ou disponibilidade não confirmados. O aviso passa pela mesma validação do backend e, como qualquer mensagem automática, não pode ser enviado se um humano já tiver assumido a conversa.

### Fora da janela de 24h do WhatsApp

- O sistema não envia templates pela API para retomar conversas.
- Quando a conversa precisa de retomada fora da janela, o cliente recebe uma **marcação** no painel de acompanhamento, com o status da conversa e um resumo para consulta interna. O resumo não é enviado ao cliente e não contém dados protegidos (seção 3a).
- A retomada é feita por um humano pelo celular (mesmo número de WhatsApp). Toda mensagem enviada pelo humano por fora da API coloca a conversa sob controle humano e é registrada no histórico.
- A marcação (pendência de retomada pelo celular) fica **ativa até ser encerrada explicitamente** por um humano autorizado (supervisor ou administrador), com responsável, data e motivo registrados. Liberar a conversa para a IA **não** encerra a pendência.
- Enquanto a pendência estiver ativa, a IA não pode afirmar que o assunto foi resolvido. A pendência é visível na supervisão.

## 3a. Dados protegidos do cliente

- Dados pessoais sensíveis enviados pelo cliente na conversa (ex.: CPF, documentos, dados de pagamento) são **criptografados** no banco e **nunca** são entregues ao motor de IA: ele recebe um marcador no lugar do dado.
- Só humanos com permissão podem ver o dado original.
- **Tratamento proporcional ao risco** (decisão do Victor, 2026-11-25), igual para respostas automáticas e respostas humanas enviadas pela API:
  - **Dado pessoal comum** (e-mail, CEP): usado só quando necessário, autorizado e pertinente ao atendimento. Na resposta automática, só se constar numa fonte aprovada citada; na resposta humana, quem autoriza é o humano. Depois do envio, o histórico guarda só o texto mascarado e o valor criptografado.
  - **E-mail institucional × pessoal** (decisão do Victor, 2026-11-25): só os endereços institucionais listados na política da empresa são da empresa. Qualquer outro e-mail é tratado como pessoal: nunca entra em conhecimento nem sai em resposta automática.
  - **Dado de identificação** (CPF, CNPJ, RG, documentos, chave PIX): exige finalidade definida e acesso restrito. No MVP, fica bloqueado até existir um fluxo seguro com finalidade definida, autorização, controle de acesso e retenção apropriada (decisão do Victor, 2026-11-25): não sai em mensagem nenhuma; o atendimento é feito pelo humano por outro meio.
  - **Dado financeiro confidencial** (número completo de cartão): nunca reproduzido em mensagem nem armazenado em texto puro.
  - **Dados de outros clientes ou empresas**: acesso e divulgação proibidos.
- A proteção vale também para entrada, armazenamento, logs e auditoria: logs e eventos não registram o valor do dado. Sem autorização ou mecanismo seguro para um dado, a operação é bloqueada e encaminhada ao tratamento humano.
- O telefone do cliente e o texto das conversas são armazenados no histórico e o texto é lido pela IA para responder (decisão do Victor, 2026-11-24). Os dados enviados à IA são usados só para o atendimento e para uso interno.

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
4. **Nunca** há nova geração após um bloqueio. A IA aguarda a resposta da supervisão.

Controles determinísticos sempre que possível. Uma segunda chamada ao modelo pode complementar, nunca substituir.

### Políticas fundamentais

Decisão do Victor (2026-11-25): um conjunto mínimo de proibições e restrições vale para toda empresa e **nem o administrador consegue desativar pela política**. A política só acrescenta restrições a esse piso:

- vazamento de dados: isolamento entre empresas e clientes, dados protegidos (seção 3a), nenhum telefone em resposta automática;
- uso de conhecimento não aprovado, vencido, revogado ou fora do escopo;
- compromissos comerciais não autorizados: valores, prazos e termos de compromisso só com fonte aprovada; os termos de compromisso padrão não podem ser removidos, só ampliados;
- respostas fora do escopo: só assuntos autônomos, com fonte do mesmo assunto;
- informação interna (INT) e restrita (RES) nunca vão para resposta automática (detalhamento técnico, **a confirmar pelo Victor**: hoje NEG, EXC e CLI continuam liberáveis pelo administrador, com as condições da seção de permissões comerciais);
- estado da conversa, autodeclaração como humano, elementos interativos e URLs fora da lista.

Essas regras não dependem só de palavras proibidas: são regras estruturadas (fonte aprovada e vigente, escopo, assunto, estado da conversa, valores comparados com a fonte) checadas pelo backend.

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
