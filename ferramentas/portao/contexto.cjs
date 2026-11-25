'use strict';
// Consulta automática antes da tarefa (hook UserPromptSubmit do Claude Code, ou à mão no Codex):
// busca decisões, invariantes e regressões relacionadas ao pedido, na memória do Ruflo (semântica) e no Git
// (palavras-chave). Uso manual: node ferramentas/portao/contexto.cjs --consulta "descrição da tarefa"

const c = require('./comum.cjs');

const VAZIAS = new Set(['para', 'como', 'com', 'que', 'uma', 'dos', 'das', 'nos', 'nas', 'sem', 'por', 'mais', 'isso', 'esse', 'essa', 'este', 'esta', 'quando', 'onde', 'qual', 'quais', 'agora', 'vamos', 'fazer', 'pode', 'deve', 'todo', 'toda', 'cada', 'entre', 'sobre', 'também', 'ainda', 'depois', 'antes']);

function termos(texto) {
  return [...new Set(texto.toLowerCase().normalize('NFD').replace(/[̀-ͯ]/g, '')
    .split(/[^a-z0-9-]+/).filter((t) => t.length >= 4 && !VAZIAS.has(t)))];
}

/** Itens do Git que compartilham termos com a consulta (determinístico, não depende do Ruflo). */
function buscarNoGit(consulta, limite) {
  const q = termos(consulta);
  if (q.length === 0) return [];
  const itens = [
    ...c.decisoes().map((d) => ({ ns: 'decisoes', id: d.id, texto: d.texto })),
    ...c.invariantes().map((d) => ({ ns: 'invariantes', id: d.id, texto: d.texto })),
    ...c.regressoes().map((r) => ({ ns: 'regressoes', id: r.id, texto: `${r.componente}: ${r.problema}. Teste: ${r.teste}` })),
  ];
  return itens
    .map((i) => ({ ...i, pontos: termos(i.texto).filter((t) => q.includes(t)).length }))
    .filter((i) => i.pontos >= 2)
    .sort((a, b) => b.pontos - a.pontos)
    .slice(0, limite);
}

function consultar(consulta) {
  const textoPorId = new Map([
    ...c.decisoes().map((d) => [d.id, d.texto]),
    ...c.invariantes().map((d) => [d.id, d.texto]),
    ...c.regressoes().map((r) => [r.id, `${r.problema} (teste: ${r.teste})`]),
  ]);
  const ruflo = c.buscar(consulta, 6);
  const git = buscarNoGit(consulta, 6);
  const achados = new Map();
  for (const r of ruflo || []) achados.set(r.key, { id: r.key, ns: r.namespace, fontes: ['Ruflo ' + r.score.toFixed(2)] });
  for (const g of git) {
    const a = achados.get(g.id) || { id: g.id, ns: g.ns, fontes: [] };
    a.fontes.push('Git');
    achados.set(g.id, a);
  }
  const lista = [...achados.values()].filter((a) => textoPorId.has(a.id)).slice(0, 10);
  return { ruflo: ruflo !== null, lista, textoPorId };
}

function formatar(resultado) {
  const linhas = ['[Portão de mudanças: consulta automática antes da tarefa]'];
  linhas.push(resultado.ruflo ? 'Fontes: memória do Ruflo (busca semântica) + documentos do Git.' : 'Ruflo indisponível: só documentos do Git.');
  if (resultado.lista.length === 0) {
    linhas.push('Nada relacionado encontrado nas decisões, invariantes e regressões.');
  } else {
    linhas.push('Relacionado ao pedido (confira antes de mudar):');
    for (const a of resultado.lista) {
      const texto = resultado.textoPorId.get(a.id).slice(0, 220);
      linhas.push(`- [${a.id}] ${texto} (${a.fontes.join(', ')})`);
    }
  }
  linhas.push('Antes de concluir mudança em src/ ou tests/: node ferramentas/portao/testar.cjs. Bug corrigido: registrar em docs/desenvolvimento/regressoes.md.');
  return linhas.join('\n');
}

function main() {
  const i = process.argv.indexOf('--consulta');
  const manual = i >= 0;
  const consulta = manual ? process.argv.slice(i + 1).join(' ') : String(c.lerEntrada().prompt || '');
  if (consulta.trim().length < 12) return 0; // pedidos curtos ("ok", "continue") não precisam de consulta
  const resultado = consultar(consulta);
  c.registrar({
    acao: 'contexto', origem: manual ? 'manual' : (process.env.CLAUDE_PROJECT_DIR ? 'hook' : 'stdin-sem-claude'), ruflo: resultado.ruflo,
    consulta: c.resumoDeTexto(consulta), achados: resultado.lista.map((a) => a.id),
  });
  const texto = formatar(resultado);
  if (manual) {
    console.log(texto);
  } else {
    process.stdout.write(JSON.stringify({ hookSpecificOutput: { hookEventName: 'UserPromptSubmit', additionalContext: texto } }));
  }
  return 0;
}

if (require.main === module) process.exitCode = main();
module.exports = { consultar, buscarNoGit, formatar };
