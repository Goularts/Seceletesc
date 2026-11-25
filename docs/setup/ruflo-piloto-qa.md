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
