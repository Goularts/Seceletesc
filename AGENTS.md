# Instruções para Agentes

Antes de trabalhar neste repositório, leia:

1. `docs/project-context.md`
2. `docs/business-rules/invariants.md`, `operacao-chatbot.md` e `governanca-conhecimento.md`
3. `docs/desenvolvimento/qualidade.md`

## Duas coisas diferentes

- **Desenvolvimento do software: autonomia.** Crie, altere, refatore, organize, teste e decida tecnicamente sem pedir aprovação a cada passo. Siga `docs/desenvolvimento/qualidade.md`.
- **Comportamento do chatbot e conhecimento: controle rigoroso.** As regras de `docs/business-rules/` são requisitos do produto. Não invente nem amplie comportamento funcional do chatbot; lacunas funcionais vão para "Decisões pendentes" e pedem definição do Victor.

## Regras

- Implemente as restrições do chatbot como controles do backend (determinísticos sempre que possível), nunca só em prompts.
- Nenhum caminho de código pode enviar mensagem ao cliente sem a validação do backend imediatamente antes do envio.
- Nenhum caminho de código pode publicar conhecimento sem aprovação humana explícita.
- Preserve o isolamento entre empresas e entre clientes.
- Proteja as regras A e B com testes que verificam a decisão do backend. Não enfraqueça um teste de regra para fazer uma implementação passar.
- Peça confirmação só para: operações destrutivas, exposição de dados, infraestrutura externa ou configuração global, e mudanças no comportamento funcional do chatbot.
- Etapa atual: preparação. Apresente um plano técnico consolidado antes de implementar funcionalidades.
