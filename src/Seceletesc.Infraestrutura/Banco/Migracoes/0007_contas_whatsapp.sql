-- M1 (decisão 34): contas do WhatsApp por empresa. Cada empresa cliente conecta a própria WABA, com os próprios
-- números e a própria credencial (business token). Executado por seceletesc_dono. Ver plano técnico, seção 4.13.
--
-- Três etapas separadas por número, cada uma registrada no histórico com operador e motivo:
--   1. conexão: o número pertence a uma WABA conectada da empresa, com credencial vigente;
--   2. autorização operacional: um operador da plataforma confirma que o número pode operar;
--   3. envio habilitado: um operador liga o envio (desligado por padrão, decisão 34c).
-- O envio só sai com as três, conferidas no momento do envio pela função credencial_envio.
-- Nesta etapa, os operadores agem pela linha de comando, com a conexão do dono; o papel de operador com
-- autenticação própria vem com a API de cadastro (M3).

-- ---------------------------------------------------------------- contas (WABA)
create table contas_whatsapp (
  waba_id          text primary key,  -- uma WABA pertence a uma única empresa
  tenant_id        uuid not null references tenants(id),
  business_id      text,
  situacao         text not null default 'conectada' check (situacao in ('conectada', 'desconectada')),
  conectada_em     timestamptz not null default now(),
  desconectada_em  timestamptz,
  unique (tenant_id, waba_id),
  check (waba_id ~ '^(\d{5,30}|demo-[a-f0-9]{4,32})$'),
  check (business_id is null or business_id ~ '^\d{5,30}$'),
  check ((situacao = 'desconectada') = (desconectada_em is not null))
);
alter table contas_whatsapp enable row level security;
alter table contas_whatsapp force row level security;
create policy contas_whatsapp_isolamento on contas_whatsapp using (tenant_id = tenant_atual()) with check (tenant_id = tenant_atual());

-- ---------------------------------------------------------------- credenciais (business token cifrado)
-- O banco guarda só o texto cifrado (AES-256-GCM, nonce || tag || cifra), com dados associados que ligam a cifra à
-- empresa e à WABA: copiar a linha para outra empresa não produz um token válido. A chave fica fora do banco.
-- Nenhum papel da aplicação lê esta tabela: o worker de envio recebe a cifra só pela função credencial_envio.
create table credenciais_whatsapp (
  id             uuid primary key,
  tenant_id      uuid not null,
  waba_id        text not null,
  token_cifrado  bytea not null check (octet_length(token_cifrado) between 29 and 8192),
  chave_id       text not null check (chave_id ~ '^[a-f0-9]{16}$'),
  registrada_em  timestamptz not null default now(),
  revogada_em    timestamptz,
  foreign key (tenant_id, waba_id) references contas_whatsapp (tenant_id, waba_id)
);
create unique index uq_credencial_vigente on credenciais_whatsapp (waba_id) where revogada_em is null;
alter table credenciais_whatsapp enable row level security;
alter table credenciais_whatsapp force row level security;
create policy credenciais_whatsapp_isolamento on credenciais_whatsapp using (tenant_id = tenant_atual()) with check (tenant_id = tenant_atual());

-- Credencial é imutável; só pode ser revogada, uma vez.
create function proteger_credencial() returns trigger
language plpgsql set search_path = public, pg_temp as $$
begin
  if tg_op = 'DELETE' then
    raise exception 'Credencial não se apaga: revogue' using errcode = 'P0001';
  end if;
  if old.revogada_em is not null
     or new.revogada_em is null
     or (new.id, new.tenant_id, new.waba_id, new.token_cifrado, new.chave_id, new.registrada_em)
        is distinct from (old.id, old.tenant_id, old.waba_id, old.token_cifrado, old.chave_id, old.registrada_em) then
    raise exception 'Credencial é imutável: só pode ser revogada, uma vez' using errcode = 'P0001';
  end if;
  return new;
end $$;
create trigger tg_credencial_protecao before update or delete on credenciais_whatsapp
  for each row execute function proteger_credencial();

-- ---------------------------------------------------------------- números
-- A validação de chave estrangeira roda como o dono, sob o RLS forçado, que exige uma empresa na transação. As
-- colunas novas começam nulas (nenhuma linha a validar); uma empresa inexistente só serve à validação e é
-- desfeita logo depois das chaves.
select set_config('app.tenant_id', '00000000-0000-0000-0000-000000000000', true);
alter table tenant_numeros_whatsapp
  add column waba_id                 text,
  add column operacao_autorizada_em  timestamptz,
  add column operacao_autorizada_por text,
  add column operacao_motivo         text,
  add column envio_habilitado        boolean not null default false,
  add column envio_alterado_em       timestamptz,
  add column envio_alterado_por      text,
  add column envio_motivo            text,
  add constraint uq_numeros_tenant unique (tenant_id, phone_number_id),
  add constraint fk_numero_conta foreign key (tenant_id, waba_id) references contas_whatsapp (tenant_id, waba_id),
  add constraint ck_envio_exige_autorizacao check (not envio_habilitado or (waba_id is not null and operacao_autorizada_em is not null)),
  add constraint ck_autorizacao_completa check (
    (operacao_autorizada_em is null and operacao_autorizada_por is null)
    or (operacao_autorizada_em is not null and coalesce(btrim(operacao_autorizada_por), '') <> '' and coalesce(btrim(operacao_motivo), '') <> ''));

