# Piloto Ruflo: monitoramento de QA

Este projeto é o primeiro piloto do Ruflo. O objetivo deste arquivo é responder, com dados, duas perguntas:

1. O Ruflo gera ganho real em relação a usar só os subagentes do Claude Code?
2. O modelo escala (mais agentes, mais tarefas, outros projetos) sem perder controle?

Este documento não define regras de negócio. Elas ficam em `docs/business-rules/`.

## Como registrar

Uma linha por tarefa concluída na tabela de registro. Preencha logo ao fim da tarefa, não de memória.

- **Modo**: `ruflo` (coordenação via ferramentas do Ruflo) ou `base` (só subagentes do Claude Code, para comparação).
- **Retrabalho**: quantas vezes a entrega voltou por erro de agente (teste quebrado, regra ignorada, arquivo errado).
- **Violação**: o agente tentou ou fez algo proibido (editar caminho protegido, inventar regra, pular teste). Descrever em Observações.
- **Bloqueio útil**: `guardiao-regras` ou `seguranca` barraram algo que de fato estava errado.
- **Falso alarme**: barraram algo que estava certo.
- **Intervenções**: quantas vezes o Victor precisou corrigir o rumo.
- **Atrito do Ruflo**: falha, lentidão, ferramenta confusa, configuração que precisou ser desligada.

## Registro

| Data | Tarefa | Modo | Agentes usados | Retrabalho | Violação | Bloqueio útil | Falso alarme | Intervenções | Atrito do Ruflo | Observações |
|---|---|---|---|---|---|---|---|---|---|---|
| 2026-11-24 | Revisão de consistência das regras A/B/C | ruflo | guardiao-regras, arquiteto (em paralelo) | 0 | 0 | 6 (ordem do aviso de transferência inventada; 3 lacunas de teste; regras não verificáveis na matriz; conflitos técnicos de concorrência/cache) | 9 (7 "regras inventadas" que vinham da 1ª especificação, que não foi passada ao guardião; branch `main` inexistente; sugestão de revisão humana contrária à decisão do Victor) | 0 | Ruflo só registrou swarm e tarefa; a revisão foi feita pelos subagentes nativos. Sem ganho mensurável do Ruflo nesta tarefa | Falsos alarmes do guardião vieram de contexto incompleto no pedido: incluir as fontes das decisões nos próximos pedidos |
| 2026-11-24 | Revisão do plano técnico v1 | ruflo | arquiteto, seguranca (em paralelo) | 1 (plano reescrito como v2) | 0 | ~20 (9 altos de segurança: contexto sem filtro de classificação, RLS sem SET LOCAL, outbox sem restrição de decisão, envio duplicado, IDOR, validador burlável; arquitetura: worker sem revalidar fontes, aviso bloqueado pelo próprio validador, ordem das fases) | 0 | 0 | Ruflo de novo só registrou a tarefa; trabalho feito pelos subagentes | Com as fontes das decisões no pedido, zero falsos alarmes (antes: 9). Os dois revisores convergiram nos achados principais |
| 2026-11-24 | F0 Fundação (implementação + revisão) | ruflo | seguranca, guardiao-regras (em paralelo) | 1 (camada de banco refeita após revisão) | 0 | 13 (alta: dono superusuário anulava o FORCE RLS nos testes; testes que passariam com proteção quebrada; checagem de cobertura burlável por Skip/remoção parcial/edição da lista) | 0 (corrigido em 2026-11-24: antes marquei como falso alarme o achado de testes de fronteira sem efeito, que era válido) | 0 | Ruflo só registrou a tarefa. Testes de mutação (feitos por mim) revelaram 1 teste fraco que a revisão não pegou; a revisão revelou 2 que a mutação não cobria | Revisão + mutação se complementam; manter as duas em toda fase. Disposição de cada achado: docs/desenvolvimento/revisoes/2026-11-24-f0.md |
| 2026-11-25 | F1a Domínio (implementação + revisão) | ruflo | seguranca, guardiao-regras (em paralelo) | 1 (validador, elegibilidade e detector reforçados) | 0 | 32 (guardião: 1 regra contrariada, 2 invenções, 6 rótulos errados; segurança: 9 altas, incluindo números recombinados, cirílico, URL com sufixo, CLI de escopo empresa) | 0 | 0 | Ruflo só registrou a tarefa. Os próprios testes acharam antes o bug grave da normalização (InvariantGlobalization) | Disposição completa: revisoes/2026-11-25-f1a.md. 8 mutantes mortos |
| 2026-11-25 | F1b Pipeline (implementação + revisão) | ruflo | seguranca, guardiao-regras (em paralelo) | 1 (banco, worker e orquestrador reforçados) | 0 | 15 (guardião: 1 regra contrariada, worker não acionava supervisão; 1 efeito não decidido; 3 rótulos fracos. Segurança: decisão sem hash do texto, transições livres da app, lease × timeout, FK sem tenant, dado protegido na saída) | 0 | 0 | Ruflo só registrou a tarefa | Mutação revelou 1 teste ausente (assumir e liberar) e 1 teste fraco (hash). 13 mutantes mortos. Disposição: revisoes/2026-11-25-f1b.md |
| 2026-11-25 | F2 Supervisão (implementação + revisão) | ruflo | seguranca, guardiao-regras (em paralelo) | 1 (aviso, permissões da conversa, autenticação antes do corpo, worker) | 0 | 19 (guardião: 1 bloqueante, aviso automático editável sem validação de conteúdo; 5 rótulos que inflavam a cobertura. Segurança: autenticação após o binding, permissões amplas da supervisão na conversa, worker falhando aberto, autor inativo, domínio genérico) | 1 parcial (S11: o FORCE na migração era necessário, como mostrou o erro 42704) | 0 | Ruflo só registrou a tarefa | A mutação achou 1 equivalente (trigger + CHECK). O guardião achou o problema mais grave da fase, numa interação entre F1 e F2 que os testes não cobriam. Disposição: revisoes/2026-11-25-f2.md |
| 2026-11-25 | F3 Conhecimento e papéis (implementação + revisão) | ruflo | seguranca, guardiao-regras (em paralelo) | 1 (e-mail exato, rebaixamento e escopo só com administrador, rejeição de edição, liberação com responsável, travas de revogação) | 0 | 22 (guardião: 1 bloqueante, e-mail por substring; supervisor rebaixando NEG; edição não rejeitável; dado comum em orientação. Segurança: escopo ampliado por supervisor, liberação sem papel no banco, corrida revogar×enviar, versão vigente regredindo) | 0 | 0 | Ruflo só registrou a tarefa | Os dois revisores convergiram em 4 achados (e-mail, rebaixamento, dado comum, edição pendente). A mutação achou 1 teste ausente (C7) e 2 equivalentes. Disposição: revisoes/2026-11-25-f3.md |
| 2026-11-25 | Pós-F3: identidade, atribuição, visibilidade, piso da política (implementação + revisão) | nenhuma (sem Ruflo) | seguranca, guardiao-regras (em paralelo) | 1 (CEP em todo conhecimento, eventos e autoria carimbados pelo banco, atendente preso à fila no banco, worker confere autor, perímetro sem diferenciar maiúsculas) | 0 | 20 (guardião: 2 bloqueantes, CEP aceito contra a regra e interpretações escritas como decisão. Segurança: autoria forjável em eventos, atendente fora da fila no banco, chave nos logs do PostgreSQL, caminho em maiúsculas) | 0 | 0 | Ruflo não foi usado nesta fase, sem perda: a revisão e a mutação acharam tudo | Os revisores convergiram em 3 achados (CEP/escopo, atribuição em conversa automática, HTTPS desligável). Indício para a próxima revisão do piloto: reduzir o Ruflo ao mínimo ou removê-lo. Disposição: revisoes/2026-11-25-pos-f3.md |

