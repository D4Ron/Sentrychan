// Supabase Edge Function: delete-account
//
// Lets a signed-in user permanently delete their own auth.users row.
// The client cannot do this directly — auth.admin.deleteUser() needs the
// service_role key, which must never ship in a desktop app. This function
// runs it server-side, but only for the caller's OWN id.
//
// Deploy:
//   supabase functions deploy delete-account
//
// Invoke from the C# client:
//   await supabase.Functions.Invoke("delete-account");
//
// All owned rows (user_library, user_manga_library, friendships,
// activity_feed, watch_rooms, user_profiles) cascade-delete via their FK
// on auth.users(id) on delete cascade.

import { createClient } from "https://esm.sh/@supabase/supabase-js@2";

const cors = {
  "Access-Control-Allow-Origin":  "*",
  "Access-Control-Allow-Headers": "authorization, x-client-info, apikey, content-type",
  "Access-Control-Allow-Methods": "POST, OPTIONS",
};

Deno.serve(async (req) => {
  if (req.method === "OPTIONS") return new Response("ok", { headers: cors });

  try {
    // 1. Identify the caller from their JWT.
    const authHeader = req.headers.get("Authorization") ?? "";
    if (!authHeader.startsWith("Bearer ")) {
      return json({ error: "Missing bearer token" }, 401);
    }

    const userClient = createClient(
      Deno.env.get("SUPABASE_URL")!,
      Deno.env.get("SUPABASE_ANON_KEY")!,
      { global: { headers: { Authorization: authHeader } } },
    );

    const { data: userRes, error: userErr } = await userClient.auth.getUser();
    if (userErr || !userRes?.user) {
      return json({ error: "Invalid session" }, 401);
    }
    const userId = userRes.user.id;

    // 2. Admin-delete using service role. Cascade FKs remove all owned rows.
    const admin = createClient(
      Deno.env.get("SUPABASE_URL")!,
      Deno.env.get("SUPABASE_SERVICE_ROLE_KEY")!,
    );

    // Best-effort: purge avatar folder in storage first (storage doesn't cascade).
    try {
      const { data: files } = await admin.storage.from("avatars").list(userId);
      if (files && files.length > 0) {
        await admin.storage.from("avatars").remove(
          files.map((f) => `${userId}/${f.name}`),
        );
      }
    } catch { /* non-fatal — the account still deletes */ }

    const { error: delErr } = await admin.auth.admin.deleteUser(userId);
    if (delErr) return json({ error: delErr.message }, 500);

    return json({ deleted: userId }, 200);
  } catch (e) {
    return json({ error: (e as Error).message }, 500);
  }
});

function json(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), {
    status,
    headers: { ...cors, "Content-Type": "application/json" },
  });
}
