using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Sources;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Sentrychan.UI.Services;

/// <summary>Picking, importing and exporting sources — shared by Settings, the sources guide and drag and drop.</summary>
public static class SourcesFileActions
{
    private static readonly FilePickerFileType SourcesType =
        new("Sources file, source pack or zip")
        {
            Patterns = ["*" + SourcesFile.Extension, "*.zip", "*.dll", "*.json"],
            // macOS filters by type; without these an unknown extension can't be picked there.
            AppleUniformTypeIdentifiers = ["public.data", "public.zip-archive", "public.json"],
        };

    /// <summary>
    /// Asks for what to import: files (a sources file, a pack, a zip — several at once is fine) or,
    /// with <paramref name="folder"/>, the folder a sources file was unpacked to. Null if cancelled.
    /// </summary>
    public static async Task<IReadOnlyList<string>?> PickAsync(Window owner, bool folder)
    {
        if (folder)
        {
            var dirs = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = "Choose the folder with your sources",
                AllowMultiple = false,
            });
            var paths = dirs.Select(d => d.TryGetLocalPath()).OfType<string>().ToList();
            return paths.Count == 0 ? null : paths;
        }

        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import your sources",
            AllowMultiple = true,
            FileTypeFilter = [SourcesType, FilePickerFileTypes.All],
        });
        var picked = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        return picked.Count == 0 ? null : picked;
    }

    /// <summary>
    /// Imports whatever was picked or dropped, then loads any new pack at once. The result carries
    /// each pack's status, so the caller can show whether it really loaded.
    /// </summary>
    public static async Task<SourcesImportResult> ImportPathsAsync(IEnumerable<string> paths)
    {
        var transfer = App.Services?.GetService<SourcesTransferService>()
                       ?? throw new InvalidOperationException("Sources import isn't available.");
        var releases = App.Services?.GetService<IReleaseProviders>();
        var secret = App.Services?.GetService<ISecretModeService>();
        var result = await transfer.ImportAsync(paths, url =>
            releases?.IsAdultFeed(url) == true && secret?.IsSecretModeActive != true ? "it needs secret mode" : null);

        if (App.LoadNewPacks != null)
            result = result with { PackStatus = await App.LoadNewPacks() };
        return result;
    }

    /// <summary>Whether dropped paths hold anything the importer can use — so other drops are left alone.</summary>
    public static bool LooksLikeSources(IReadOnlyList<string> paths)
    {
        try { return !SourcesInput.Read(paths).IsEmpty; }
        catch { return false; }
    }

    /// <summary>Asks where to save and exports. Returns a sentence for the status line, or null if cancelled.</summary>
    public static async Task<string?> ExportAsync(Window owner)
    {
        var transfer = App.Services?.GetService<SourcesTransferService>()
                       ?? throw new InvalidOperationException("Sources export isn't available.");
        var file = await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export my sources",
            SuggestedFileName = "My sources" + SourcesFile.Extension,
            DefaultExtension = SourcesFile.Extension.TrimStart('.'),
            FileTypeChoices = [new FilePickerFileType("Sentrychan sources file") { Patterns = ["*" + SourcesFile.Extension] }],
        });
        if (file?.TryGetLocalPath() is not { } path) return null;

        var written = await transfer.ExportAsync(path, includePacks: true, name: Environment.UserName);
        var m = written.Manifest;
        var bits = new[]
        {
            m.Feeds.Count > 0 ? $"{m.Feeds.Count} feed(s)" : null,
            string.IsNullOrWhiteSpace(m.PreferredReleaseGroups) ? null : "preferred groups",
            written.PackNames.Count > 0 ? $"{written.PackNames.Count} source pack(s)" : null,
            m.MihonRepositories.Count > 0 ? $"{m.MihonRepositories.Count} Mihon repositor{(m.MihonRepositories.Count == 1 ? "y" : "ies")}" : null,
        }.Where(b => b != null);
        var what = string.Join(", ", bits);
        return what.Length == 0
            ? "Saved, but there was nothing to put in it yet."
            : $"Saved {what}. Send the file as it is — whoever gets it drags it onto Sentrychan or uses Import. It holds your source addresses and packs: share it only with people you trust.";
    }
}
