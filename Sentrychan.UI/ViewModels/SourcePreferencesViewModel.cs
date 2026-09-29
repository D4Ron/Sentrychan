using System.Collections.ObjectModel;
using ReactiveUI;
using Sentrychan.Core.MihonBridge;

namespace Sentrychan.UI.ViewModels;

/// <summary>
/// A bridged source's own settings, rendered from what the extension declares — switches, check
/// boxes, text, single and multiple choice. Each change is saved at once; the list is then
/// rebuilt from the server's answer, because one setting can show, hide or disable others.
/// </summary>
public sealed class SourcePreferencesViewModel : ViewModelBase
{
    private readonly BridgedMangaSource _source;
    private readonly IMihonBridge _bridge;

    public SourcePreferencesViewModel(BridgedMangaSource source, IMihonBridge bridge)
    {
        _source = source;
        _bridge = bridge;
    }

    public string Title => _source.SourceName + " settings";

    public ObservableCollection<PreferenceRowVm> Rows { get; } = new();

    private string _message = string.Empty;
    public string Message { get => _message; private set => this.RaiseAndSetIfChanged(ref _message, value); }

    public bool IsEmpty => Rows.Count == 0;

    public async Task LoadAsync()
    {
        try
        {
            var client = await _bridge.ClientAsync();
            Show(await client.GetPreferencesAsync(_source.Source.Id));
        }
        catch (Exception ex) { Message = ex.Message; }
    }

    private void Show(IReadOnlyList<BridgePreference> prefs)
    {
        Rows.Clear();
        foreach (var p in prefs.Where(p => p.Visible))
            Rows.Add(new PreferenceRowVm(p, SaveAsync));
        this.RaisePropertyChanged(nameof(IsEmpty));
    }

    private async Task SaveAsync(BridgePreference pref, object value)
    {
        try
        {
            var client = await _bridge.ClientAsync();
            Show(await client.SetPreferenceAsync(_source.Source.Id, pref, value));
            Message = string.Empty;
        }
        catch (Exception ex) { Message = ex.Message; }
    }
}

public sealed class PreferenceRowVm : ViewModelBase
{
    private readonly Func<BridgePreference, object, Task> _save;
    private bool _loading = true;

    public PreferenceRowVm(BridgePreference pref, Func<BridgePreference, object, Task> save)
    {
        Preference = pref;
        _save = save;
        _boolValue = pref.BoolValue ?? false;
        _text = pref.TextValue ?? string.Empty;
        var index = pref.EntryValues.ToList().IndexOf(pref.TextValue ?? string.Empty);
        _selectedEntry = index >= 0 && index < pref.Entries.Count ? pref.Entries[index] : null;
        Choices = pref.Entries.Select((e, i) => new ChoiceVm(e, i < pref.EntryValues.Count ? pref.EntryValues[i] : e,
            pref.Values.Contains(i < pref.EntryValues.Count ? pref.EntryValues[i] : e), OnChoiceChanged)).ToList();
        SaveTextCommand = ReactiveCommand.CreateFromTask(() => _save(Preference, Text));
        _loading = false;
    }

    public BridgePreference Preference { get; }
    public string Title => Preference.Title ?? Preference.Key ?? string.Empty;

    /// <summary>Android's "%s" in a summary stands for the current value.</summary>
    public string? Summary => Preference.Summary?.Replace("%s", SelectedEntry ?? Text);

    public bool HasSummary => !string.IsNullOrWhiteSpace(Summary);
    public bool IsEnabled => Preference.Enabled;

    public bool IsToggle => Preference.Kind is PreferenceKind.Switch or PreferenceKind.CheckBox;
    public bool IsText => Preference.Kind == PreferenceKind.EditText;
    public bool IsList => Preference.Kind == PreferenceKind.List;
    public bool IsMulti => Preference.Kind == PreferenceKind.MultiSelect;

    private bool _boolValue;
    public bool BoolValue
    {
        get => _boolValue;
        set
        {
            if (_boolValue == value) return;
            this.RaiseAndSetIfChanged(ref _boolValue, value);
            if (!_loading) _ = _save(Preference, value);
        }
    }

    private string _text;
    public string Text { get => _text; set => this.RaiseAndSetIfChanged(ref _text, value); }

    /// <summary>Text is saved on demand, not per keystroke.</summary>
    public ReactiveCommand<System.Reactive.Unit, System.Reactive.Unit> SaveTextCommand { get; }

    public IReadOnlyList<string> Entries => Preference.Entries;

    private string? _selectedEntry;
    public string? SelectedEntry
    {
        get => _selectedEntry;
        set
        {
            if (_selectedEntry == value || value == null) return;
            this.RaiseAndSetIfChanged(ref _selectedEntry, value);
            var i = Preference.Entries.ToList().IndexOf(value);
            if (!_loading && i >= 0 && i < Preference.EntryValues.Count) _ = _save(Preference, Preference.EntryValues[i]);
        }
    }

    public IReadOnlyList<ChoiceVm> Choices { get; }

    private void OnChoiceChanged()
    {
        if (_loading) return;
        _ = _save(Preference, Choices.Where(c => c.IsChecked).Select(c => c.Value).ToList());
    }

    public sealed class ChoiceVm(string label, string value, bool isChecked, Action changed) : ViewModelBase
    {
        public string Label { get; } = label;
        public string Value { get; } = value;

        private bool _isChecked = isChecked;
        public bool IsChecked
        {
            get => _isChecked;
            set
            {
                if (_isChecked == value) return;
                this.RaiseAndSetIfChanged(ref _isChecked, value);
                changed();
            }
        }
    }
}