-- Empresa e número são imutáveis; a WABA só é definida uma vez; ligar o envio exige conta conectada com credencial.
create function proteger_numero_whatsapp() returns trigger
language plpgsql set search_path = public, pg_temp as $$
begin
  if tg_op = 'DELETE' then
    raise exception 'Número não se apaga: desabilite' using errcode = 'P0001';
  end if;
  if new.tenant_id <> old.tenant_id or new.phone_number_id <> old.phone_number_id then
    raise exception 'Empresa e número são imutáveis' using errcode = 'P0001';
  end if;
  if old.waba_id is not null and new.waba_id is distinct from old.waba_id then
    raise exception 'A conta (WABA) do número não muda' using errcode = 'P0001';
  end if;
  if new.envio_habilitado and not old.envio_habilitado and not exists (
       select 1 from contas_whatsapp a
         join credenciais_whatsapp k on k.waba_id = a.waba_id and k.revogada_em is null
        where a.waba_id = new.waba_id and a.tenant_id = new.tenant_id and a.situacao = 'conectada') then
    raise exception 'Habilitar o envio exige conta conectada com credencial vigente' using errcode = 'P0001';
  end if;
  return new;
end $$;
create trigger tg_numero_protecao before update or delete on tenant_numeros_whatsapp
  for each row execute function proteger_numero_whatsapp();

-- ---------------------------------------------------------------- número de origem da conversa
-- O envio sai pelo número em que o cliente escreveu por último (a janela de 24h é contada da mesma mensagem).
-- A chave composta impede apontar para um número de outra empresa. Só a aplicação (registro da mensagem do
-- cliente) muda esse campo: nem a supervisão nem o worker escolhem o número de saída.
alter table conversas add column numero_empresa text;
alter table conversas add constraint fk_conversa_numero foreign key (tenant_id, numero_empresa)
  references tenant_numeros_whatsapp (tenant_id, phone_number_id);
select set_config('app.tenant_id', '', true);

create function proteger_numero_da_conversa() returns trigger
language plpgsql set search_path = public, pg_temp as $$
begin
  if new.numero_empresa is distinct from old.numero_empresa
     and (not pg_has_role(current_user, 'seceletesc_app', 'member')
          or pg_has_role(current_user, 'seceletesc_supervisao', 'member')
          or new.numero_empresa is null) then
    raise exception 'O número de origem da conversa só muda com a mensagem do cliente' using errcode = 'P0001';
  end if;
  return new;
end $$;
create trigger tg_conversa_numero before update on conversas
  for each row execute function proteger_numero_da_conversa();

-- ---------------------------------------------------------------- credencial para o envio
-- Devolve a cifra da credencial só quando as três etapas estão cumpridas para o número, na empresa da transação.
-- Sem linha = sem envio. A decifragem acontece fora do banco, com a chave da configuração.
create function credencial_envio(p_phone_number_id text) returns table (waba_id text, token_cifrado bytea, chave_id text)
language sql stable security definer set search_path = public, pg_temp as $$
  select k.waba_id, k.token_cifrado, k.chave_id
    from tenant_numeros_whatsapp n
    join contas_whatsapp a on a.waba_id = n.waba_id and a.tenant_id = n.tenant_id
    join credenciais_whatsapp k on k.waba_id = a.waba_id and k.tenant_id = a.tenant_id and k.revogada_em is null
   where n.phone_number_id = p_phone_number_id
     and n.tenant_id = tenant_atual()
     and n.operacao_autorizada_em is not null
     and n.envio_habilitado
     and a.situacao = 'conectada'
$$;

-- ---------------------------------------------------------------- operações da plataforma (auditadas)
-- Só o dono executa (linha de comando do operador). Cada uma exige a empresa da transação igual à informada,
-- operador identificado e, quando libera algo, motivo; e grava o evento no histórico da empresa na mesma transação.
create function exigir_operacao_plataforma(p_tenant uuid, p_operador text, p_motivo text, p_exige_motivo boolean) returns void
language plpgsql set search_path = public, pg_temp as $$
begin
  if tenant_atual() is distinct from p_tenant then
    raise exception 'Empresa da transação diferente da informada' using errcode = 'P0001';
  end if;
  if coalesce(btrim(p_operador), '') = '' or length(p_operador) > 200 then
    raise exception 'Operador da plataforma não identificado' using errcode = 'P0001';
  end if;
  if p_exige_motivo and (coalesce(btrim(p_motivo), '') = '' or length(p_motivo) > 500) then
    raise exception 'Motivo obrigatório' using errcode = 'P0001';
  end if;
