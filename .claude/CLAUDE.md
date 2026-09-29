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
   GitHub Releases) or the website in `docs/` — that's done on Windows, outside this plan. The one
   exception is the website section of Phase 6.

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

## Phase 6 — macOS and Linux

The UI (Avalonia) and runtime (.NET 9) are cross-platform already; the work is the Windows-only edges.
Find them with `grep -rn "ProtectedData\|DllImport\|user32\|FileAttributes.Hidden\|\.exe\"\|Registry" --include=*.cs`.

- **Paths:** `AppPaths` (Phase 0) resolves per OS — `%AppData%` on Windows,
  `~/Library/Application Support` on macOS, `$XDG_DATA_HOME` (or `~/.local/share`) on Linux.
- **Secrets:** an `ISecretStore` for everything DPAPI protects today (vault key, sign-in session):
  DPAPI on Windows, Keychain on macOS, the Secret Service (libsecret) on Linux, with a clear error — not
  a silent plaintext fallback — when none is available. Existing Windows data must keep opening.
- **Video:** LibVLC per OS (`VideoLAN.LibVLC.Mac` on macOS; the system libvlc on Linux — detect it and
  explain how to install it if missing).
- **Notifications and tray:** keep the Windows toasts; add macOS and Linux implementations (or Avalonia's
  own where it suffices) behind the existing notification interface.
- **Everything else Windows-specific** (hidden/system attributes, bundled `.exe` helpers such as the
  tunnel client, `Process.Start` of URLs/folders) gets a per-OS path or a graceful "not available on
  this OS" — never a crash. The Mihon bridge (Phase 4) downloads the matching Suwayomi build per OS.
- **Builds:** a GitHub Actions workflow (on `preview`) that builds and packs unsigned macOS (arm64 and
  x64) and Linux (x64 AppImage) artifacts with Velopack and uploads them as **workflow artifacts only** —
  it must not create or edit GitHub Releases. Apple Silicon binaries need at least ad-hoc signing to run.
- **macOS signing is not funded.** Without Apple's paid Developer ID the app is unsigned and
  unnotarised: macOS will block the first launch until the user allows it in System Settings →
  Privacy & Security → "Open Anyway". Don't try to work around Gatekeeper; document it.
