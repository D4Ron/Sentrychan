using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.Sources;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Sentrychan.UI.Services;

/// <summary>Picking, importing and exporting a sources file — shared by Settings and the sources guide.</summary>
public static class SourcesFileActions
{
    private static readonly FilePickerFileType SourcesType =
        new("Sentrychan sources file") { Patterns = ["*" + SourcesFile.Extension, "*.json"] };

    /// <summary>Asks for a file and imports it. Returns what happened, or null if nothing was picked.</summary>
    public static async Task<SourcesImportResult?> ImportAsync(Window owner)
    {
        var transfer = App.Services?.GetService<SourcesTransferService>()
                       ?? throw new InvalidOperationException("Sources import isn't available.");
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import a sources file",
            AllowMultiple = false,
            FileTypeFilter = [SourcesType],
        });
        if (files.Count == 0) return null;

        var releases = App.Services?.GetService<IReleaseProviders>();
        var secret = App.Services?.GetService<ISecretModeService>();
        return await transfer.ImportAsync(files[0].Path.LocalPath, url =>
            releases?.IsAdultFeed(url) == true && secret?.IsSecretModeActive != true ? "it needs secret mode" : null);
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
            FileTypeChoices = [SourcesType],
        });
        if (file == null) return null;

        var written = await transfer.ExportAsync(file.Path.LocalPath, includePacks: true, name: Environment.UserName);
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
            : $"Saved {what}. It holds your source addresses and packs — share it only with people you trust.";
    }
}
