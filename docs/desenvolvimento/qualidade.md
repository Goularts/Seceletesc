# C. Qualidade de desenvolvimento

Como o software é construído. A equipe de agentes, coordenada pelo Ruflo, tem **autonomia técnica**. O objetivo da qualidade é garantir que as regras A (`operacao-chatbot.md`) e B (`governanca-conhecimento.md`) sejam cumpridas no software em produção.

O Ruflo coordena a qualidade do desenvolvimento. Ele **não substitui** as verificações de segurança do backend em produção.

## 1. Autonomia

Sem aprovação individual do Victor, os agentes podem:

- criar, modificar, mover e organizar arquivos;
- definir a arquitetura e as soluções técnicas;
- criar, modificar e refatorar código e testes;
- configurar ferramentas locais do projeto;
- implementar APIs e infraestrutura do projeto;
- corrigir problemas apontados pelos agentes de revisão;
- abrir e integrar PRs quando o CI estiver verde.

## 2. O que precisa de confirmação do Victor

Só quando houver risco relevante:

- operações destrutivas (apagar dados, reescrever histórico publicado do Git, force push);
- exposição de dados (publicar conteúdo, enviar dados a serviços externos);
- alterações de infraestrutura externa (contas, serviços pagos, recursos em nuvem, configurações globais da máquina);
- mudanças que **ampliem ou alterem o comportamento funcional do chatbot** ou as regras em `docs/business-rules/`.

Etapa atual: preparação. Antes de implementar funcionalidades, apresentar um **plano técnico consolidado** ao Victor.

## 3. Agentes (`.claude/agents/`)

| Agente | Papel | Ferramentas |
|---|---|---|
| `arquiteto` | propõe e revisa arquitetura | leitura |
| `desenvolvedor` | implementa e corrige | leitura, edição, shell |
| `testes` | escreve e executa testes, inclusive adversariais | leitura, edição, shell |
| `seguranca` | revisa isolamento, dados, injeção, envio indevido | leitura |
| `guardiao-regras` | confere aderência às regras A e B | leitura |

Fluxo típico de uma entrega (recomendado, não obrigatório):

1. `arquiteto` propõe a abordagem (quando a mudança é estrutural).
2. `testes` escreve os testes das regras envolvidas.
3. `desenvolvedor` implementa até os testes passarem.
4. `seguranca` e `guardiao-regras` revisam; achados voltam para o `desenvolvedor`.
5. CI verde → merge.

**Registro de revisão:** toda revisão gera um arquivo em `docs/desenvolvimento/revisoes/` com a disposição de **cada** achado: corrigido (com evidência), aceito como risco (motivo e onde está documentado), adiado (para qual fase) ou rejeitado (motivo). Nenhum achado fica sem disposição.

Proporcional à complexidade: mudança pequena não precisa de todas as etapas.

## 4. Testes

- Os testes de regras A e B verificam **a decisão do backend** (envio autorizado, bloqueado, supervisão acionada, motivo registrado), não apenas se o texto do modelo parece adequado.
- Testes podem ser criados e alterados livremente, **exceto** para enfraquecer a proteção de uma regra A ou B só para fazer uma implementação passar. Se um teste de regra mudar, o PR explica o porquê, e o `guardiao-regras` confere.
- Matriz regra → teste: `matriz-rastreabilidade.md`.
- Cada teste de regra é marcado com `[Trait("Regra", "<ID>")]`, com IDs `INV-<n>`, `CTX-<nnn>` ou `ADV-<nn>`.
- `docs/desenvolvimento/cobertura-obrigatoria.txt` lista as regras exigidas até a fase atual e o mínimo de testes de cada uma.
- Proteção automática, sem aprovação humana:
  - `CoberturaRegrasTestes` conta os testes ativos nos assemblies compilados (teste desativado com `Skip` ou comentado não conta) e falha se alguma regra ficar abaixo do mínimo, ou se algum teste apontar para uma regra que não existe;
  - o job `cobertura-nao-diminui` do CI falha se o PR tirar uma regra da lista ou reduzir um mínimo.
- Testes que protegem o banco são validados por mutação: remover a proteção deve fazer algum teste falhar.

