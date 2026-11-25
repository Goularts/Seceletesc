'use strict';
// Antes de editar um arquivo (hook PreToolUse do Claude Code, ou à mão): mostra a área afetada, as regras, as
// regressões já corrigidas naquela área, os revisores obrigatórios e os testes principais. Só Git, sem rede.
// Uso manual: node ferramentas/portao/impacto.cjs --arquivo src/Seceletesc.Dominio/Envio/ValidadorEnvio.cs

const path = require('node:path');
const c = require('./comum.cjs');

function relativo(arquivo) {
  const abs = path.isAbsolute(arquivo) ? arquivo : path.join(c.RAIZ, arquivo);
  return path.relative(c.RAIZ, abs).split(path.sep).join('/');
}

function analisar(arquivo) {
  const rel = relativo(arquivo);
  const areas = c.mapaImpacto().areas.filter((a) => rel.startsWith(a.padrao));
  if (areas.length === 0) return null;
  const componentes = new Set(areas.flatMap((a) => a.componentes));
  const regressoes = c.regressoes().filter((r) => [...componentes].some((k) => r.componente.split('/').includes(k)));
  return {
    arquivo: rel,
    areas: areas.map((a) => a.nome),
    regras: [...new Set(areas.flatMap((a) => a.regras))],
    revisores: [...new Set(areas.flatMap((a) => a.revisores))],
    testes: [...new Set(areas.flatMap((a) => a.testes))],
    avisos: areas.map((a) => a.aviso).filter(Boolean),
    regressoes,
  };
}

function formatar(r) {
  const l = [`[Portão de mudanças: impacto de ${r.arquivo}]`];
  l.push(`Área: ${r.areas.join('; ')}. Regras: ${r.regras.join(', ')}.`);
  if (r.regressoes.length > 0) {
    l.push('Regressões já corrigidas nesta área (não reabrir):');
    for (const g of r.regressoes) l.push(`- ${g.id}: ${g.problema} -> teste ${g.teste}`);
  }
  if (r.testes.length > 0) l.push(`Testes principais: ${r.testes.join(', ')}.`);
  l.push(`Revisão obrigatória antes do PR: ${r.revisores.join(', ')}.`);
  for (const a of r.avisos) l.push('Atenção: ' + a);
  return l.join('\n');
}

function main() {
  const i = process.argv.indexOf('--arquivo');
  const manual = i >= 0;
  const entrada = manual ? { tool_input: { file_path: process.argv[i + 1] } } : c.lerEntrada();
  const arquivo = entrada.tool_input && (entrada.tool_input.file_path || entrada.tool_input.notebook_path);
  if (!arquivo) return 0;
  const r = analisar(arquivo);
  if (!r) return 0;
  c.registrar({ acao: 'impacto', origem: manual ? 'manual' : (process.env.CLAUDE_PROJECT_DIR ? 'hook' : 'stdin-sem-claude'), arquivo: r.arquivo, regras: r.regras, regressoes: r.regressoes.map((g) => g.id) });
  const texto = formatar(r);
  if (manual) {
    console.log(texto);
  } else {
    process.stdout.write(JSON.stringify({ hookSpecificOutput: { hookEventName: 'PreToolUse', additionalContext: texto } }));
  }
  return 0;
}

if (require.main === module) process.exitCode = main();
module.exports = { analisar, formatar };
