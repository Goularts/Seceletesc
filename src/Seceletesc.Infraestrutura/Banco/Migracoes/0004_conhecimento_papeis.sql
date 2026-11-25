-- F3 Governança do conhecimento: papéis humanos, aprovação com autoria, limites comerciais, pendência de retomada
-- e expurgo do texto enviado. Executado por seceletesc_dono. Ver plano técnico, seção 4.11.
--
-- Defesa em profundidade: a API confere o papel em toda operação; o banco confere de novo nas operações de risco
-- (aprovar, revogar, publicar política, encerrar pendência), a partir do usuário gravado na própria linha.
-- Limite: o banco confia no id de usuário que a API grava (a API autentica). Ver plano técnico, seção 6.

-- ---------------------------------------------------------------- papéis
alter table usuarios_supervisao add column papel text not null default 'Atendente'
  check (papel in ('Atendente', 'Supervisor', 'Administrador'));

drop function autenticar_supervisor(text);
create function autenticar_supervisor(p_chave_sha256 text) returns table (usuario_id uuid, tenant_id uuid, papel text)
language sql stable security definer set search_path = public, pg_temp as $$
  select u.id, u.tenant_id, u.papel from usuarios_supervisao u where u.chave_sha256 = p_chave_sha256 and u.ativo
$$;

-- Papel de um usuário ativo desta empresa (nulo se não existe, é de outra empresa ou está desativado).
-- Só responde pela empresa da transação (revisão da F3, S12): não serve para sondar usuários de outra empresa.
create function papel_supervisor(p_tenant uuid, p_usuario uuid) returns text
language sql stable security definer set search_path = public, pg_temp as $$
  select u.papel from usuarios_supervisao u
   where u.id = p_usuario and u.tenant_id = p_tenant and p_tenant = tenant_atual() and u.ativo
$$;

create or replace function supervisor_ativo(p_tenant uuid, p_usuario uuid) returns boolean
language sql stable security definer set search_path = public, pg_temp as $$
  select exists (select 1 from usuarios_supervisao u
                  where u.id = p_usuario and u.tenant_id = p_tenant and p_tenant = tenant_atual() and u.ativo)
$$;

-- ---------------------------------------------------------------- conhecimento: autoria da aprovação
-- As validações abaixo varrem tabelas com RLS forçado: o FORCE sai e volta na mesma transação (ver 0003).
alter table versoes_conhecimento no force row level security;
alter table itens_conhecimento no force row level security;
alter table politicas_tenant no force row level security;

alter table versoes_conhecimento
  add column aprovada_por uuid,
  add column aprovada_em timestamptz,
  add constraint ck_versao_aprovacao check ((aprovada_por is null) = (aprovada_em is null)),
  add constraint ck_versao_classificacoes check (classificacoes <@ array['Publico', 'Interno', 'Temporario', 'Cliente', 'Negociacao', 'Excecao', 'Restrito']),
  add constraint fk_versao_aprovador foreign key (tenant_id, aprovada_por) references usuarios_supervisao (tenant_id, id);

alter table itens_conhecimento
  add column alterado_por uuid,
  add constraint fk_item_alterado_por foreign key (tenant_id, alterado_por) references usuarios_supervisao (tenant_id, id);

alter table politicas_tenant
  add column publicada_por uuid,
  add constraint fk_politica_publicada_por foreign key (tenant_id, publicada_por) references usuarios_supervisao (tenant_id, id);

alter table versoes_conhecimento force row level security;
alter table itens_conhecimento force row level security;
alter table politicas_tenant force row level security;

-- Uma versão aprovada só nasce da supervisão, com aprovador ativo desta empresa e com papel suficiente:
-- supervisor aprova conhecimento operacional comum; NEG, EXC, RES e CLI exigem administrador. NEG e EXC exigem
-- ainda escopo de cliente ou atendimento e validade (condição comercial excepcional respeita cliente e validade).
create function proteger_versao_conhecimento() returns trigger
language plpgsql set search_path = public, pg_temp as $$
declare
  papel text;
  origem_tipo text;
  origem_ref uuid;
