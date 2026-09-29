# Sentrychan — Preview work plan

You are working on the **preview** line of Sentrychan: a .NET 9 + Avalonia 11 (ReactiveUI, EF Core
SQLite) desktop app that tracks anime, manga and novels, downloads new episodes/chapters and files them
into a library. This file is the brief. Read all of it before changing anything, and keep the
**Progress log** at the bottom current — a later session (or a human) continues from it.

## Ground rules

- **Branch:** work on `preview` (create it from `main` if it doesn't exist). **Never push to `main`.**
  Stable fixes land on `main` separately; merge `main` into `preview` when asked, not on your own.
- **Build stays green:** `dotnet build Sentrychan.sln` — all projects target `net9.0` and build on
  Linux. Windows-only APIs (DPAPI, user32) compile with CA1416 warnings; that's expected. Don't
  disable features to silence warnings.
- **Tests:** there is no test project yet — Phase 0 adds `Sentrychan.Tests` (xUnit). Everything
  that can be tested headlessly must be: parsers, planners, renamers, API clients (against recorded
  responses), migrations. The UI can't be run here; say in the log what needs a visual check on Windows.
- **Small, reviewable commits**, each building. Message style: a plain summary line, then *why*.
- **Comments explain why, not what.** Match the density of the surrounding code.
- **Never move/rewrite .cs files through anything that re-encodes them**; files are UTF-8.

### Hard constraints (non-negotiable)

1. **This repository contains no content sources, and must not start to.** Sources are plugins loaded at
   runtime from a separately distributed pack (`IMangaSourceService`, `IReleaseProvider` in
   `Sentrychan.Core/Interfaces`). Do **not** add scrapers, site-specific code, site names, or default
   URLs of any content site or extension repository — not in code, comments, tests, fixtures or docs.
   Test fixtures use invented hosts (`example.test`).
2. **Plugin contracts must stay backward compatible.** Packs are built outside this repo against these
   interfaces. New interface members get **default implementations** so an older pack still loads.
3. The public build (`-p:IncludeSources=false`) must still connect to nothing on its own.
4. Don't commit secrets, and don't touch release packaging/publishing (`pack.ps1`, Velopack upload,
   GitHub Releases, the website in `docs/`) — that's done on Windows, outside this plan.

### Codebase orientation

| Project | Role |
|---|---|
| `Sentrychan.Core` | Services, EF Core (`Data/AppDbContext`, migrations), models, interfaces, `Vault/` |
| `Sentrychan.UI` | Avalonia views (`Views/`) + view-models (`ViewModels/`), controls, styles (`Assets/Styles/GlobalStyles.axaml`) |
| `Sentrychan.App` | Composition root: `Program.cs` (DI, plugin loading, `db.Database.Migrate()`), Velopack update service |

Things that will bite you:
- `command.Execute().Subscribe()` crashes on unobserved errors — `RxApp.DefaultExceptionHandler` guards it; keep it.
- Resources declared in `Styles` aren't found through `Application.Current.Resources`; use `Application.Current.TryGetResource(key, theme, out v)`.
- `<Run Text="{Binding}">` inline bindings don't work in Avalonia here; use a control.
- A closed `ComboBox` changes value on mouse wheel; `<ComboBoxItem>` children bound via `SelectedItem` store the control, not the text — use `<x:String>` items.
- Shared styles: pill tabs `TabControl.pills`, status chips `Border.chip` (`.good`/`.bad`), buttons `.accent`/`.ghost`.
- The library files downloads as `Library/<Title>/Season N/<file>` (`FileMovementPipeline`, `VideoFileLocator`).
- `MangaDownloadService` owns the manga download queue (2 slots) and raises `StatusChanged`; UI rows only mirror it.
- `MangaService.SyncChaptersAsync` updates chapter rows **in place** keyed on `SourceId` — chapter ids must stay stable.

---

## Phase 0 — Foundation: a preview flavour that can't hurt a stable install

Preview installs **side by side** with stable as a separate app with separate data.

1. **`AppPaths`** (Core): one place for the data directory. Today `%AppData%/Sentrychan` is built by hand in
   ~17 files (`grep -rn '"Sentrychan"' --include=*.cs`). Replace them all. Honour an env override
   `SENTRYCHAN_DATA_DIR` (tests and manual testing need it — .NET ignores `%APPDATA%` redirection).
2. **Build flavour:** MSBuild property `Flavor` (`Stable` default, `Preview`) → a generated
   `BuildInfo.IsPreview`. Preview: data dir `%AppData%/Sentrychan Preview`, window/app name
   "Sentrychan Preview", a small "Preview" badge beside the logo (sidebar + top bar).
3. **Updates:** `VelopackUpdateService` — stable unchanged (`GithubSource(..., prerelease: false)`);
   preview uses `prerelease: true` and `UpdateOptions.ExplicitChannel = "preview"`. Packaging itself is
   done on Windows; just make the flavour select the right source/channel.
4. **Never both at once:** a named, cross-flavour mutex. If stable is running when preview starts (or
   vice versa), the second app starts with **RSS monitoring and downloads paused** and a clear banner
   ("Sentrychan is already running — monitoring is paused here so nothing downloads twice").
5. **Vault separation:** the vault root defaults to `<LibraryPath>/.cache`. The preview must default to
   its own root (e.g. `.cache-preview`) so two apps never write one encrypted index.
6. **First run of preview:** offer "Copy my library from Sentrychan" — copies the stable database and
   settings into the preview data dir (one way; stable is never written). Don't copy the vault key, and
   reset `VaultRoot` so preview gets its own vault. Explain that in the dialog.
7. **`Sentrychan.Tests`** (xUnit) added to the solution, with a first test for `AppPaths`.

Done when: both flavours build; tests pass; the log lists what needs checking on Windows.

## Phase 1 — Tidy library (Jellyfin-friendly naming)

A naming **template** used when new downloads are filed, plus a **Tidy library** tool for what's there.

- **Presets:** `Jellyfin/Plex` (**default**): `<Title> (<Year>)/Season 01/<Title> S01E01.mkv` ·
  `Minimal`: `<Title>/Season 1/01.mkv` · `Custom` with tokens `{Title} {Year} {Season:00} {Episode:00}
  {Group} {Quality} {Version}`. Specials/OVAs → `Season 00`; movies → `<Title> (<Year>)/<Title> (<Year>).ext`.
- **Folder names** lose release-group tags, resolution, ranges, `v2` etc.; the title comes from the
  series' own metadata (the `Series` row), falling back to a cleaned folder name. Use the existing
  AnitomySharp dependency for parsing file names.
- **Season numbering:** handle shows that continue episode numbers across seasons (ep 13 = S02E01) using
  the season information the app already has; when unsure, leave the file alone and report it.
- **Plan → preview → apply → undo:** the tool builds a plan (old path → new path) shown as a before/after
  list with per-item untick; nothing moves until confirmed. Each run writes a **journal** so
  "Undo last tidy" restores exactly. Collisions are reported, never overwritten.
- **Sidecars move with their video** (`.ass .srt .ssa .vtt .nfo`, `-thumb.jpg`), keeping language suffixes.
- **Skip** files that are open, still downloading, or owned by an unfinished torrent job.
- **Keep the app's records right:** update every stored path (search the models/DB for file and folder
  paths — episodes, download jobs, series folders). Store the **original file name** so episode repair
  (which finds a file's torrent by its original name) still works after a rename.
- **Exceptions:** per series "Don't tidy" and "Keep full file names".
- **UI:** Settings → Library: naming preset/template with a live example; a "Tidy library…" dialog
  (plan list, counts, Apply, Undo last tidy).
- **Tests:** planner on synthetic trees (temp dirs), sidecars, collisions, undo round-trip, record updates.

## Phase 2 — Manga source contract v2 (filters, popular, latest)

Extend `IMangaSourceService` — **all new members with defaults**:
- `SourceInfo`: stable id, language, `IsNsfw`, `SupportsLatest`.
- `GetPopularAsync(page)`, `GetLatestAsync(page)`, `SearchAsync(query, page, FilterList)` → a page
  result with `HasNextPage`. Defaults fall back to the existing search.
- **Filters modelled on Mihon's `FilterList`** so a bridged Mihon source maps 1:1: `Header`, `Separator`,
  `Select`, `Text`, `CheckBox`, `TriState` (include/exclude/ignore), `Sort` (index + ascending),
  `Group`. `GetFilterList()` returns the source's filters; the UI renders them generically.
- `LocalMangaSourceService` implements what applies. Document the contract in XML docs — pack authors
  work from them.

## Phase 3 — Mihon-style manga UI (opt-in)

Setting `MangaUiStyle` = `Classic` (default) | `Mihon`, in Settings → Appearance. **Share view-models**
with the classic UI where possible; only views differ. Plan to make it the default later and delete Classic.

- **Library:** categories (create/rename/reorder/assign), display modes (comfortable grid, compact grid,
  list, cover-only), filters (downloaded, unread, started, completed), sorts (title, last read, latest
  chapter, unread count, date added), unread + downloaded badges.
- **Updates:** new chapters across the library, newest first, grouped by day.
- **History:** recently read with resume; remove entries.
- **Browse:** a **Sources** list (language, pinned, last used) — a list you pick from, not a dropdown and not
  a page-filling grid — then per source **Popular / Latest / Filter** (a filter sheet from Phase 2) and
  **global search** across sources.
- **Title page:** chapter multi-select (select all/range), "Download next 1/5/10/unread/all", chapter
  filters (unread, downloaded, bookmarked) and sort (source order, number, upload date), bookmarks,
  "Mark previous as read", **migrate** to another source.
- **Download queue page** for manga: reorder, pause/resume all, cancel — built on `MangaDownloadService`
  (add pause and reordering there).
- New tables (categories, manga↔category, chapter bookmarks, reading history) via **EF migrations**.

## Phase 4 — Mihon extension bridge (opt-in)

Mihon extensions are Android APKs of compiled JVM code; they can't run in .NET. **Suwayomi-Server**
(MPL-2.0) runs them on desktop and exposes an API — Sentrychan manages it as a local helper process.
**Read Suwayomi's own documentation/source for its API and settings; don't assume endpoint shapes.**

- **Opt-in** in Settings → Sources: "Mihon extensions". Enabling downloads a **pinned** Suwayomi release
  that bundles its Java runtime (verify a pinned SHA-256) into `AppPaths/mihon-bridge`.
- **Process:** start on demand, bound to `127.0.0.1` on a free port, health-checked, stopped with the
  app. Put process management behind an interface; the Windows specifics (a job object so it dies with
  the app) will be verified on Windows — note it in the log.
- **Repos:** the user adds repo URLs themselves. **Ship none, suggest none, pre-fill none.**
- **Extensions:** list (language, NSFW, installed/update available), install, update, uninstall.
- **Each installed source is a Sentrychan source:** one generic `BridgedMangaSource : IMangaSourceService`
  per source — popular/latest/search/filters (Phase 2 maps directly), details, chapters, pages. Page
  images through the server's image endpoint.
- **Source preferences:** render a source's settings generically.
- **Failure modes surfaced plainly:** server not running, extension needs a web check the server can't
  pass, login required.
- **Tests:** client against recorded responses; if the container allows, an integration test against a
  real Suwayomi run on Linux.

## Phase 5 — Mihon backup import

Import a Mihon backup (`.tachibk`: gzip-compressed protobuf; schema in Mihon's source): library
entries, categories, read chapters, history. Match source ids to bridged sources (Phase 4); list what
couldn't be matched. Works without the bridge for categories/reading state of titles already present.

## Not for the cloud session (done on Windows)

Preview packaging and release, website changes, process-lifetime checks on Windows, filter support
inside the external source pack, and a visual pass over every new view.

---

## Progress log

_Newest first. Date, phase, what's done, what's next, and anything that needs checking on Windows._

- 2026-09-29 — Plan written. Nothing started.
