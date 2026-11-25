'use strict';
// Impressão digital das mudanças em src/ e tests/ (para saber se os testes rodaram sobre o código atual).

const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const c = require('./comum.cjs');

const ULTIMO_TESTE = path.join(c.DADOS, 'ultimo-teste.json');
const PASTAS = ['src', 'tests'];

function git(args) {
  const r = spawnSync('git', args, { cwd: c.RAIZ, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 });
  return r.status === 0 ? r.stdout : '';
}

/** Arquivos alterados, novos ou apagados em src/ e tests/ (ignorando bin/obj). */
function alteracoes() {
  return git(['status', '--porcelain', '--', ...PASTAS]).split('\n').filter((l) => l.trim().length > 0);
}

function impressao() {
  const h = crypto.createHash('sha256');
  h.update(git(['rev-parse', 'HEAD']));
  h.update(git(['diff', 'HEAD', '--', ...PASTAS]));
  for (const arquivo of git(['ls-files', '--others', '--exclude-standard', '--', ...PASTAS]).split('\n').filter(Boolean).sort()) {
    h.update(arquivo);
    try { h.update(fs.readFileSync(path.join(c.RAIZ, arquivo))); } catch { /* apagado entre a listagem e a leitura */ }
  }
  return h.digest('hex');
}

function ultimoTeste() {
  try { return JSON.parse(fs.readFileSync(ULTIMO_TESTE, 'utf8')); } catch { return null; }
}

function gravarUltimoTeste(dados) {
  fs.mkdirSync(c.DADOS, { recursive: true });
  fs.writeFileSync(ULTIMO_TESTE, JSON.stringify(dados, null, 2));
}

/**
 * Decisão do portão no fim de uma tarefa. Puro (testável): bloqueia quando há mudança em src/tests e os testes não
 * passaram sobre exatamente esse código. Nunca bloqueia duas vezes seguidas (stop_hook_active).
 */
function decidir({ mudancas, impressaoAtual, ultimo, jaBloqueou }) {
  if (jaBloqueou || mudancas.length === 0) return { bloquear: false, motivo: mudancas.length === 0 ? 'sem mudanças em src/tests' : 'já bloqueado uma vez' };
  if (ultimo && ultimo.sucesso && ultimo.impressao === impressaoAtual) return { bloquear: false, motivo: 'testes passaram sobre o código atual' };
  return {
    bloquear: true,
    motivo: ultimo && !ultimo.sucesso
      ? 'a última execução dos testes falhou'
      : 'há mudanças em src/ ou tests/ sem execução dos testes de regressão sobre o código atual',
  };
}

module.exports = { alteracoes, impressao, ultimoTeste, gravarUltimoTeste, decidir, ULTIMO_TESTE };
