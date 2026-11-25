#!/bin/sh
# Usuários de login do ambiente local (só desenvolvimento). Roda depois de 01-papeis.sql.
set -eu
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
  -v senha="$SECELETESC_DB_SENHA_LOCAL" <<'SQL'
alter role seceletesc_dono password :'senha';
create role app_local login password :'senha' in role seceletesc_app;
create role worker_local login password :'senha' in role seceletesc_worker;
create role supervisao_local login password :'senha' in role seceletesc_supervisao;
-- A supervisão apresenta a chave do usuário ao banco em cada transação: parâmetros nunca vão ao log do servidor.
alter role supervisao_local set log_parameter_max_length = 0;
alter role supervisao_local set log_parameter_max_length_on_error = 0;
SQL