begin
  if new.aprovada_por is null then
    new.aprovada_em := null;
    return new;  -- proposta (sugestão ou edição): nunca é usada pelo agente
  end if;
  if not pg_has_role(current_user, 'seceletesc_supervisao', 'member') then
    raise exception 'Somente a supervisão humana aprova conhecimento' using errcode = 'P0001';
  end if;
  papel := papel_supervisor(new.tenant_id, new.aprovada_por);
  if papel is null or papel not in ('Supervisor', 'Administrador') then
    raise exception 'Aprovação exige supervisor ou administrador ativo desta empresa' using errcode = 'P0001';
  end if;
  if cardinality(new.classificacoes) = 0 then
    raise exception 'Conhecimento aprovado precisa de classificação' using errcode = 'P0001';
  end if;
  if new.escopo_tipo = 'Proposta' then
    raise exception 'Escopo de proposta está fora de uso' using errcode = 'P0001';
  end if;
  if new.valido_ate is null and exists (select 1 from unnest(new.classificacoes) c where c <> 'Publico') then
    raise exception 'Só conhecimento exclusivamente PUB pode ficar sem validade' using errcode = 'P0001';
  end if;
  -- Conhecimento que nasceu de um cliente ou atendimento: só o administrador amplia ou troca o escopo
  -- (informação de um cliente não passa a valer para outros por decisão de um supervisor).
  if papel <> 'Administrador' then
    select v.escopo_tipo, v.escopo_ref into origem_tipo, origem_ref
      from versoes_conhecimento v where v.item_id = new.item_id and v.versao = 1;
    if origem_tipo in ('Cliente', 'Atendimento')
       and (new.escopo_tipo is distinct from origem_tipo or new.escopo_ref is distinct from origem_ref) then
      raise exception 'Ampliar ou trocar o escopo de conhecimento vindo de um cliente ou atendimento exige administrador' using errcode = 'P0001';
    end if;
  end if;
  if new.classificacoes && array['Negociacao', 'Excecao', 'Restrito', 'Cliente'] and papel <> 'Administrador' then
    raise exception 'Conhecimento NEG, EXC, RES ou CLI só é aprovado por administrador' using errcode = 'P0001';
  end if;
  -- Rebaixar conhecimento de maior risco (ex.: negociação individual virando condição geral) também exige administrador.
  if papel <> 'Administrador' and exists (
       select 1 from versoes_conhecimento v join itens_conhecimento i on i.id = v.item_id
        where i.id = new.item_id and v.versao = i.versao_vigente and v.aprovada_por is not null
          and v.classificacoes && array['Negociacao', 'Excecao', 'Restrito', 'Cliente']) then
    raise exception 'Alterar conhecimento NEG, EXC, RES ou CLI aprovado exige administrador' using errcode = 'P0001';
  end if;
  if new.classificacoes && array['Negociacao', 'Excecao']
     and (new.escopo_tipo not in ('Cliente', 'Atendimento') or new.escopo_ref is null or new.valido_ate is null) then
    raise exception 'NEG e EXC exigem escopo de cliente ou atendimento e validade' using errcode = 'P0001';
  end if;
  new.aprovada_em := now();  -- carimbo do banco: quem aprova não escolhe a data
  return new;
end $$;

create trigger tg_versoes_conhecimento_aprovacao before insert on versoes_conhecimento
  for each row execute function proteger_versao_conhecimento();

-- Ciclo de vida (substitui a versão da 0002). Todo conhecimento nasce como sugestão (Rev), inclusive pela supervisão.
-- Rev -> Aprovado | Rejeitado; Aprovado -> Aprovado (nova versão vigente) | Revogado; Rejeitado e Revogado são finais.
-- Aprovado aponta para uma versão aprovada. Rejeitar exige supervisor; revogar exige administrador.
create or replace function proteger_status_conhecimento() returns trigger
language plpgsql set search_path = public, pg_temp as $$
declare
  papel text;