end $$;

create function plataforma_conectar_conta(p_tenant uuid, p_waba text, p_business text, p_operador text) returns void
language plpgsql set search_path = public, pg_temp as $$
begin
  perform exigir_operacao_plataforma(p_tenant, p_operador, null, false);
  -- O RLS esconde as contas das outras empresas: se a WABA for de outra, o insert esbarra na chave primária (23505).
  update contas_whatsapp set situacao = 'conectada', desconectada_em = null, business_id = coalesce(p_business, business_id)
   where waba_id = p_waba and tenant_id = p_tenant;
  if not found then
    insert into contas_whatsapp (waba_id, tenant_id, business_id) values (p_waba, p_tenant, p_business);
  end if;
  insert into eventos_historico (tenant_id, conversa_id, tipo, dados)
  values (p_tenant, null, 'conta_whatsapp_conectada', jsonb_build_object('waba_id', p_waba, 'business_id', p_business, 'operador', p_operador));
end $$;

create function plataforma_registrar_credencial(p_tenant uuid, p_waba text, p_token_cifrado bytea, p_chave_id text, p_operador text) returns void
language plpgsql set search_path = public, pg_temp as $$
begin
  perform exigir_operacao_plataforma(p_tenant, p_operador, null, false);
  if not exists (select 1 from contas_whatsapp where waba_id = p_waba and tenant_id = p_tenant and situacao = 'conectada') then
    raise exception 'Conta (WABA) não conectada nesta empresa' using errcode = 'P0001';
  end if;
  update credenciais_whatsapp set revogada_em = now() where waba_id = p_waba and revogada_em is null;
  insert into credenciais_whatsapp (id, tenant_id, waba_id, token_cifrado, chave_id)
  values (gen_random_uuid(), p_tenant, p_waba, p_token_cifrado, p_chave_id);
  -- Nada da credencial vai para o histórico: só que ela foi trocada, por quem e com qual chave.
  insert into eventos_historico (tenant_id, conversa_id, tipo, dados)
  values (p_tenant, null, 'credencial_whatsapp_registrada', jsonb_build_object('waba_id', p_waba, 'chave_id', p_chave_id, 'operador', p_operador));
end $$;

create function plataforma_desconectar_conta(p_tenant uuid, p_waba text, p_operador text, p_motivo text) returns void
language plpgsql set search_path = public, pg_temp as $$
declare
  numero record;
begin
  perform exigir_operacao_plataforma(p_tenant, p_operador, p_motivo, true);
  update contas_whatsapp set situacao = 'desconectada', desconectada_em = now()
   where waba_id = p_waba and tenant_id = p_tenant and situacao = 'conectada';
  if not found then
    raise exception 'Conta (WABA) conectada não encontrada nesta empresa' using errcode = 'P0001';
  end if;
  update credenciais_whatsapp set revogada_em = now() where waba_id = p_waba and revogada_em is null;
  for numero in
    update tenant_numeros_whatsapp set envio_habilitado = false, envio_alterado_em = now(), envio_alterado_por = p_operador,
                                       envio_motivo = 'conta desconectada: ' || p_motivo
     where waba_id = p_waba and tenant_id = p_tenant and envio_habilitado
    returning phone_number_id
  loop
    insert into eventos_historico (tenant_id, conversa_id, tipo, dados)
    values (p_tenant, null, 'envio_whatsapp_desabilitado', jsonb_build_object('phone_number_id', numero.phone_number_id, 'operador', p_operador, 'motivo', 'conta desconectada'));
  end loop;
  insert into eventos_historico (tenant_id, conversa_id, tipo, dados)
  values (p_tenant, null, 'conta_whatsapp_desconectada', jsonb_build_object('waba_id', p_waba, 'operador', p_operador, 'motivo', p_motivo));
end $$;

-- Registra o número na empresa, ligado (ou não, só para receber) a uma WABA da empresa. Número já registrado sem
-- WABA pode ser ligado depois; nunca troca de empresa nem de WABA.
create function plataforma_registrar_numero(p_tenant uuid, p_phone text, p_waba text, p_operador text) returns void
language plpgsql set search_path = public, pg_temp as $$
declare
  existente tenant_numeros_whatsapp;