### Cenários adversariais obrigatórios

| ID | Cenário |
|---|---|
| ADV-01 | Cliente pede desconto não autorizado |
| ADV-02 | Cliente pergunta preço inexistente |
| ADV-03 | Cliente pergunta horário sem disponibilidade confirmada |
| ADV-04 | Cliente pede informação confidencial |
| ADV-05 | Cliente tenta obter dados de outra pessoa |
| ADV-06 | Cliente tenta induzir a IA a ignorar instruções |
| ADV-07 | Cliente pede serviço fora do escopo |
| ADV-08 | Informação contraditória ou desatualizada |
| ADV-09 | Humano assume durante a geração de uma resposta |
| ADV-10 | Tentativa de usar conhecimento ainda não aprovado |
| ADV-11 | Resposta humana contém exceção específica (não pode virar conhecimento geral) |
| ADV-12 | Resposta gerada contém informação não relacionada ou compromisso indevido |
| ADV-13 | Cliente pergunta se está falando com um humano |
| ADV-14 | Motor de IA declara assunto falso (autorizado) para pergunta de assunto não autorizado |
| ADV-15 | Resposta cita fonte válida mas sem relação com a pergunta |
| ADV-16 | Conteúdo INT/NEG/RES parafraseado sem citação |
| ADV-17 | URL na resposta gerada |
| ADV-18 | Aviso de transferência depois que o humano assumiu |
| ADV-19 | Fonte revogada enquanto a mensagem aguarda no outbox |
| ADV-20 | Timeout ambíguo no envio ao WhatsApp |
| ADV-21 | Telefone de outro cliente no texto gerado |
| ADV-22 | Usuário da supervisão acessa conversa de outra empresa pelo id |
| ADV-23 | Webhook com assinatura inválida ou reenviado |

## 5. Git e CI

- Trabalho em branches; integração na `master` por PR.
- Revisão independente das mudanças é feita pelo CI (build, testes, testes de regras). O parecer de um subagente do mesmo modelo não conta como revisão independente.
- O CI deve existir antes do primeiro merge de código da aplicação. Provedor: GitHub Actions (o repositório está no GitHub).
- Sem exigência de aprovação manual de PR nem de conta separada para agentes neste momento.

## 6. O que é software e o que é comportamento

| Item | Classificação | Controle |
|---|---|---|
| Código, testes, CI, arquitetura | software | autonomia + CI |
| Prompt do motor de IA | software | autonomia, mas toda mudança roda os testes adversariais; o prompt nunca é a única barreira |
| Política por tenant (assuntos autorizados, assuntos que exigem humano, permissões) | comportamento em produção | definida por humanos no painel, versionada e auditável |
| Regras em `docs/business-rules/` | comportamento do produto | mudança exige aprovação do Victor |

## 7. Diretrizes para o plano técnico

Levantadas na revisão de arquitetura de 2026-11-24. O plano técnico consolidado deve resolver, nesta ordem:

1. Máquina de estados da conversa (inclui "transferência pendente", em que só o aviso de transferência é permitido) e controle de concorrência.
2. Ponto único de saída para tudo que vai ao cliente, inclusive respostas humanas (auditoria). "Imediatamente antes do envio" = conferir o estado e reservar o envio na mesma transação, com versão do estado; a tomada de controle humana usa o mesmo mecanismo.
3. Isolamento por tenant e por cliente (opções: RLS, schema por tenant, filtro na aplicação) e identificação do tenant pelo número de WhatsApp.
4. Contrato estruturado com o motor de IA: texto, fontes citadas (id + versão), assunto declarado. O motor não acessa o banco nem envia mensagens.
5. Política por tenant versionada; revalidação consulta status e validade das fontes direto no banco no momento do envio, sem depender de cache.
6. Histórico append-only registrando versões de política e de conhecimento usadas em cada decisão.
7. Webhooks do WhatsApp: idempotência, ordem, verificação de assinatura; janela de 24h e templates.

## 8. Piloto do Ruflo

Ganho e escalabilidade do Ruflo são medidos em `docs/setup/ruflo-piloto-qa.md`.
