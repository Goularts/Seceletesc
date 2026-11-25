# Registro de regressões

Bugs reais encontrados (por revisão, mutação ou teste) e já corrigidos. Cada linha aponta o teste que impede a volta do problema.

- **Fonte de verdade:** este arquivo, no Git. A memória do Ruflo recebe uma cópia para busca semântica (`node ferramentas/portao/sincronizar.cjs`), mas não a substitui.
- **Verificado no CI:** o teste `RegressoesTestes` confere que cada teste citado existe e está ativo. Uma regressão sem teste, ou com teste apagado ou ignorado, quebra o build.
- **Como registrar:** `node ferramentas/portao/registrar.cjs regressao` (ver `docs/desenvolvimento/portao.md`) ou edite a tabela e rode a sincronização.

Formato da coluna "Teste": `NomeDoMetodo` exatamente como no código (sem parâmetros).

| ID | Componente | Problema | Correção | Teste | Origem |
|---|---|---|---|---|---|
| REG-001 | validador/dados-protegidos | E-mail da resposta automática conferido por trecho: com a fonte `financeiro@empresa.com.br`, passavam `ro@empresa.com.br` e `financeiro@empresa.com` | Comparação exata com os e-mails detectados na fonte citada | Email_que_nao_consta_na_fonte_citada_e_bloqueado | revisoes/2026-11-25-f3.md (G1) |
| REG-002 | worker/conhecimento | Revogação em andamento e envio se cruzavam: a mensagem saía com a fonte já revogada | Trava consultiva compartilhada no worker e exclusiva na revogação | S4_envio_espera_a_revogacao_em_andamento_e_nao_usa_a_fonte_revogada | revisoes/2026-11-25-f3.md (S4) |
| REG-003 | conhecimento/papeis | Supervisor editava e reaprovava como PUB um NEG aprovado pelo administrador | Conhecimento de maior risco só muda com administrador; supervisor nem o vê | G2_supervisor_nao_rebaixa_conhecimento_comercial_aprovado_pelo_administrador | revisoes/2026-11-25-f3.md (G2) |
| REG-004 | validador/aviso | Aviso de transferência saía sem validação de conteúdo (prazo, valor, compromisso) | O aviso passa pelas regras de conteúdo na publicação e ao enfileirar | Aviso_de_transferencia_com_conteudo_proibido_nao_sai | revisoes/2026-11-25-f2.md (G1) |
| REG-005 | banco/identidade | Banco aceitava resposta humana gravada em nome de outro usuário ativo | Autor precisa ser o usuário comprovado na transação | Banco_recusa_resposta_humana_em_nome_de_outro_usuario | revisoes/2026-11-25-pos-f3.md (P2, mutante D2) |
| REG-006 | conhecimento/dados-protegidos | CEP de cliente entrava em conhecimento por escopo sugerido ou por edição | Só CEP institucional da política entra em conhecimento | G1_cep_nao_entra_em_conhecimento_nem_avulso | revisoes/2026-11-25-pos-f3.md (G1, S5) |
| REG-007 | api/autenticacao | Caminho em maiúsculas (`/SUPERVISAO/...`) pulava HTTPS, autenticação e limite de corpo | Perímetro compara sem diferenciar maiúsculas | S3_caminho_em_maiusculas_tambem_passa_pela_autenticacao | revisoes/2026-11-25-pos-f3.md (S3) |
| REG-008 | worker/atribuicao | Resposta de atendente na fila saía depois de a conversa ser reatribuída | Worker confere autor ativo e atribuição no envio | S8_resposta_de_atendente_nao_sai_se_a_conversa_foi_reatribuida_antes_do_envio | revisoes/2026-11-25-pos-f3.md (S8) |
| REG-009 | ia/fila | Mensagem com outra mais nova saía da fila sem resposta, sem supervisão e sem registro | Agrupamento registrado; fora de ordem é reconciliada; sem resposta válida, supervisão | Mensagem_fora_de_ordem_e_reconciliada_com_o_historico_completo_e_registrada | revisoes/2026-11-25-f4.md (G1) |
| REG-010 | webhook/lote | Um evento com erro derrubava o lote inteiro, inclusive um eco posterior | Ecos primeiro; evento com erro de dado é recusado sem derrubar o lote | S1_no_mesmo_lote_o_eco_vale_antes_da_mensagem_do_cliente | revisoes/2026-11-25-f4.md (S1) |
| REG-011 | webhook/eco | Eco em conversa já humana não subia a versão; liberação antiga passava sem ver a resposta do celular | O eco sempre sobe a versão | G2_eco_em_conversa_ja_humana_invalida_liberacao_com_a_versao_antiga | revisoes/2026-11-25-f4.md (G2) |
| REG-012 | ia/fila | Fila da IA sem RLS: a aplicação via e apagava itens de outra empresa | RLS por empresa na fila | S2_aplicacao_nao_ve_nem_apaga_a_fila_da_IA_de_outra_empresa | revisoes/2026-11-25-f4.md (S2) |
| REG-013 | gateway | Número de saída vinha só da configuração, sem conferir o cadastro da empresa | Gateway confere o número no banco antes de enviar | S3_gateway_nao_envia_por_numero_que_nao_e_da_empresa | revisoes/2026-11-25-f4.md (S3) |
| REG-014 | politica/dados-protegidos | Elemento nulo nas listas da política gerava erro interno (500) | Recusado como formato inválido (422) | S10_politica_com_elemento_nulo_e_recusada_sem_erro_interno | revisoes/2026-11-25-pos-f3.md (S10) |
| REG-015 | ia/fila | Reação gravada como mensagem do cliente desfazia o agrupamento e gerava falso fora de ordem (duas respostas) | Reação não conta como mensagem mais nova nem vai ao motor | Reacao_nao_desfaz_o_agrupamento_nem_vai_ao_motor | revisoes/2026-11-25-f4-5.md (G2, S7) |
| REG-016 | ia/fila | Mensagem mais nova respondida durante a geração de uma antiga: a resposta da antiga saía sem ter visto a nova | Na decisão, mais nova respondida depois do contexto manda para supervisão | Mensagem_mais_nova_respondida_durante_a_geracao_manda_a_antiga_para_supervisao | revisoes/2026-11-25-f4-5.md (G1) |
| REG-017 | ia/fila | Falha na detecção de dados protegidos numa mensagem agrupada não chegava à supervisão | Falha de detecção é tratada antes do agrupamento | Falha_de_deteccao_em_mensagem_agrupada_vai_para_supervisao | revisoes/2026-11-25-f4-5.md (G4) |
| REG-018 | conhecimento/politica | Relógios diferentes na aprovação (aplicação) e no uso (banco) tiravam de uso conhecimento aprovado no limite do prazo | Prazo conferido pelo relógio do banco nos dois pontos | Conhecimento_aprovado_no_limite_do_prazo_continua_em_uso | revisoes/2026-11-25-f4-5.md (G3) |
