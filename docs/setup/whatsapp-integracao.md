# Integração com o WhatsApp: requisitos antes de conectar o número real

**Status:** F4 e M1 implementadas sem número real (webhook, fila da IA, eco, status; contas, credenciais e números por empresa, decisão 34; gateway desligado). Nenhum número real conectado e nenhuma mensagem externa enviada. Conectar exige autorização explícita do Victor (decisões 31 e 34).

## 1. O que precisa ser decidido ou fornecido pelo Victor

| # | Item | Por quê | Quem |
|---|---|---|---|
| 1 | **Portfólio empresarial na Meta** (Business Manager) verificado | exigido para usar a Cloud API em produção | Victor |
| 2 | **Conta do WhatsApp Business (WABA)** e os números de cada empresa cliente | os números que os clientes já conhecem | cada empresa cliente, cadastrada por um operador da plataforma |
| 3 | **Caminho de cadastro do número** (ver seção 2) | coexistência (app + API) **desligada por ora** (decisão 34b), sem impedir a implementação futura | decidido |
| 4 | **App da Meta** (developers.facebook.com) com o produto WhatsApp | gera o App Secret (assinatura do webhook) e recebe o webhook | equipe, com acesso do Victor |
| 5 | **Acesso avançado** a `whatsapp_business_messaging` e `whatsapp_business_management` (App Review) e um **business token por empresa cliente** | Tech Provider usa só business tokens; cada um fica cifrado no banco, com a chave fora do banco (M1) | Victor pede o acesso; o token de cada cliente é registrado pelo operador (`GUIA-ADMINISTRADOR.md`, B3); nunca vai para o Git |
| 6 | **Hospedagem com HTTPS público** para o webhook e a API de supervisão | a Meta só chama URL HTTPS; a supervisão exige HTTPS (decisão 30) | infraestrutura externa: decisão do Victor |
| 7 | **Número de teste da Meta** para a primeira conexão | testar ponta a ponta sem cliente real | equipe, com autorização do Victor |
| 8 | **Autenticação de produção** da API de supervisão | pré-requisito para expor a API (decisão 30) | decisão pendente |

## 2. Coexistência (WhatsApp Business App + Cloud API no mesmo número)

**Não presumir que está disponível** (decisão 31). A verificação é feita no próprio cadastro (Embedded Signup), com o número real, por um provedor de tecnologia credenciado pela Meta (Tech Provider ou BSP) ou pelo nosso app, se ele for habilitado como Tech Provider.

O que a pesquisa encontrou (fontes de provedores, não da Meta; **confirmar na documentação oficial e no cadastro**):

- **Disponibilidade:** o Brasil não aparece nas listas de países excluídos que encontramos (as listas citam, entre outros, Nigéria, África do Sul e, em algumas fontes, UE/EEE/Reino Unido). As listas divergem entre si.
- **Pré-requisitos citados:** número já ativo no WhatsApp Business App, em versão 2.24.17 ou mais nova; o cadastro sincroniza histórico e contatos uma vez e desconecta os aparelhos vinculados (podem ser reconectados os compatíveis).
- **Manutenção citada:** abrir o app pelo menos a cada 13 dias para manter a coexistência ativa.
- **Ecos:** mensagens enviadas pelo app chegam ao webhook como eventos `smb_message_echoes`. Mensagens enviadas por aparelhos vinculados não compatíveis **não** geram eco.
- **Janela de 24h:** mensagem enviada pelo app **não abre nem estende** a janela de atendimento da API. A janela continua sendo contada pela última mensagem do cliente, como o sistema já faz.
- **Vazão:** limite citado de 20 mensagens por segundo no número em coexistência (suficiente para o volume esperado).

Impactos para o sistema:

- Cada eco coloca a conversa sob controle humano e é registrado no histórico (invariante 24). Isso já está no plano da F4.
- **Risco:** mensagem enviada por um aparelho vinculado não compatível não gera eco. O sistema não saberia que um humano respondeu, e a IA poderia responder depois. Mitigação proposta: orientar a equipe a responder só pelo app principal; registrar como risco do piloto.

## 3. Alternativa sem coexistência (só Cloud API)

Se a coexistência não estiver disponível para o número, o número passa a operar só pela API (deixa de funcionar no app do celular) e o atendimento humano é feito **pelo painel ligado à API**, com o mesmo número perante o cliente (decisão 31). O sistema já suporta isso: `POST /supervisao/conversas/{id}/responder`.

**Ponto que exige decisão do Victor:** sem coexistência e sem templates (decisão 6d), **não há como retomar uma conversa fora da janela de 24h**: a API só permite texto livre dentro da janela, e o celular não está disponível. Opções: (a) aceitar que, fora da janela, só se volta a falar quando o cliente escrever; (b) autorizar templates de retomada, revendo a decisão 6d; (c) usar outro número ou canal para a retomada, o que muda a identidade perante o cliente.

