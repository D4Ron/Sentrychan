using System;
using System.IO;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sentrychan.Core.Interfaces;

namespace Sentrychan.App;

/// <summary>
/// Loads manga/novel sources at runtime from external "source pack" DLLs, instead of baking
/// them into the app. Sources live in <see cref="UserSourcesDir"/>; a build may also bundle a
/// pack next to the exe (<c>sources/</c>), which is copied ("seeded") into the user folder on
/// startup so beta builds keep working after an update. The public build bundles nothing, so it
/// ships as a clean reader and the user imports a pack themselves (Mihon-style).
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

    private static void Load(IServiceProvider services, IMangaSourceRegistry registry, ILogger logger)
    {
        if (!Directory.Exists(UserSourcesDir)) return;
        foreach (var dll in Directory.GetFiles(UserSourcesDir, "*.dll"))
        {
            try
            {
                var asm = Assembly.LoadFrom(dll);
                foreach (var type in asm.GetTypes())
                {
                    if (!typeof(IMangaSourceService).IsAssignableFrom(type) || type.IsAbstract || type.IsInterface)
                        continue;
                    if (ActivatorUtilities.CreateInstance(services, type) is IMangaSourceService src)
                    {
                        registry.Add(src);
                        logger.LogInformation("[Sources] loaded {Name} from {Dll}", src.SourceName, Path.GetFileName(dll));
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[Sources] failed to load {Dll}", Path.GetFileName(dll));
            }
        }
    }
}
