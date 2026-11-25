# Seceletesc

Plataforma de atendimento inteligente por WhatsApp com supervisão humana e conhecimento controlado.

- Regras do produto: [`docs/business-rules/`](docs/business-rules/)
- Contexto e decisões: [`docs/project-context.md`](docs/project-context.md)
- Plano técnico: [`docs/desenvolvimento/plano-tecnico.md`](docs/desenvolvimento/plano-tecnico.md)
- Qualidade e testes: [`docs/desenvolvimento/qualidade.md`](docs/desenvolvimento/qualidade.md)

## Requisitos

- .NET SDK 10
- Docker (os testes de integração sobem um PostgreSQL real via Testcontainers)

## Rodar os testes (CMD)

```cmd
dotnet test
```

## Banco local (CMD)

```cmd
docker compose up -d --wait
```

O PostgreSQL local fica em `127.0.0.1:55432` (mude com a variável `SECELETESC_DB_PORTA`). O superusuário do container só provisiona os papéis; a aplicação usa `app_local`, as migrações usam `seceletesc_dono`.
