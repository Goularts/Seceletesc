-- F4 WhatsApp: fila durável da IA, eco do app, status de entrega. Executado por seceletesc_dono.
-- Ver plano técnico, seção 4.8.

-- ---------------------------------------------------------------- fila da IA
-- O webhook só registra a mensagem (rápido, idempotente pelo wamid) e põe na fila; o trabalhador da IA trata
-- depois. Se o processo cair, a mensagem continua na fila (lease). A decisão sai uma única vez: quem tira a linha
-- da fila na transação da decisão é quem decide.
-- RLS habilitado SEM force (revisão da F4, S2): a aplicação só vê e apaga linhas da empresa da transação; o dono
-- (reservar_ia, que atende todas as empresas e devolve só ids) não é limitado.
create table fila_ia (
  mensagem_id      uuid primary key,
  tenant_id        uuid not null references tenants(id),
  conversa_id      uuid not null,
  deteccao_falhou  boolean not null default false,
  tentativas       int not null default 0,
  disponivel_em    timestamptz not null default now(),
  criada_em        timestamptz not null default now(),
  foreign key (tenant_id, mensagem_id) references mensagens (tenant_id, id),
  foreign key (tenant_id, conversa_id) references conversas (tenant_id, id)
);
create index ix_fila_ia_disponivel on fila_ia (disponivel_em);
alter table fila_ia enable row level security;
create policy fila_ia_isolamento on fila_ia using (tenant_id = tenant_atual()) with check (tenant_id = tenant_atual());

-- A aplicação só enfileira mensagens da empresa da transação.
create function exigir_tenant_da_transacao() returns trigger
language plpgsql set search_path = public, pg_temp as $$
begin
  if new.tenant_id is distinct from tenant_atual() then
    raise exception 'Linha de outra empresa' using errcode = 'P0001';
  end if;
  return new;
end $$;
create trigger tg_fila_ia_tenant before insert on fila_ia
  for each row execute function exigir_tenant_da_transacao();

create function reservar_ia(p_lease_segundos int) returns table (mensagem_id uuid, tenant_id uuid)
language sql security definer set search_path = public, pg_temp as $$
  update fila_ia f
     set disponivel_em = now() + make_interval(secs => p_lease_segundos), tentativas = f.tentativas + 1
   where f.mensagem_id = (
     select mensagem_id from fila_ia
      where disponivel_em <= now()
      order by disponivel_em
      for update skip locked
      limit 1)
     and p_lease_segundos between 10 and 600
  returning f.mensagem_id, f.tenant_id
$$;

-- ---------------------------------------------------------------- eco do WhatsApp Business App (coexistência)
alter table mensagens drop constraint mensagens_direcao_check;
alter table mensagens add constraint mensagens_direcao_check check (direcao in ('entrada', 'saida', 'saida_app'));

create or replace function proteger_conversa() returns trigger
language plpgsql set search_path = public, pg_temp as $$
declare
  papel text;
  operador uuid;
