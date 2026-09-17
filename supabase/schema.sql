-- ============================================================================
--  Sentrychan Supabase schema
--
--  Idempotent — safe to run against an empty project OR an existing one.
--  Every object is created with IF NOT EXISTS / OR REPLACE, and every column
--  addition is guarded, so re-running never destroys data.
--
--  Run this in the Supabase SQL Editor (or via `psql`) as one script.
-- ============================================================================

-- Required extension for gen_random_uuid()
create extension if not exists "pgcrypto";

-- ---------------------------------------------------------------------------
-- 1. user_profiles  (NEW — required to fix "add friend by email")
--
--    Mirrors non-sensitive fields from auth.users so we can look up users by
--    email without exposing auth.users through PostgREST (which Supabase
--    doesn't allow anyway). A trigger keeps it in sync on signup / update.
-- ---------------------------------------------------------------------------

create table if not exists public.user_profiles (
    id           uuid primary key references auth.users(id) on delete cascade,
    email        text not null,
    display_name text,
    avatar_url   text,
    created_at   timestamptz not null default now(),
    updated_at   timestamptz not null default now()
);

create unique index if not exists user_profiles_email_lower_idx
    on public.user_profiles (lower(email));

alter table public.user_profiles enable row level security;

-- Any signed-in user can look up any profile by email/id (needed for
-- "add friend by email" and friend-name enrichment). Only PII we expose here
-- is what the user chose to sign in with — email + Google display name/avatar.
drop policy if exists "profiles are readable by authenticated" on public.user_profiles;
create policy "profiles are readable by authenticated"
    on public.user_profiles for select
    to authenticated
    using (true);

drop policy if exists "users update own profile" on public.user_profiles;
create policy "users update own profile"
    on public.user_profiles for update
    using (auth.uid() = id);

-- Trigger: create/refresh a profile whenever a user signs up or their
-- Google metadata changes (name change, new avatar).
create or replace function public.handle_new_user()
returns trigger
language plpgsql
security definer
set search_path = public
as $$
begin
    insert into public.user_profiles (id, email, display_name, avatar_url, updated_at)
    values (
        new.id,
        new.email,
        coalesce(new.raw_user_meta_data ->> 'full_name',
                 new.raw_user_meta_data ->> 'name'),
        new.raw_user_meta_data ->> 'avatar_url',
        now()
    )
    on conflict (id) do update
        set email        = excluded.email,
            display_name = coalesce(excluded.display_name, public.user_profiles.display_name),
            avatar_url   = coalesce(excluded.avatar_url,   public.user_profiles.avatar_url),
            updated_at   = now();
    return new;
end;
$$;

drop trigger if exists on_auth_user_created on auth.users;
create trigger on_auth_user_created
    after insert or update of email, raw_user_meta_data on auth.users
    for each row execute function public.handle_new_user();

-- Backfill: if you already had users before this table existed, seed them now.
insert into public.user_profiles (id, email, display_name, avatar_url)
select u.id,
       u.email,
       coalesce(u.raw_user_meta_data ->> 'full_name',
                u.raw_user_meta_data ->> 'name'),
       u.raw_user_meta_data ->> 'avatar_url'
from auth.users u
on conflict (id) do nothing;

-- ---------------------------------------------------------------------------
-- 2. user_library  (anime library cloud sync)
-- ---------------------------------------------------------------------------

create table if not exists public.user_library (
    id               uuid primary key default gen_random_uuid(),
    user_id          uuid references auth.users(id) on delete cascade not null,
    mal_id           integer not null,
    title            text not null,
    last_episode     integer default 0,
    total_episodes   integer default 0,
    airing_status    text,
    monitoring_state text,
    added_at         timestamptz default now(),
    updated_at       timestamptz not null default now(),
    unique (user_id, mal_id)
);

-- Idempotent column additions (for older projects created before a column existed).
alter table public.user_library add column if not exists added_at   timestamptz default now();
alter table public.user_library add column if not exists updated_at timestamptz not null default now();

create index if not exists user_library_user_id_idx  on public.user_library (user_id);
create index if not exists user_library_updated_idx  on public.user_library (user_id, updated_at desc);

alter table public.user_library enable row level security;
drop policy if exists "users manage own library" on public.user_library;
create policy "users manage own library"
    on public.user_library for all
    using (auth.uid() = user_id)
    with check (auth.uid() = user_id);

-- ---------------------------------------------------------------------------
-- 3. friendships
-- ---------------------------------------------------------------------------

create table if not exists public.friendships (
    id           uuid primary key default gen_random_uuid(),
    requester_id uuid references auth.users(id) on delete cascade not null,
    addressee_id uuid references auth.users(id) on delete cascade not null,
    status       text not null default 'pending', -- pending | accepted | blocked
    created_at   timestamptz not null default now(),
    unique (requester_id, addressee_id),
    check (requester_id <> addressee_id)
);

create index if not exists friendships_requester_idx on public.friendships (requester_id);
create index if not exists friendships_addressee_idx on public.friendships (addressee_id);
create index if not exists friendships_status_idx    on public.friendships (status);

alter table public.friendships enable row level security;

drop policy if exists "users manage own friendships" on public.friendships;
create policy "users manage own friendships"
    on public.friendships for all
    using (auth.uid() = requester_id or auth.uid() = addressee_id)
    with check (auth.uid() = requester_id or auth.uid() = addressee_id);

-- ---------------------------------------------------------------------------
-- 4. activity_feed
-- ---------------------------------------------------------------------------

create table if not exists public.activity_feed (
    id           uuid primary key default gen_random_uuid(),
    user_id      uuid references auth.users(id) on delete cascade not null,
    event_type   text not null,   -- watched_episode | added_series | completed_series
    series_title text not null,
    mal_id       integer not null,
    episode      integer,
    created_at   timestamptz not null default now()
);

create index if not exists activity_user_created_idx on public.activity_feed (user_id, created_at desc);
create index if not exists activity_created_idx      on public.activity_feed (created_at desc);

alter table public.activity_feed enable row level security;

drop policy if exists "friends can read activity" on public.activity_feed;
create policy "friends can read activity"
    on public.activity_feed for select
    using (
        auth.uid() = user_id
        or exists (
            select 1 from public.friendships f
            where f.status = 'accepted'
              and ((f.requester_id = auth.uid() and f.addressee_id = activity_feed.user_id)
                or (f.addressee_id = auth.uid() and f.requester_id = activity_feed.user_id))
        )
    );

drop policy if exists "users insert own activity" on public.activity_feed;
create policy "users insert own activity"
    on public.activity_feed for insert
    with check (auth.uid() = user_id);

-- ---------------------------------------------------------------------------
-- 5. watch_rooms  (Hyperbeam co-watch sessions)
-- ---------------------------------------------------------------------------

create table if not exists public.watch_rooms (
    id            uuid primary key default gen_random_uuid(),
    host_id       uuid references auth.users(id) on delete cascade not null,
    host_name     text not null,
    series_title  text not null,
    mal_id        integer not null default 0,
    episode       integer,
    hb_session_id text not null,
    embed_url     text not null,
    is_active     boolean not null default true,
    created_at    timestamptz not null default now()
);

create index if not exists watch_rooms_host_idx   on public.watch_rooms (host_id);
create index if not exists watch_rooms_active_idx on public.watch_rooms (is_active) where is_active;

alter table public.watch_rooms enable row level security;

drop policy if exists "friends see active rooms" on public.watch_rooms;
create policy "friends see active rooms"
    on public.watch_rooms for select
    using (
        is_active = true and (
            auth.uid() = host_id
            or exists (
                select 1 from public.friendships f
                where f.status = 'accepted'
                  and ((f.requester_id = auth.uid() and f.addressee_id = watch_rooms.host_id)
                    or (f.addressee_id = auth.uid() and f.requester_id = watch_rooms.host_id))
            )
        )
    );

drop policy if exists "host manages own rooms" on public.watch_rooms;
create policy "host manages own rooms"
    on public.watch_rooms for all
    using (auth.uid() = host_id)
    with check (auth.uid() = host_id);

-- ---------------------------------------------------------------------------
-- 6. updated_at auto-bump trigger (shared)
-- ---------------------------------------------------------------------------

create or replace function public.set_updated_at()
returns trigger language plpgsql as $$
begin
    new.updated_at = now();
    return new;
end;
$$;

drop trigger if exists trg_user_library_updated on public.user_library;
create trigger trg_user_library_updated
    before update on public.user_library
    for each row execute function public.set_updated_at();

drop trigger if exists trg_user_profiles_updated on public.user_profiles;
create trigger trg_user_profiles_updated
    before update on public.user_profiles
    for each row execute function public.set_updated_at();

-- ---------------------------------------------------------------------------
-- 7. Realtime (optional but recommended)
--
--    Adds these tables to the supabase_realtime publication so the client can
--    subscribe to live changes (new friend requests, live activity feed,
--    friends going live in a watch room). Safe to re-run.
-- ---------------------------------------------------------------------------

do $$
begin
    if not exists (
        select 1 from pg_publication where pubname = 'supabase_realtime'
    ) then
        create publication supabase_realtime;
    end if;
end $$;

do $$
declare
    t text;
begin
    foreach t in array array['friendships', 'activity_feed', 'watch_rooms'] loop
        begin
            execute format('alter publication supabase_realtime add table public.%I', t);
        exception when duplicate_object then
            -- already in the publication
            null;
        end;
    end loop;
end $$;

-- ---------------------------------------------------------------------------
-- 8. user_manga_library  (manga/novel cross-device sync)
-- ---------------------------------------------------------------------------

create table if not exists public.user_manga_library (
    id                uuid primary key default gen_random_uuid(),
    user_id           uuid references auth.users(id) on delete cascade not null,
    source            text not null,                 -- MangaDex, WeebCentral, …
    source_id         text not null,                 -- source's own identifier
    title             text not null,
    cover_url         text,
    total_chapters    numeric,                       -- decimal — chapter 12.5 is real
    last_read_chapter numeric not null default 0,
    is_novel          boolean not null default false,
    is_censored       boolean not null default false,
    added_at          timestamptz not null default now(),
    updated_at        timestamptz not null default now(),
    unique (user_id, source, source_id)
);

create index if not exists user_manga_user_idx    on public.user_manga_library (user_id);
create index if not exists user_manga_updated_idx on public.user_manga_library (user_id, updated_at desc);

alter table public.user_manga_library enable row level security;
drop policy if exists "users manage own manga" on public.user_manga_library;
create policy "users manage own manga"
    on public.user_manga_library for all
    using (auth.uid() = user_id)
    with check (auth.uid() = user_id);

drop trigger if exists trg_user_manga_updated on public.user_manga_library;
create trigger trg_user_manga_updated
    before update on public.user_manga_library
    for each row execute function public.set_updated_at();

-- ---------------------------------------------------------------------------
-- 9. series_airings  (push-style "new episode dropped" alerts)
--
--    Any client that writes to this table (typically the RSS/monitor pipeline)
--    fans out a Realtime insert to every client. The desktop app filters
--    inserts to shows in the user's library and shows a toast.
-- ---------------------------------------------------------------------------

create table if not exists public.series_airings (
    id           uuid primary key default gen_random_uuid(),
    mal_id       integer not null,
    series_title text not null,
    episode      integer,
    resolution   text,
    source       text,                             -- SubsPlease, nyaa, …
    magnet       text,
    created_at   timestamptz not null default now()
);

create index if not exists series_airings_mal_idx     on public.series_airings (mal_id);
create index if not exists series_airings_created_idx on public.series_airings (created_at desc);

alter table public.series_airings enable row level security;

-- Readable to any authenticated user (each client filters by their library).
drop policy if exists "airings readable by authenticated" on public.series_airings;
create policy "airings readable by authenticated"
    on public.series_airings for select
    to authenticated
    using (true);

-- Any signed-in user can post an airing (the trusted writer is the app itself).
-- If you later want to lock this to a specific service account, replace this
-- policy with a role check.
drop policy if exists "authenticated users post airings" on public.series_airings;
create policy "authenticated users post airings"
    on public.series_airings for insert
    to authenticated
    with check (true);

-- ---------------------------------------------------------------------------
-- 10. Realtime — publish the new tables too
-- ---------------------------------------------------------------------------

do $$
declare
    t text;
begin
    foreach t in array array['user_manga_library', 'series_airings'] loop
        begin
            execute format('alter publication supabase_realtime add table public.%I', t);
        exception when duplicate_object then
            null;
        end;
    end loop;
end $$;

-- ---------------------------------------------------------------------------
-- 11. Storage bucket for user avatars
--
--    Public-read (avatars need to render in friends' UIs), owner-only-write.
--    Paths are namespaced as `<user-id>/<filename>` so the RLS policy can
--    enforce that a user can only touch files under their own id prefix.
-- ---------------------------------------------------------------------------

insert into storage.buckets (id, name, public)
values ('avatars', 'avatars', true)
on conflict (id) do update set public = true;

drop policy if exists "avatars readable by anyone"        on storage.objects;
drop policy if exists "avatars writable by owner"         on storage.objects;
drop policy if exists "avatars updatable by owner"        on storage.objects;
drop policy if exists "avatars deletable by owner"        on storage.objects;

create policy "avatars readable by anyone"
    on storage.objects for select
    using (bucket_id = 'avatars');

create policy "avatars writable by owner"
    on storage.objects for insert
    to authenticated
    with check (
        bucket_id = 'avatars'
        and (storage.foldername(name))[1] = auth.uid()::text
    );

create policy "avatars updatable by owner"
    on storage.objects for update
    to authenticated
    using (
        bucket_id = 'avatars'
        and (storage.foldername(name))[1] = auth.uid()::text
    );

create policy "avatars deletable by owner"
    on storage.objects for delete
    to authenticated
    using (
        bucket_id = 'avatars'
        and (storage.foldername(name))[1] = auth.uid()::text
    );

-- ============================================================================
--  Deploy the account-deletion Edge Function separately:
--
--      supabase functions deploy delete-account --no-verify-jwt=false
--
--  Source is in supabase/functions/delete-account/index.ts
-- ============================================================================

-- ============================================================================
-- Done.
-- ============================================================================