## Custos conhecidos do Ruflo (linha de base em 2026-11-24)

- `ruflo@3.80.6` fixado, mas dependências em faixa (`^`): o código baixado pode mudar sem aviso.
- O MCP anuncia ~394 ferramentas (~78 mil tokens de schema).
- Funcionalidades ligadas por padrão que precisaram ser desligadas: daemon residente, funnel de marketing, auto-update.
- Memória em modo `memory` (não persistente).

Registrar aqui qualquer custo novo que aparecer.

## Revisão do piloto

Fazer a cada ~10 tarefas ou ao fim de cada fatia entregue. Para cada pergunta, responder com base no registro:

| Critério | Sinal de ganho | Sinal de problema |
|---|---|---|
| Qualidade | menos retrabalho no modo `ruflo` que no `base` | igual ou pior |
| Governança | zero violações; bloqueios úteis > falsos alarmes | violação de invariante ou caminho protegido |
| Autonomia | intervenções caindo com o tempo | intervenções estáveis ou subindo |
| Custo | atrito baixo e estável | atrito recorrente, tokens/latência altos |
| Escala | paralelismo (swarm) entrega mais sem perder controle | paralelismo gera conflito ou perda de rastreio |

Decisão possível em cada revisão: manter, ajustar (ex.: habilitar memória persistente, workflows), reduzir ao mínimo, ou remover o Ruflo e seguir só com subagentes.

### Revisões

_Nenhuma ainda._
