using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core;
using Sentrychan.Core.Sources;

namespace Sentrychan.App;

/// <summary>
/// Loads everything that talks to an outside content site from external "source pack" DLLs,
/// instead of baking it into the app: manga/novel sources (<see cref="IMangaSourceService"/>),
/// release-index providers (<see cref="IReleaseProvider"/>) and schedule overrides
/// (<see cref="IAiringScheduleService"/>).
///
/// Packs live in <see cref="UserSourcesDir"/>; a build may also bundle one next to the exe
/// (<c>sources/</c>), which is copied ("seeded") into the user folder on startup so beta builds
/// keep working after an update. The public build bundles nothing, so it ships as a neutral
/// library manager and reader and the user imports a pack themselves (Mihon-style).
///
/// Packs are loaded from their bytes, not from the file: a loaded file stays unlocked, so
/// importing a newer copy while the app runs works (it takes effect on the next start), and a
/// pack imported for the first time loads at once.
/// </summary>
public sealed class PluginSourceLoader : ISourcePackHost
{
    public static string UserSourcesDir => AppPaths.Sources;

    private static string BundledSourcesDir => Path.Combine(AppContext.BaseDirectory, "sources");

    /// <summary>Pack file names a newer bundled pack replaces.</summary>
    private static readonly string[] RetiredPackFiles = ["Sentrychan.Sources.dll"];

    private readonly IServiceProvider _services;
    private readonly ILogger _logger;
    private readonly object _gate = new();
    // Keyed on file name; the write time tells a replaced copy from the one that's loaded.
    private readonly Dictionary<string, (SourcePackStatus Status, DateTime WriteTime)> _packs = new(StringComparer.OrdinalIgnoreCase);

    public PluginSourceLoader(IServiceProvider services, ILogger<PluginSourceLoader> logger)
    {
        _services = services;
        _logger = logger;
        // A pack loaded from bytes has no folder to find its own dependencies in: look in the
        // sources folder for them.
        AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
        {
            var file = Path.Combine(UserSourcesDir, new AssemblyName(e.Name).Name + ".dll");
            try { return File.Exists(file) ? Assembly.Load(File.ReadAllBytes(file)) : null; }
            catch { return null; }
        };
    }

    public IReadOnlyList<SourcePackStatus> Packs
    {
        get { lock (_gate) return Snapshot(); }
    }

    public void SeedAndLoad()
    {
        try { Directory.CreateDirectory(UserSourcesDir); } catch { /* best-effort */ }
        Seed();
        LoadNewPacks();
    }

