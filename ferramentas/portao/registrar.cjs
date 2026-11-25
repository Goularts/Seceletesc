'use strict';
// Registra uma regressão corrigida no Git (docs/desenvolvimento/regressoes.md) e na memória do Ruflo.
// Uso:
//   node ferramentas/portao/registrar.cjs regressao --componente "ia/fila" --problema "..." --correcao "..." --teste NomeDoTeste --origem "revisoes/..."
// O teste citado precisa existir e estar ativo: o CI confere (CoberturaRegrasTestes.Toda_regressao_registrada_tem_teste_ativo).

const fs = require('node:fs');
const c = require('./comum.cjs');

function arg(nome) {
  const i = process.argv.indexOf('--' + nome);
  return i >= 0 ? process.argv[i + 1] : undefined;
}

function main() {
  if (process.argv[2] !== 'regressao') {
    console.error('Uso: node ferramentas/portao/registrar.cjs regressao --componente X --problema "..." --correcao "..." --teste Nome --origem "..."');
    return 2;
  }

  const campos = Object.fromEntries(['componente', 'problema', 'correcao', 'teste', 'origem'].map((k) => [k, arg(k)]));
  const faltando = Object.entries(campos).filter(([, v]) => !v || v.includes('|') || /[\r\n]/.test(v)).map(([k]) => k);
  if (!/^[A-Za-z0-9_]+$/.test(campos.teste || '')) faltando.push('teste (só o nome do método)');
  if (faltando.length > 0) {
    console.error('Campos obrigatórios ausentes ou com "|": ' + faltando.join(', '));
    return 2;
  }

  const numeros = c.regressoes().map((r) => Number(r.id.replace('REG-', '')));
  const id = 'REG-' + String(Math.max(0, ...numeros) + 1).padStart(3, '0');
  const linha = `| ${id} | ${campos.componente} | ${campos.problema} | ${campos.correcao} | ${campos.teste} | ${campos.origem} |`;
  const texto = c.ler(c.DOCS.regressoes).replace(/\n*$/, '\n') + linha + '\n';
  fs.writeFileSync(c.DOCS.regressoes, texto);

  // O Git guarda o texto completo; o Ruflo recebe a versão segura para a linha de comando (a sincronização importa por arquivo, sem shell).
  const valor = c.seguro(`${campos.componente}: ${campos.problema}. Correção: ${campos.correcao}. Teste: ${campos.teste}. Origem: ${campos.origem}`, 1000);
  const r = c.ruflo(['memory', 'store', '-k', id, '--value', valor, '-n', 'regressoes', '--provenance', 'agent_output'], 60000);
  c.registrar({ acao: 'registrar-regressao', id, ruflo: r.ok });
  console.log(`${id} registrada em docs/desenvolvimento/regressoes.md${r.ok ? ' e na memória do Ruflo' : ' (Ruflo indisponível: rode sincronizar.cjs depois)'}.`);
  return 0;
}

process.exitCode = main();
