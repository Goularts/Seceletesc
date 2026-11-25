# Integração com o WhatsApp: requisitos antes de conectar o número real

**Status:** preparação (F4). Nenhum número real conectado e nenhuma mensagem externa enviada. Conectar exige autorização explícita do Victor (decisão 31).

## 1. O que precisa ser decidido ou fornecido pelo Victor

| # | Item | Por quê | Quem |
|---|---|---|---|
| 1 | **Portfólio empresarial na Meta** (Business Manager) verificado | exigido para usar a Cloud API em produção | Victor |
| 2 | **Conta do WhatsApp Business (WABA)** e o número que será usado | o número que o cliente já conhece | Victor |
| 3 | **Caminho de cadastro do número** (ver seção 2): coexistência (app + API) ou só API | muda como o humano responde e como funciona a retomada fora da janela | Victor |
| 4 | **App da Meta** (developers.facebook.com) com o produto WhatsApp | gera o App Secret (assinatura do webhook) e recebe o webhook | equipe, com acesso do Victor |
| 5 | **Usuário de sistema e token permanente** com `whatsapp_business_messaging` e `whatsapp_business_management` | o envio pela API; o token fica só no ambiente do servidor | Victor gera; nunca vai para o Git |
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
| `WhatsApp:TokenAcesso` | token permanente do usuário de sistema, usado só pelo gateway de envio |
| `WhatsApp:EnvioHabilitado` | desligado por padrão: sem ele, nada sai para a Meta |
| Campos assinados no webhook | `messages` (mensagens e status) e, com coexistência, `smb_message_echoes` |
| `tenant_numeros_whatsapp` | liga o `phone_number_id` do número à empresa (cadastro pelo administrador do banco) |

## Fontes consultadas

- [360dialog: Coexistence](https://docs.360dialog.com/docs/resources/phone-numbers/coexistence)
- [360dialog: Coexistence Webhooks](https://docs.360dialog.com/partner/onboarding/whatsapp-coexistence/coexistence-webhooks)
- [Dualhook: smb_message_echoes](https://dualhook.com/docs/webhook-smb-message-echoes)
- [Dualhook: Coexistence](https://dualhook.com/docs/coexistence)
- [Referência no GitHub (COEXISTENCE.md)](https://github.com/bellopushon/whatsapp-cloud-api/blob/main/skills/whatsapp-cloud-api/references/COEXISTENCE.md)
- [8x8: WhatsApp Business App Coexistence](https://developer.8x8.com/connect/docs/whatsapp/whatsapp-business-app-coexistence)
- [Infobip: Coexistence](https://www.infobip.com/docs/whatsapp/manage-integration/coexistence)
- [DEV Community: Coexistence for developers](https://dev.to/wapgrowai_org/whatsapp-coexistence-for-developers-when-the-business-app-and-the-cloud-api-share-a-number-1c4a)
