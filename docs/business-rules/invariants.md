# Invariantes de Regras de Negócio

Estas regras são obrigatórias. Qualquer alteração futura deve preservá-las e ser avaliada conforme os controles de testes, revisão independente e repositório definidos para o projeto.

1. O atendimento ao cliente deve ocorrer de forma conversacional natural, sem menus ou URA.
2. A IA só pode responder autonomamente quando possuir informações e autorização suficientes.
3. Ao identificar um impasse, a IA deve transferir o controle para supervisão humana via API.
4. A supervisão humana pode responder ao cliente pelo mesmo número de WhatsApp ou fornecer orientação interna ao agente.
5. Enquanto um atendimento estiver sob controle humano, a IA não pode enviar mensagens ao cliente.
6. Todo histórico de atendimento deve ser estruturado, auditável e persistente.
7. A IA pode sugerir conhecimentos e classificações, mas não pode aprová-los nem publicá-los.
8. Conhecimento reutilizável só pode ser aprovado por humano autorizado.
9. Todo conhecimento deve possuir escopo, classificação, validade, versionamento e permissões.
10. Informações comerciais, preços, negociações, exceções e propostas fora do escopo devem estar sujeitas a controles específicos.
11. Dados de tenants devem permanecer isolados entre si.
12. As regras de negócio devem ser protegidas por testes automatizados, revisão independente e controles de repositório.

## Aplicação durante a evolução do projeto

- Não introduza comportamento que contradiga uma invariante.
- Quando uma regra depender de detalhes ainda pendentes, registre a lacuna e peça definição; não a preencha por suposição.
- Trate qualquer mudança nestas regras como mudança explícita de requisito, sujeita à aprovação humana.