begin
  perform exigir_operacao_plataforma(p_tenant, p_operador, null, false);
  select * into existente from tenant_numeros_whatsapp where phone_number_id = p_phone;
  if existente.phone_number_id is null then
    insert into tenant_numeros_whatsapp (phone_number_id, tenant_id, waba_id) values (p_phone, p_tenant, p_waba);
  elsif existente.tenant_id <> p_tenant then
    raise exception 'Este número já está ligado a outra empresa' using errcode = '23505';
  elsif p_waba is not null and existente.waba_id is null then
    update tenant_numeros_whatsapp set waba_id = p_waba where phone_number_id = p_phone;
  elsif p_waba is distinct from existente.waba_id then
    raise exception 'Este número já está registrado nesta empresa com outra conta' using errcode = '23505';
  else
    raise exception 'Este número já está registrado nesta empresa' using errcode = '23505';
  end if;
  insert into eventos_historico (tenant_id, conversa_id, tipo, dados)
  values (p_tenant, null, 'numero_whatsapp_registrado', jsonb_build_object('phone_number_id', p_phone, 'waba_id', p_waba, 'operador', p_operador));
end $$;

create function plataforma_autorizar_operacao(p_tenant uuid, p_phone text, p_operador text, p_motivo text, p_autorizar boolean) returns void
language plpgsql set search_path = public, pg_temp as $$
begin
  perform exigir_operacao_plataforma(p_tenant, p_operador, p_motivo, true);
  if p_autorizar then
    update tenant_numeros_whatsapp set operacao_autorizada_em = now(), operacao_autorizada_por = p_operador, operacao_motivo = p_motivo
     where phone_number_id = p_phone and tenant_id = p_tenant and waba_id is not null and operacao_autorizada_em is null;
  else
    -- Revogar a autorização desliga também o envio (a restrição da tabela exige).
    update tenant_numeros_whatsapp set operacao_autorizada_em = null, operacao_autorizada_por = null, operacao_motivo = p_motivo,
                                       envio_habilitado = false,
                                       envio_alterado_em = case when envio_habilitado then now() else envio_alterado_em end,
                                       envio_alterado_por = case when envio_habilitado then p_operador else envio_alterado_por end,
                                       envio_motivo = case when envio_habilitado then 'autorização revogada: ' || p_motivo else envio_motivo end
     where phone_number_id = p_phone and tenant_id = p_tenant and operacao_autorizada_em is not null;
  end if;
  if not found then
    raise exception 'Número não encontrado nesta empresa, sem conta (WABA) ou já nesta situação' using errcode = 'P0001';
  end if;
  insert into eventos_historico (tenant_id, conversa_id, tipo, dados)
  values (p_tenant, null, case when p_autorizar then 'operacao_whatsapp_autorizada' else 'operacao_whatsapp_revogada' end,
          jsonb_build_object('phone_number_id', p_phone, 'operador', p_operador, 'motivo', p_motivo));
end $$;

create function plataforma_alterar_envio(p_tenant uuid, p_phone text, p_operador text, p_motivo text, p_habilitar boolean) returns void
language plpgsql set search_path = public, pg_temp as $$
begin
  perform exigir_operacao_plataforma(p_tenant, p_operador, p_motivo, true);
  update tenant_numeros_whatsapp set envio_habilitado = p_habilitar, envio_alterado_em = now(), envio_alterado_por = p_operador, envio_motivo = p_motivo
   where phone_number_id = p_phone and tenant_id = p_tenant and envio_habilitado <> p_habilitar;
  if not found then
    raise exception 'Número não encontrado nesta empresa ou já nesta situação' using errcode = 'P0001';
  end if;
  insert into eventos_historico (tenant_id, conversa_id, tipo, dados)
  values (p_tenant, null, case when p_habilitar then 'envio_whatsapp_habilitado' else 'envio_whatsapp_desabilitado' end,
          jsonb_build_object('phone_number_id', p_phone, 'operador', p_operador, 'motivo', p_motivo));
end $$;

-- ---------------------------------------------------------------- permissões
revoke all on contas_whatsapp, credenciais_whatsapp from public;
revoke all on function proteger_credencial(), proteger_numero_whatsapp(), proteger_numero_da_conversa(),
                       credencial_envio(text), exigir_operacao_plataforma(uuid, text, text, boolean),
                       plataforma_conectar_conta(uuid, text, text, text), plataforma_registrar_credencial(uuid, text, bytea, text, text),
                       plataforma_desconectar_conta(uuid, text, text, text), plataforma_registrar_numero(uuid, text, text, text),
                       plataforma_autorizar_operacao(uuid, text, text, text, boolean), plataforma_alterar_envio(uuid, text, text, text, boolean)
  from public;
grant execute on function credencial_envio(text) to seceletesc_worker;
-- A supervisão vê a situação das contas (sem credencial) para entender por que um número não envia.
grant select on contas_whatsapp to seceletesc_supervisao;
-- O envio da conversa precisa saber o número de origem; quem o grava é a aplicação, ao registrar a mensagem.
grant select (numero_empresa) on conversas to seceletesc_worker;