begin
  if new.tenant_id <> old.tenant_id or new.cliente_id <> old.cliente_id or new.id <> old.id then
    raise exception 'tenant, cliente e id da conversa são imutáveis' using errcode = 'P0001';
  end if;
  if new.versao_estado < old.versao_estado
     or (new.estado <> old.estado and new.versao_estado <= old.versao_estado) then
    raise exception 'versao_estado deve crescer a cada transição' using errcode = 'P0001';
  end if;
  if new.estado <> old.estado
     and not pg_has_role(current_user, 'seceletesc_supervisao', 'member')
     and not (old.estado = 'Automatico' and new.estado = 'TransferenciaPendente')
     -- F4: eco do WhatsApp Business App (humano respondeu pelo celular) coloca a conversa sob controle humano
     -- (invariante 24). Só pela função registrar_eco_humano (roda como o dono e marca a transação); a aplicação não
     -- usa o dono. O CLI de administração conecta como dono, mas não altera conversas.
     and not (current_user = 'seceletesc_dono' and new.estado = 'Humano'
              and coalesce(current_setting('seceletesc.eco_wamid', true), '') <> '') then
    raise exception 'Transição % -> % só pela supervisão humana', old.estado, new.estado using errcode = 'P0001';
  end if;
  -- Atendente (revisão pós-F3, S4): só tira da IA uma conversa da fila (pendência de retomada) e não mexe no estado
  -- de conversa atribuída a outra pessoa.
  if new.estado <> old.estado and pg_has_role(current_user, 'seceletesc_supervisao', 'member') then
    operador := usuario_autenticado();
    if papel_supervisor(new.tenant_id, operador) = 'Atendente'
       and ((old.estado = 'Automatico' and not old.marcada_retomada_humana)
            or (old.atribuida_a is not null and old.atribuida_a <> operador)) then
      raise exception 'Atendente só assume atendimentos da fila ou atribuídos a ele' using errcode = 'P0001';
    end if;
  end if;
  if new.liberada_em is distinct from old.liberada_em and not pg_has_role(current_user, 'seceletesc_supervisao', 'member') then
    raise exception 'Liberação só pela supervisão humana' using errcode = 'P0001';
  end if;
  if old.estado = 'Humano' and new.estado = 'Automatico'
     and (new.liberada_em is null or new.liberada_em is not distinct from old.liberada_em) then
    raise exception 'Voltar ao modo automático exige registrar a liberação' using errcode = 'P0001';
  end if;
  -- Quem libera fica registrado e precisa ser supervisor ou administrador (atendente não libera).
  if new.estado = 'Humano' and old.estado <> 'Humano' then
    new.liberada_por := null;  -- cada liberação registra o próprio responsável
  elsif old.estado = 'Humano' and new.estado = 'Automatico' then
    papel := papel_supervisor(new.tenant_id, new.liberada_por);
    if papel is null or papel not in ('Supervisor', 'Administrador') then
      raise exception 'Liberar a conversa para a IA exige supervisor ou administrador' using errcode = 'P0001';
    end if;
  elsif new.liberada_por is distinct from old.liberada_por then
    raise exception 'Responsável pela liberação só muda ao liberar' using errcode = 'P0001';
  end if;
  if not old.marcada_retomada_humana and new.marcada_retomada_humana then
    -- Pendência reaberta: o encerramento anterior não vale para ela.
    new.retomada_encerrada_por := null;
    new.retomada_encerrada_em := null;
    new.retomada_motivo := null;
  elsif old.marcada_retomada_humana and not new.marcada_retomada_humana then
    if not pg_has_role(current_user, 'seceletesc_supervisao', 'member') then
      raise exception 'A pendência de retomada só é encerrada pela supervisão humana' using errcode = 'P0001';
    end if;
    papel := papel_supervisor(new.tenant_id, new.retomada_encerrada_por);
    if papel is null or papel not in ('Supervisor', 'Administrador')
       or coalesce(btrim(new.retomada_motivo), '') = '' then
      raise exception 'A pendência de retomada só é encerrada por supervisor ou administrador, com motivo' using errcode = 'P0001';
    end if;
    new.retomada_encerrada_em := now();
  elsif new.retomada_encerrada_por is distinct from old.retomada_encerrada_por
     or new.retomada_encerrada_em is distinct from old.retomada_encerrada_em
     or new.retomada_motivo is distinct from old.retomada_motivo then
    raise exception 'O registro de encerramento da pendência só muda ao encerrá-la' using errcode = 'P0001';
  end if;
  -- Atribuição. De volta à IA, a conversa sai da responsabilidade de quem a atendia. Conversa com a IA não recebe
  -- responsável (revisão pós-F3, G3/S7: recusar em vez de zerar em silêncio).
  if new.estado = 'Automatico' and old.estado <> 'Automatico' then
    new.atribuida_a := null;
  elsif new.estado = 'Automatico' and new.atribuida_a is not null then
    raise exception 'Conversa com a IA não tem responsável humano' using errcode = 'P0001';
  elsif new.atribuida_a is distinct from old.atribuida_a then
    if not pg_has_role(current_user, 'seceletesc_supervisao', 'member') then
      raise exception 'Atribuição só pela supervisão humana' using errcode = 'P0001';
    end if;
    operador := usuario_autenticado();
    papel := papel_supervisor(new.tenant_id, operador);
    if papel is null then
      raise exception 'Atribuição exige usuário ativo desta empresa' using errcode = 'P0001';
    end if;
    if papel = 'Atendente' then
      -- O atendente só pega da fila, para si mesmo.
      if old.atribuida_a is not null or new.atribuida_a is distinct from operador then
        raise exception 'Atendente só pega para si um atendimento da fila' using errcode = 'P0001';
      end if;
    elsif new.atribuida_a is not null and not usuario_ativo_da_empresa(new.tenant_id, new.atribuida_a) then
      raise exception 'Responsável precisa ser usuário ativo desta empresa' using errcode = 'P0001';
    end if;
  end if;
  return new;