    /// <summary>Copy any bundled pack DLLs into the user folder (auto-seed for beta updates).</summary>
    private void Seed()
    {
        try
        {
            if (!Directory.Exists(BundledSourcesDir)) return;
            var bundled = Directory.GetFiles(BundledSourcesDir, "*.dll");
            foreach (var dll in bundled)
            {
                var dest = Path.Combine(UserSourcesDir, Path.GetFileName(dll));
                if (!File.Exists(dest) || File.GetLastWriteTimeUtc(dll) > File.GetLastWriteTimeUtc(dest))
                    File.Copy(dll, dest, overwrite: true);
            }

            // The bundled pack was renamed; the old copy would load beside the new one. Only
            // retired when this build ships a replacement, so a user's own import is kept.
            if (bundled.Length > 0)
                foreach (var retired in RetiredPackFiles)
                {
                    var old = Path.Combine(UserSourcesDir, retired);
                    if (File.Exists(old) && !bundled.Any(b => Path.GetFileName(b).Equals(retired, StringComparison.OrdinalIgnoreCase)))
                    {
                        File.Delete(old);
                        _logger.LogInformation("[Sources] removed superseded pack {File}", retired);
                    }
                }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "[Sources] seeding bundled pack failed"); }
    }

    private static bool IsPluginType(Type t) =>
        !t.IsAbstract && !t.IsInterface &&
        (typeof(IMangaSourceService).IsAssignableFrom(t)
         || typeof(IReleaseProvider).IsAssignableFrom(t)
         || typeof(IAiringScheduleService).IsAssignableFrom(t));

    public IReadOnlyList<SourcePackStatus> LoadNewPacks()
    {
        lock (_gate)
        {
            if (!Directory.Exists(UserSourcesDir)) return Snapshot();
            foreach (var dll in SourcesTransferService.InstalledPacks(UserSourcesDir))
            {
                var name = Path.GetFileName(dll);
                var written = File.GetLastWriteTimeUtc(dll);
                if (_packs.TryGetValue(name, out var known))
                {
                    if (known.Status.Loaded && written > known.WriteTime && !known.Status.NeedsRestart)
                    {
                        _packs[name] = (known.Status with { NeedsRestart = true }, known.WriteTime);
                        _logger.LogInformation("[Sources] {Dll} was replaced while loaded — the new copy loads on the next start", name);
                    }
                    else if (!known.Status.Loaded && written > known.WriteTime) _packs[name] = (Load(dll), written);
                    continue;
                }
                _packs[name] = (Load(dll), written);
            }
            return Snapshot();
        }
    }

    private SourcePackStatus Load(string dll)
    {
        var name = Path.GetFileName(dll);
        Type[] types;
        try
        {
            var bytes = File.ReadAllBytes(dll);
            if (SourcesInput.CheckPack(bytes) is { } notAPack)
            {
                _logger.LogWarning("[Sources] {Dll} skipped: {Why}", name, notAPack);
                return new(name, false, notAPack, [], [], 0, false);
            }
            types = Assembly.Load(bytes).GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            // Usually a pack built for a newer (or much older) app: some contract it uses isn't here.
            var first = ex.LoaderExceptions.FirstOrDefault(e => e != null)?.Message ?? ex.Message;
            _logger.LogWarning(ex, "[Sources] failed to load {Dll}", name);
            return new(name, false, "doesn't fit this version of Sentrychan — " + first, [], [], 0, false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Sources] failed to load {Dll}", name);
            return new(name, false, "couldn't be loaded — " + ex.Message, [], [], 0, false);
        }

        var manga = new List<string>();
        var releases = new List<string>();
        var schedules = 0;
        var mangaRegistry = _services.GetService<IMangaSourceRegistry>();
        var releaseProviders = _services.GetService<IReleaseProviders>();
        var scheduleRegistry = _services.GetService<IAiringScheduleRegistry>();
        string? firstError = null;

        foreach (var type in types)
        {
            if (!IsPluginType(type)) continue;

            // One instance per type, registered under every contract it implements. A type
            // that fails to construct is skipped without taking the rest of the pack down.
            object instance;
            try { instance = ActivatorUtilities.CreateInstance(_services, type); }
            catch (Exception ex)
            {
                firstError ??= $"{type.Name} couldn't start — {ex.Message}";
                _logger.LogWarning(ex, "[Sources] could not create {Type} from {Dll}", type.Name, name);
                continue;
            }

            if (instance is IMangaSourceService src && mangaRegistry != null)
            {
                mangaRegistry.Add(src);
                manga.Add(src.SourceName);
                _logger.LogInformation("[Sources] loaded manga source {Name} from {Dll}", src.SourceName, name);
            }
            if (instance is IReleaseProvider rel && releaseProviders != null)
            {
                releaseProviders.Add(rel);
                releases.Add(rel.ProviderName);
                _logger.LogInformation("[Sources] loaded release provider {Name} from {Dll}", rel.ProviderName, name);
            }
            if (instance is IAiringScheduleService sched && scheduleRegistry != null)
            {
                scheduleRegistry.Add(sched);
                schedules++;
                _logger.LogInformation("[Sources] loaded schedule {Type} from {Dll}", type.Name, name);
            }
        }

        var any = manga.Count + releases.Count + schedules > 0;
        return new(name, any, any ? null : firstError ?? "it has no sources in it", manga, releases, schedules, false);
    }

    private List<SourcePackStatus> Snapshot() =>
        _packs.Values.Select(p => p.Status).OrderBy(s => s.FileName, StringComparer.OrdinalIgnoreCase).ToList();
}
