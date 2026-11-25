'use strict';
// O aviso DEP0190 (shell no Windows, necessário para chamar o npx.cmd) não interessa: os argumentos são montados aqui.
process.noDeprecation = true;
// Funções comuns do portão de mudanças (docs/desenvolvimento/portao.md).
// Fonte de verdade: documentos versionados no Git. O Ruflo guarda uma cópia para busca semântica e registra o uso.

const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
const { spawnSync } = require('node:child_process');

const RAIZ = path.resolve(__dirname, '..', '..');
const VERSAO_RUFLO = 'ruflo@3.80.6';
const DADOS = path.join(RAIZ, '.claude-flow', 'data'); // fora do Git (.gitignore)
const LOG = path.join(DADOS, 'portao.log');

const DOCS = {
  decisoes: path.join(RAIZ, 'docs', 'project-context.md'),
  invariantes: path.join(RAIZ, 'docs', 'business-rules', 'invariants.md'),
  regressoes: path.join(RAIZ, 'docs', 'desenvolvimento', 'regressoes.md'),
  mapa: path.join(RAIZ, 'docs', 'desenvolvimento', 'mapa-impacto.json'),
};

function ler(arquivo) {
  return fs.readFileSync(arquivo, 'utf8').replace(/\r\n/g, '\n');
}

/** Decisões numeradas de project-context.md ("1. ...", "6a. ..."). */
function decisoes() {
  const texto = ler(DOCS.decisoes);
  const inicio = texto.indexOf('## Decisões estabelecidas');
  const fim = texto.indexOf('\n## ', inicio + 5);
  const bloco = texto.slice(inicio, fim < 0 ? undefined : fim);
  return [...bloco.matchAll(/^(\d+[a-z]?)\. (.+)$/gm)].map((m) => ({ id: 'decisao-' + m[1], texto: m[2] }));
}

/** Invariantes numeradas de invariants.md. */
function invariantes() {
  return [...ler(DOCS.invariantes).matchAll(/^(\d+)\. (.+)$/gm)].map((m) => ({ id: 'INV-' + m[1], texto: m[2] }));
}

/** Linhas do registro de regressões. */
function regressoes() {
  return ler(DOCS.regressoes)
    .split('\n')
    .filter((l) => l.startsWith('| REG-'))
    .map((l) => l.split('|').map((c) => c.trim()))
    .map((c) => ({ id: c[1], componente: c[2], problema: c[3], correcao: c[4], teste: c[5], origem: c[6] }));
}

function mapaImpacto() {
  return JSON.parse(ler(DOCS.mapa));
}

function hashDocs() {
  const h = crypto.createHash('sha256');
  for (const k of ['decisoes', 'invariantes', 'regressoes']) h.update(ler(DOCS[k]));
  return h.digest('hex');
}

const BIN_LOCAL = path.join(RAIZ, 'node_modules', 'ruflo', 'bin', 'ruflo.js');

/**
 * Ambiente mínimo para o processo do Ruflo (revisão da F4.5, S3): nada de conexões, tokens ou chaves do terminal.
 * Só o necessário para o Node e o Windows funcionarem, mais as variáveis que desligam promoções e daemon.
 */
function ambienteMinimo() {
  const manter = ['PATH', 'Path', 'SystemRoot', 'SYSTEMROOT', 'windir', 'ComSpec', 'APPDATA', 'LOCALAPPDATA', 'USERPROFILE', 'HOME', 'TEMP', 'TMP', 'HOMEDRIVE', 'HOMEPATH'];
  const env = {};
  for (const k of manter) if (process.env[k] !== undefined) env[k] = process.env[k];
  return { ...env, RUFLO_FUNNEL: '0', RUFLO_DAEMON_AUTOSTART: '0', CLAUDE_FLOW_AUTO_UPDATE: 'false', npm_config_update_notifier: 'false', NODE_NO_WARNINGS: '1' };
}

