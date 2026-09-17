using System;
using System.Reactive;
using ReactiveUI;
using Sentrychan.Core.Interfaces;

namespace Sentrychan.UI.ViewModels;

/// <summary>One MangaDex search result with an Add-to-library button.</summary>
public class MangaResultVm : ViewModelBase
{
    public MangaSearchResult Result { get; }
    public string SourceName { get; }

    public string Title => Result.Title;
    public string CoverUrl => Result.CoverUrl;

    public string Meta
    {
        get
        {
            var bits = new System.Collections.Generic.List<string>();
            if (Result.Year.HasValue) bits.Add(Result.Year.Value.ToString());
            if (!string.IsNullOrEmpty(Result.Status))
                bits.Add(char.ToUpper(Result.Status![0]) + Result.Status.Substring(1));
            if (Result.LastChapter.HasValue) bits.Add($"{Result.LastChapter} ch");
            return string.Join("  ·  ", bits);
        }
    }

    private bool _isInLibrary;
    public bool IsInLibrary { get => _isInLibrary; set => this.RaiseAndSetIfChanged(ref _isInLibrary, value); }

    private bool _isAdding;
    public bool IsAdding { get => _isAdding; set => this.RaiseAndSetIfChanged(ref _isAdding, value); }

    public ReactiveCommand<Unit, Unit> AddCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenCommand { get; }

    public MangaResultVm(MangaSearchResult result, string sourceName,
        Func<MangaResultVm, System.Threading.Tasks.Task> onAdd, Action<MangaResultVm> onOpen, bool inLibrary)
    {
        Result = result;
        SourceName = sourceName;
        _isInLibrary = inLibrary;
        AddCommand = ReactiveCommand.CreateFromTask(() => onAdd(this));
        OpenCommand = ReactiveCommand.Create(() => onOpen(this));
    }
}
