using System;
using System.IO;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sentrychan.Core.Interfaces;

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
/// </summary>
public static class PluginSourceLoader
{
    public static string UserSourcesDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Sentrychan", "sources");

    private static string BundledSourcesDir => Path.Combine(AppContext.BaseDirectory, "sources");

    public static void SeedAndLoad(IServiceProvider services, IMangaSourceRegistry registry, ILogger logger)
    {
        try { Directory.CreateDirectory(UserSourcesDir); } catch { /* best-effort */ }
        Seed(logger);
        Load(services, registry, logger);
    }

    /// <summary>Copy any bundled pack DLLs into the user folder (auto-seed for beta updates).</summary>
    private static void Seed(ILogger logger)
    {
        try
        {
            if (!Directory.Exists(BundledSourcesDir)) return;
            foreach (var dll in Directory.GetFiles(BundledSourcesDir, "*.dll"))
            {
                var dest = Path.Combine(UserSourcesDir, Path.GetFileName(dll));
                if (!File.Exists(dest) || File.GetLastWriteTimeUtc(dll) > File.GetLastWriteTimeUtc(dest))
                    File.Copy(dll, dest, overwrite: true);
            }
        }
        catch (Exception ex) { logger.LogWarning(ex, "[Sources] seeding bundled pack failed"); }
    }

    private static bool IsPluginType(Type t) =>
        !t.IsAbstract && !t.IsInterface &&
        (typeof(IMangaSourceService).IsAssignableFrom(t)
         || typeof(IReleaseProvider).IsAssignableFrom(t)
         || typeof(IAiringScheduleService).IsAssignableFrom(t));

    private static void Load(IServiceProvider services, IMangaSourceRegistry mangaRegistry, ILogger logger)
    {
        if (!Directory.Exists(UserSourcesDir)) return;

        var releases  = services.GetService<IReleaseProviders>();
        var schedules = services.GetService<IAiringScheduleRegistry>();

        foreach (var dll in Directory.GetFiles(UserSourcesDir, "*.dll"))
        {
            Type[] types;
            try { types = Assembly.LoadFrom(dll).GetTypes(); }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[Sources] failed to load {Dll}", Path.GetFileName(dll));
                continue;
            }

            foreach (var type in types)
            {
                if (!IsPluginType(type)) continue;

                // One instance per type, registered under every contract it implements. A type
                // that fails to construct is skipped without taking the rest of the pack down.
                object instance;
                try { instance = ActivatorUtilities.CreateInstance(services, type); }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "[Sources] could not create {Type} from {Dll}", type.Name, Path.GetFileName(dll));
                    continue;
                }

                if (instance is IMangaSourceService src)
                {
                    mangaRegistry.Add(src);
                    logger.LogInformation("[Sources] loaded manga source {Name} from {Dll}", src.SourceName, Path.GetFileName(dll));
                }
                if (instance is IReleaseProvider rel && releases != null)
                {
                    releases.Add(rel);
                    logger.LogInformation("[Sources] loaded release provider {Name} from {Dll}", rel.ProviderName, Path.GetFileName(dll));
                }
                if (instance is IAiringScheduleService sched && schedules != null)
                {
                    schedules.Add(sched);
                    logger.LogInformation("[Sources] loaded schedule {Type} from {Dll}", type.Name, Path.GetFileName(dll));
                }
            }
        }
    }
}