/**
 * Chama a CLI do Ruflo. Preferência: o binário instalado no projeto (versão exata e dependências travadas em
 * package-lock.json), executado pelo Node SEM shell: nenhum texto passa por cmd.exe ou bash (revisão da F4.5, S1).
 * Sem a instalação local (`npm ci --ignore-scripts`), cai para o npx com a versão fixada; aí os argumentos já
 * chegam filtrados por seguro().
 */
function ruflo(args, timeoutMs = 60000) {
  const opcoes = { cwd: RAIZ, env: ambienteMinimo(), encoding: 'utf8', timeout: timeoutMs, windowsHide: true };
  const r = fs.existsSync(BIN_LOCAL)
    ? spawnSync(process.execPath, [BIN_LOCAL, ...args], { ...opcoes, shell: false })
    : spawnSync('npx', ['-y', VERSAO_RUFLO, ...args.map((a) => (/\s/.test(a) ? '"' + seguro(a, 2000) + '"' : seguro(a, 2000)))], { ...opcoes, shell: process.platform === 'win32' });
  return { ok: r.status === 0, saida: (r.stdout || '') + (r.stderr || ''), status: r.status, local: fs.existsSync(BIN_LOCAL) };
}

function jsonDaSaida(saida) {
  const i = saida.indexOf('{');
  const f = saida.lastIndexOf('}');
  if (i < 0 || f < i) return null;
  try { return JSON.parse(saida.slice(i, f + 1)); } catch { return null; }
}

/**
 * Texto que vai para a linha de comando (o npx no Windows passa por shell): só letras, números, espaço e pontuação
 * simples. Remove tudo o que o CMD ou o bash interpretam (& | ^ % < > " ' ` $ ! ; e quebras de linha).
 */
function seguro(texto, max = 300) {
  return String(texto).replace(/[^\p{L}\p{N} .,:()/@_+=?\-]/gu, ' ').replace(/\s+/g, ' ').trim().slice(0, max);
}

/** Antes de mandar uma consulta ao Ruflo: sem e-mails nem sequências longas de dígitos (CPF, telefone, cartão, tokens). */
function semDadosPessoais(texto) {
  return String(texto).replace(/\S+@\S+/g, ' ').replace(/[A-Za-z0-9_-]{24,}/g, ' ').replace(/\d[\d./ -]{6,}\d/g, ' ');
}

/** Busca semântica na memória do Ruflo. Nulo se o Ruflo não responder (o chamador usa só o Git). */
function buscar(consulta, limite = 8) {
  const q = seguro(semDadosPessoais(consulta));
  const r = ruflo(['memory', 'search', '-q', q, '--limit', String(limite), '--threshold', '0.3', '--format', 'json'], 30000);
  if (!r.ok) return null;
  const j = jsonDaSaida(r.saida);
  return j && Array.isArray(j.results) ? j.results.filter((x) => x.namespace !== 'prova') : null;
}

/** Resumo de uma consulta para o log: só hash e tamanho, nunca o texto (revisão da F4.5, S2). */
function resumoDeTexto(texto) {
  return { hash: crypto.createHash('sha256').update(String(texto)).digest('hex').slice(0, 16), tamanho: String(texto).length };
}

/** Registro local do que o portão fez (prova de funcionamento). Nunca guarda texto de mensagem nem segredos. */
function registrar(evento) {
  fs.mkdirSync(DADOS, { recursive: true });
  fs.appendFileSync(LOG, JSON.stringify({ em: new Date().toISOString(), ...evento }) + '\n');
}

function lerEntrada() {
  try { return JSON.parse(fs.readFileSync(0, 'utf8') || '{}'); } catch { return {}; }
}

module.exports = {
  RAIZ, DADOS, LOG, DOCS, VERSAO_RUFLO, BIN_LOCAL, seguro, semDadosPessoais, resumoDeTexto,
  decisoes, invariantes, regressoes, mapaImpacto, hashDocs, ruflo, jsonDaSaida, buscar, registrar, lerEntrada, ler,
};