end $$;

-- Humano respondeu pelo celular: a conversa vai para controle humano (qualquer estado -> Humano). Mínima: só essa
-- transição, dentro do tenant da transação (RLS), com evento. Não mexe na janela de 24h (eco não a abre).
-- Exige o eco registrado na mesma conversa (revisão da F4, S8: sem eco real, nada de evento de auditoria).
-- A versão sobe mesmo se a conversa já estava em Humano (G2): uma liberação com a versão antiga, que não viu a
-- resposta dada pelo celular, é recusada.
create function registrar_eco_humano(p_conversa uuid, p_wamid text) returns bigint
language plpgsql security definer set search_path = public, pg_temp as $$
declare
  nova_versao bigint;
begin
  if not exists (select 1 from mensagens m where m.conversa_id = p_conversa and m.direcao = 'saida_app' and m.wamid = p_wamid) then
    raise exception 'Eco não registrado nesta conversa' using errcode = 'P0001';
  end if;
  perform set_config('seceletesc.eco_wamid', p_wamid, true);
  update conversas set estado = 'Humano', versao_estado = versao_estado + 1
   where id = p_conversa
  returning versao_estado into nova_versao;
  perform set_config('seceletesc.eco_wamid', '', true);
  insert into eventos_historico (tenant_id, conversa_id, tipo, dados)
  select tenant_id, id, 'eco_app_humano', jsonb_build_object('wamid', p_wamid, 'versao', nova_versao)
    from conversas where id = p_conversa;
  return nova_versao;
end $$;

-- ---------------------------------------------------------------- status de entrega
create table status_entrega (
  tenant_id    uuid not null references tenants(id),
  wamid        text not null,
  status       text not null check (status in ('sent', 'delivered', 'read', 'failed', 'deleted')),
  ocorrido_em  timestamptz not null,
  erro_codigo  text,
  registrado_em timestamptz not null default now(),
  primary key (tenant_id, wamid, status)
);
alter table status_entrega enable row level security;
alter table status_entrega force row level security;
create policy status_entrega_isolamento on status_entrega using (tenant_id = tenant_atual()) with check (tenant_id = tenant_atual());
create trigger tg_status_entrega_imutavel before update or delete on status_entrega
  for each row execute function bloquear_alteracao_imutavel();

-- Número de origem de uma empresa, pelo mesmo cadastro que resolve a entrada (revisão da F4, S3): o envio nunca
-- sai por um número de outra empresa por erro de configuração.
create function numero_do_tenant(p_tenant uuid, p_phone_number_id text) returns boolean
language sql stable security definer set search_path = public, pg_temp as $$
  select exists (select 1 from tenant_numeros_whatsapp n where n.tenant_id = p_tenant and n.phone_number_id = p_phone_number_id)
$$;

-- ---------------------------------------------------------------- permissões
revoke all on fila_ia, status_entrega from public;
revoke all on function reservar_ia(int), registrar_eco_humano(uuid, text), exigir_tenant_da_transacao(), numero_do_tenant(uuid, text) from public;
grant execute on function numero_do_tenant(uuid, text) to seceletesc_worker, seceletesc_app;
grant select, insert, delete on fila_ia to seceletesc_app;
grant select, insert on status_entrega to seceletesc_app;
grant execute on function reservar_ia(int), registrar_eco_humano(uuid, text), transferir_para_supervisao(uuid, text) to seceletesc_app;
grant select on status_entrega to seceletesc_supervisao;