- **Website — the one exception to "no website changes", and only once this phase works:** on `preview`
  (it goes live when merged at release time), in `docs/index.html`:
  - Download buttons per OS (detect the visitor's OS; always show links to the other two). Stable-only
    until a stable release carries Mac/Linux assets — the preview card lists what the preview has.
  - Wording that says "on Windows" (tagline, meta description, Installing) covers all three.
  - In **Installing**, a **macOS notice**: the Mac version works but is unsigned, so macOS warns on first
    launch (with the Open Anyway steps); signing and notarising it needs an Apple developer membership,
    which needs **further funding** — link it with the page's existing funding link (class `js-fund`).
    State plainly that funding is what stands between the Mac build and a warning-free install.
  - A **Linux notice**: AppImage, how to make it executable, the libvlc dependency for video.
- **Tests:** `AppPaths` per OS, the secret-store selection, and that the build runs on Linux in CI.

## Not for the cloud session (done on Windows)

Preview packaging and release, website changes other than the Phase 6 section above, process-lifetime
checks on Windows, filter support inside the external source pack, and a visual pass over every new view.

---

## Progress log

_Newest first. Date, phase, what's done, what's next, and anything that needs checking on Windows._

- 2026-09-29 — **Phase 1 done** (Tidy library). 94 tests pass; both flavours build.
  - **Core (`Sentrychan.Core/Library/`):** `NamingTemplate` (presets + tokens; empty tokens vanish with
    their brackets; Windows-illegal chars dropped like the old folder names), `ReleaseNameParser`
    (AnitomySharp + bare "05" + strict SxxEyy), `FolderNameCleaner`, `EpisodeNumbering` (continued counts →
    season-relative only when every earlier season's total is known; otherwise *unsure*), `LibraryShows`
    (seasons sharing a base title share a folder; folder keys match "Show", "Show (2020)", tagged names),
    `TidyPlanner`, `TidyExecutor` (journal in `AppPaths/tidy-journals`, each move recorded *before* it
    happens; undo reverses only what it can see happened), `TidyRecords`, `LibraryFiling`,
    `LibraryMetadata`, `LibraryTidyService`.
  - **Data:** migration `AddLibraryTidy` — `Series.Year/MediaType/TidyExcluded/KeepFileNames`, table
    `LibraryFileOrigins` (current path → name it downloaded as; path NOCASE unique).
  - **App behaviour changes:** new downloads are filed by the template (default Jellyfin/Plex), season
    from the RSS title or the series title; uncertain numbers keep the release name in the template's
    folders; "Don't tidy" keeps the old layout. Repair/fill-gaps use the original name (cached-torrent
    lookup *and* re-staging). Locator/library scan understand both layouts and original names.
    `ITitleResolverService.GetByMalId` (default `null`) gives year/type from the offline DB.
  - **UI:** Settings → Library → File naming (preset, custom template, live example, "Tidy library…");
    `TidyLibraryDialog` (before → after list, problems first, ticks, counts, Move, Undo last tidy;
    playback positions follow); series page → "Library Files" (Don't tidy / Keep full file names).
  - **Needs checking on Windows:** the dialog and settings visually; a real tidy + undo on a copy of a
    library (incl. a case-only rename, which goes via a temp name); the open-file check (`FileShare.None`)
    against a file playing in the internal player and in an external one; repair of a renamed episode
    end to end.
  - **Heads-up:** stable (main) doesn't know the new layout — its locator won't find episodes the
    preview filed as "Show (2023)/Season 01/Show S01E05.mkv", so a stable run on the same library could
    re-download them (Minimal's bare "05" isn't recognised by stable either). While both are used on one
    library, either set "Don't tidy" on the series or port the locator/normaliser change to stable.

- 2026-09-29 — **Phase 0 done** (branch `preview`). Both flavours build (`dotnet build Sentrychan.sln`,
  add `-p:Flavor=Preview`; also with `-p:IncludeSources=false`); `dotnet test Sentrychan.Tests` → 27 pass.
  Warning count unchanged from `main` (26).
  - **Build fix first:** `Assets/frpc.exe` is git-ignored, so a fresh clone failed MSB3030. It's now
    copied only if present (`FrpTunnelService` already reports a missing binary).
  - **`AppPaths`** (Core) replaces every hand-built `%AppData%/Sentrychan`; `SENTRYCHAN_DATA_DIR`
    overrides. `AppPaths.StableDataDir` is stable's folder whatever the build (the copy source).
  - **Flavour:** `Directory.Build.props` → `Flavor` (Stable|Preview, anything else fails the build);
    Core generates `obj/.../BuildInfo.g.cs` (`BuildInfo.Flavor`, `.IsPreview`, `.AppName`). Preview:
    data in `Sentrychan Preview`, title/tray/product name "Sentrychan Preview", `Border.chip.preview`
    badge beside the logo (sidebar + top bar).
  - **Updates:** `VelopackUpdateService` — stable unchanged; preview `prerelease: true` +
    `ExplicitChannel = "preview"`. Preview packages must be packed with `--channel preview` (Windows).
  - **Never both at once:** `InstanceGuard` (Core). Single-instance mutex/show-event names are now per
    flavour (stable keeps the old names); at startup each probes the other's mutex (by taking it —
    stale named mutexes survive crashes on Unix). If held: RSS checks (incl. manual/tray), torrent
    enqueue, pending promotion + startup torrent resume, download-folder watcher, manga chapter
    downloads and the manga update check are skipped for the session, and a banner shows
    `InstanceGuard.PausedMessage`. Not persisted — the user's own Start/Stop survives. Decided once at
    startup; restart to resume. Verified across real processes on Linux (held/released/crashed).
  - **Vault:** `VaultService.DefaultRoot` → `<Library>/.cache-preview` for preview. A pinned
    `VaultRoot` is left alone (the copy clears it).
  - **First run of preview:** `StableLibraryCopy` + `CopyFromStableDialog`, offered when the preview
    library is empty, stable has a DB and it hasn't been answered (`StableCopyOffered`). Only while
    stable isn't running (checked on click). Stages a file copy (db + leftover -wal, `sources/`,
    `Covers/`, `ImageCache/`, `playback.json`) into `import-from-stable`, fixes it up with plain SQL
    (drop `VaultRoot`, repoint `Series.PosterPath`/`Manga.CoverPath`, drop Pending/Downloading jobs,
    mark offered), then restarts (`--restarted` makes the new process wait for the old one's mutex).
    `Program` swaps it in before the DB opens; what it replaces goes to `before-import-from-stable-<ts>`.
    Not copied: `vault.key`, `Auth/` (session), logs, caches.
  - **Needs checking on Windows:** badge + banner + copy dialog visually (both layouts, secret mode);
    two flavours side by side (either order) → banner, nothing downloads in the second, stable's
    window isn't surfaced by a preview launch; the copy end-to-end incl. the automatic restart from an
    installed (Velopack) preview; preview toasts — `WindowsNotificationService` still uses the
    `'Sentrychan'` notifier id, which may need the preview's own AUMID once it's packed; Velopack
    preview channel against a real prerelease; that a stable build from before this change is
    detected by the preview (same mutex name, should just work). The reverse (an old stable noticing
    a running preview) can't work until stable carries `InstanceGuard`.
  - **Known limits / follow-ups:** the pause doesn't lift if the other app quits (restart needed);
    no Settings entry to re-offer the copy after "Start fresh"; if stable's DB is from a *newer*
    schema than the preview knows, the copy is used as-is. The app doesn't start on Linux yet (named
    `EventWaitHandle` is Windows-only) — Phase 6.
  - **Cloud container note:** the default .NET download host is blocked here. The SDK was installed by
    extracting the Debian 12 `dotnet-sdk-9.0` debs from packages.microsoft.com into `/root/.dotnet`
    (host, hostfxr, runtime, aspnetcore-runtime, targeting/apphost packs); run built apps with
    `DOTNET_ROOT=/root/.dotnet`.
  - **Next:** Phase 1 (Tidy library).

- 2026-09-29 — Phase 6 (macOS and Linux, plus its website section) added.
- 2026-09-29 — Plan written. Nothing started.