## 4. Configuração técnica (quando autorizado)

Nenhum destes valores vai para o Git. Em desenvolvimento: user-secrets; em produção: variáveis de ambiente ou cofre.

| Configuração | Uso |
|---|---|
| `WhatsApp:AppSecret` | verificar a assinatura `X-Hub-Signature-256` de cada webhook |
| `WhatsApp:TokenVerificacao` | responder ao desafio de verificação do webhook (GET) |
| `WhatsApp:ChaveCredenciais` | chave (base64, 32 bytes) que cifra no banco a credencial (business token) de cada empresa; sem ela, nenhum envio é registrado (M1, decisão 34) |
| `WhatsApp:EnvioHabilitado` | interruptor geral, desligado por padrão: sem ele, nada sai para a Meta. Cada número também precisa de autorização operacional e envio habilitado por um operador |
| `WhatsApp:ProcessamentoHabilitado` | liga o trabalhador da fila da IA (até a F5, tudo vai para supervisão) |
| Conta (WABA), credencial e números | por empresa, no banco, pelos comandos do operador da plataforma (`GUIA-ADMINISTRADOR.md`, B3). A resposta sai pelo número em que o cliente escreveu por último |
| `ConnectionStrings:App` e `ConnectionStrings:Worker` | papéis `seceletesc_app` (webhook, IA) e `seceletesc_worker` (envio) |
| `DadosProtegidos:ChavePublicaPem` | chave pública para cifrar dados protegidos; sem ela, o webhook não registra mensagens |
| Campos assinados no webhook | `messages` (mensagens e status) e, com coexistência, `smb_message_echoes` |
| `tenant_numeros_whatsapp` | liga o `phone_number_id` à empresa e à WABA, com a autorização operacional e o envio de cada número |

## 4a. Pré-requisitos de operação (revisão da F4)

- `WhatsApp:ProcessamentoHabilitado` ligado sempre que o webhook estiver recebendo: desligado, toda mensagem vai direto para supervisão.
- Com o envio desligado, nenhuma mensagem sai, **inclusive respostas humanas pelo painel**: não conectar o número real com o envio desligado e humanos respondendo pelo painel.
- Confirmar a assinatura de `smb_message_echoes` e o número em `tenant_numeros_whatsapp` antes de ligar o processamento: sem o eco, a resposta pelo celular não silencia a IA (invariante 24).
- Connection strings de produção sem `Include Error Detail`; papel de login da supervisão com `log_parameter_max_length = 0`.
- Limite de taxa do webhook: 600 pedidos por minuto por IP; no máximo 1.000 eventos por lote. Atrás de proxy, configurar `Seguranca:ProxiesConfiaveis` para o IP real.
- Risco do piloto: dado de identificação digitado pelo humano no celular sai sem passar pelo backend (só é mascarado no histórico); aparelhos vinculados não compatíveis não geram eco.

## 5. Roteiro de conexão (quando autorizado)

1. Victor fornece os itens da seção 1 e decide o caminho da seção 2 ou 3.
2. Cadastrar o número de teste da Meta em `tenant_numeros_whatsapp` (administrador do banco) e configurar os segredos (seção 4) fora do Git.
3. Publicar a API com HTTPS e registrar a URL do webhook no app da Meta (campos `messages` e, com coexistência, `smb_message_echoes`).
4. Com `EnvioHabilitado` desligado e `ProcessamentoHabilitado` ligado: enviar mensagens do celular de teste e conferir registro, mascaramento, fila e supervisão (até a F5, toda mensagem vai para supervisão).
5. Só então, com autorização explícita, ligar `EnvioHabilitado` para o número de teste e validar envio, status e janela de 24h.
6. Número real: nova autorização.

## Fontes consultadas

- [360dialog: Coexistence](https://docs.360dialog.com/docs/resources/phone-numbers/coexistence)
- [360dialog: Coexistence Webhooks](https://docs.360dialog.com/partner/onboarding/whatsapp-coexistence/coexistence-webhooks)
- [Dualhook: smb_message_echoes](https://dualhook.com/docs/webhook-smb-message-echoes)
- [Dualhook: Coexistence](https://dualhook.com/docs/coexistence)
- [Referência no GitHub (COEXISTENCE.md)](https://github.com/bellopushon/whatsapp-cloud-api/blob/main/skills/whatsapp-cloud-api/references/COEXISTENCE.md)
- [8x8: WhatsApp Business App Coexistence](https://developer.8x8.com/connect/docs/whatsapp/whatsapp-business-app-coexistence)
- [Infobip: Coexistence](https://www.infobip.com/docs/whatsapp/manage-integration/coexistence)
- [DEV Community: Coexistence for developers](https://dev.to/wapgrowai_org/whatsapp-coexistence-for-developers-when-the-business-app-and-the-cloud-api-share-a-number-1c4a)