begin
  if tg_op = 'INSERT' then
    if new.status <> 'Rev' then
      raise exception 'Somente a supervisão humana publica conhecimento: sugestões nascem com status Rev' using errcode = 'P0001';
    end if;
    return new;
  end if;
  if not pg_has_role(current_user, 'seceletesc_supervisao', 'member') then
    raise exception 'Somente a supervisão humana altera conhecimento' using errcode = 'P0001';
  end if;
  if new.id <> old.id or new.tenant_id <> old.tenant_id or new.assunto <> old.assunto then
    raise exception 'id, empresa e assunto do conhecimento são imutáveis' using errcode = 'P0001';
  end if;
  if old.status in ('Rejeitado', 'Revogado') then
    raise exception 'Conhecimento % não muda mais', old.status using errcode = 'P0001';
  end if;
  papel := papel_supervisor(new.tenant_id, new.alterado_por);
  if papel is null or papel not in ('Supervisor', 'Administrador') then
    raise exception 'Alteração de conhecimento exige supervisor ou administrador ativo desta empresa' using errcode = 'P0001';
  end if;
  if new.status = 'Aprovado' then
    if not exists (select 1 from versoes_conhecimento v
                    where v.item_id = new.id and v.versao = new.versao_vigente and v.aprovada_por is not null) then
      raise exception 'Conhecimento aprovado precisa apontar para uma versão aprovada' using errcode = 'P0001';
    end if;
    -- Sem voltar para uma aprovação antiga: a vigente é sempre a aprovação mais recente.
    if exists (select 1 from versoes_conhecimento v
                where v.item_id = new.id and v.aprovada_por is not null and v.versao > new.versao_vigente) then
      raise exception 'A versão vigente só avança para a aprovação mais recente' using errcode = 'P0001';
    end if;
  elsif new.status = 'Rejeitado' then
    if old.status <> 'Rev' then
      raise exception 'Só sugestões pendentes são rejeitadas' using errcode = 'P0001';
    end if;
  elsif new.status = 'Revogado' then
    if old.status <> 'Aprovado' then
      raise exception 'Só conhecimento aprovado é revogado' using errcode = 'P0001';
    end if;
    if papel <> 'Administrador' then
      raise exception 'Revogação exige administrador' using errcode = 'P0001';
    end if;
  else
    raise exception 'Transição de conhecimento % -> % não permitida', old.status, new.status using errcode = 'P0001';
  end if;
  return new;
end $$;

-- Rejeição de uma versão proposta (ex.: edição pendente de um conhecimento já aprovado, que continua valendo).
-- Só inserção: a versão rejeitada nunca é aprovada depois (conferido pela API).
create table rejeicoes_versao (
  item_id        uuid not null,
  versao         int not null,
  tenant_id      uuid not null references tenants(id),
  rejeitada_por  uuid not null,
  rejeitada_em   timestamptz not null default now(),
  motivo         text not null check (btrim(motivo) <> ''),
  primary key (item_id, versao),
  foreign key (item_id, versao) references versoes_conhecimento (item_id, versao),
  foreign key (tenant_id, rejeitada_por) references usuarios_supervisao (tenant_id, id)
);
create trigger tg_rejeicoes_imutaveis before update or delete on rejeicoes_versao
  for each row execute function bloquear_alteracao_imutavel();

create function proteger_rejeicao_versao() returns trigger
language plpgsql set search_path = public, pg_temp as $$
declare
  papel text;
begin
  if not pg_has_role(current_user, 'seceletesc_supervisao', 'member') then
    raise exception 'Somente a supervisão humana rejeita versões' using errcode = 'P0001';
  end if;
  papel := papel_supervisor(new.tenant_id, new.rejeitada_por);
  if papel is null or papel not in ('Supervisor', 'Administrador') then
    raise exception 'Rejeição exige supervisor ou administrador ativo desta empresa' using errcode = 'P0001';
  end if;
  if not exists (select 1 from versoes_conhecimento v
                  where v.item_id = new.item_id and v.versao = new.versao and v.tenant_id = new.tenant_id and v.aprovada_por is null) then
    raise exception 'Só uma versão proposta desta empresa é rejeitada' using errcode = 'P0001';
  end if;
  new.rejeitada_em := now();
  return new;
end $$;

create trigger tg_rejeicoes_protecao before insert on rejeicoes_versao
  for each row execute function proteger_rejeicao_versao();

alter table rejeicoes_versao enable row level security;
alter table rejeicoes_versao force row level security;
create policy rejeicoes_isolamento on rejeicoes_versao using (tenant_id = tenant_atual()) with check (tenant_id = tenant_atual());

-- ---------------------------------------------------------------- política: só administrador publica
create function exigir_administrador_na_politica() returns trigger
language plpgsql set search_path = public, pg_temp as $$
begin
  -- Superusuário (administração do banco) fica de fora; a aplicação nunca o usa.
  if pg_has_role(current_user, 'seceletesc_supervisao', 'member')
     and not exists (select 1 from pg_roles where rolname = current_user and rolsuper) then
    if coalesce(papel_supervisor(new.tenant_id, new.publicada_por), '') <> 'Administrador' then
      raise exception 'Somente administrador publica a política' using errcode = 'P0001';
    end if;
  end if;
  return new;
end $$;

create trigger tg_politicas_administrador before insert on politicas_tenant
  for each row execute function exigir_administrador_na_politica();

