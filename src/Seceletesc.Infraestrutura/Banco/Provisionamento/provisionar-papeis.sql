-- Provisionamento dos papéis do banco. Executado UMA vez por um administrador do PostgreSQL,
-- nunca pela aplicação e nunca pelas migrações. Senhas são definidas depois, fora do Git:
--   alter role seceletesc_dono password '...';
-- Usuários de login da aplicação são criados pela infraestrutura como membros de
-- seceletesc_app ou seceletesc_worker.
--
-- Nenhum destes papéis é superusuário nem tem BYPASSRLS: superusuário ignora RLS mesmo com FORCE.
-- Ver docs/desenvolvimento/plano-tecnico.md, seção 4.5.

do $$
begin
  if not exists (select 1 from pg_roles where rolname = 'seceletesc_dono') then
    create role seceletesc_dono login nosuperuser nocreatedb nocreaterole nobypassrls;
  end if;
  if not exists (select 1 from pg_roles where rolname = 'seceletesc_app') then
    create role seceletesc_app nologin nosuperuser nocreatedb nocreaterole nobypassrls;
  end if;
  if not exists (select 1 from pg_roles where rolname = 'seceletesc_worker') then
    create role seceletesc_worker nologin nosuperuser nocreatedb nocreaterole nobypassrls;
  end if;
  -- API de supervisão humana (F2/F3): única que lê dados protegidos e altera status de conhecimento.
  if not exists (select 1 from pg_roles where rolname = 'seceletesc_supervisao') then
    create role seceletesc_supervisao nologin nosuperuser nocreatedb nocreaterole nobypassrls;
  end if;
end $$;

-- O dono cria e possui os objetos do schema da aplicação.
grant usage, create on schema public to seceletesc_dono;