-- ---------------------------------------------------------------- pendência de retomada pelo celular
-- Fica ativa até um humano autorizado encerrá-la, com responsável, data e motivo. Liberar não a encerra.
alter table conversas no force row level security;
alter table conversas
  add column retomada_encerrada_por uuid,
  add column retomada_encerrada_em timestamptz,
  add column retomada_motivo text,
  add column liberada_por uuid,
  add constraint fk_conversa_liberada_por foreign key (tenant_id, liberada_por) references usuarios_supervisao (tenant_id, id),
  add constraint fk_conversa_retomada_por foreign key (tenant_id, retomada_encerrada_por) references usuarios_supervisao (tenant_id, id);
alter table conversas force row level security;

create or replace function proteger_conversa() returns trigger
language plpgsql set search_path = public, pg_temp as $$
declare
  papel text;
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
     and not (old.estado = 'Automatico' and new.estado = 'TransferenciaPendente') then
    raise exception 'Transição % -> % só pela supervisão humana', old.estado, new.estado using errcode = 'P0001';
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
  return new;
end $$;

-- ---------------------------------------------------------------- expurgo do texto enviado
-- O texto fica na fila em claro só enquanto precisa ser enviado. Ao terminar, o worker troca o texto pela versão
-- mascarada (dado protegido vira marcador) uma única vez.
alter table outbox add column texto_mascarado boolean not null default false;
-- Hash do texto original de toda mensagem na fila (automática ou humana), calculado pelo banco: depois do expurgo,
-- ainda dá para conferir o que foi enviado. Limite: o banco não prova que o texto mascarado veio do original.
alter table outbox add column texto_sha256 text;

create function carimbar_hash_outbox() returns trigger
language plpgsql set search_path = public, pg_temp as $$
begin
  new.texto_sha256 := encode(sha256(convert_to(new.texto, 'UTF8')), 'hex');
  new.texto_mascarado := false;
  return new;
end $$;

create trigger tg_outbox_hash before insert on outbox
  for each row execute function carimbar_hash_outbox();

create or replace function proteger_estado_terminal_outbox() returns trigger
language plpgsql set search_path = public, pg_temp as $$
begin
  if old.status in ('enviada', 'cancelada', 'incerta') then
    raise exception 'Mensagem em estado terminal (%) não pode mudar', old.status using errcode = 'P0001';
  end if;
  if new.decisao_id is distinct from old.decisao_id or new.conversa_id <> old.conversa_id
     or new.autor_id is distinct from old.autor_id or new.texto_sha256 is distinct from old.texto_sha256 then
    raise exception 'Conteúdo da mensagem na fila é imutável' using errcode = 'P0001';
  end if;
  if new.texto <> old.texto
     and not (new.status in ('enviada', 'cancelada', 'incerta') and not old.texto_mascarado and new.texto_mascarado) then
    raise exception 'Texto da mensagem na fila só muda para a versão mascarada, ao terminar' using errcode = 'P0001';
  end if;
  if new.texto_mascarado <> old.texto_mascarado and new.texto = old.texto then
    raise exception 'Marcação de texto mascarado sem trocar o texto' using errcode = 'P0001';
  end if;
  new.atualizada_em := now();
  return new;
end $$;

-- ---------------------------------------------------------------- permissões
revoke all on function papel_supervisor(uuid, uuid) from public;
revoke all on function autenticar_supervisor(text) from public;
revoke all on function proteger_versao_conhecimento(), exigir_administrador_na_politica(), proteger_rejeicao_versao() from public;
revoke all on rejeicoes_versao from public;
grant select, insert on rejeicoes_versao to seceletesc_supervisao;
grant execute on function autenticar_supervisor(text), papel_supervisor(uuid, uuid) to seceletesc_supervisao;
grant update (marcada_retomada_humana, retomada_encerrada_por, retomada_motivo, liberada_por) on conversas to seceletesc_supervisao;
revoke all on function carimbar_hash_outbox() from public;
-- A supervisão só altera status, versão vigente e responsável; assunto, empresa e id ficam fixos.
revoke update on itens_conhecimento from seceletesc_supervisao;
grant update (status, versao_vigente, alterado_por) on itens_conhecimento to seceletesc_supervisao;

-- O worker registra o envio mascarado e cifra o dado protegido do texto enviado.
grant update (texto, texto_mascarado) on outbox to seceletesc_worker;
grant insert on dados_protegidos to seceletesc_worker;
