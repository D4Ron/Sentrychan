using Avalonia.Controls;
using Avalonia.Media;
using Microsoft.EntityFrameworkCore;
using ReactiveUI;
using Sentrychan.Core.Data;
using Sentrychan.Core.Models;
using Sentrychan.Core.Interfaces;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Sentrychan.UI.Views.Dialogs;

/// <summary>Simple ViewModel for a backend option row in the picker.</summary>
public class BackendOptionVm : ReactiveObject
{
    public IDownloadBackend Backend { get; init; } = null!;
    public string DisplayName   { get; init; } = string.Empty;
    public string StatusText    { get; set; } = string.Empty;
    public string AvailabilityBadge { get; set; } = string.Empty;
    public string AvailabilityColor { get; set; } = "#6B7280";
    public bool   IsAvailable   { get; set; }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }
}

public partial class DownloadPickerDialog : Window
{
    private readonly IDownloadBackendRouter _router;
    private readonly DownloadContext _context;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private List<BackendOptionVm> _options = [];

    public DownloadPickerDialog(
        IDownloadBackendRouter router,
        DownloadContext context,
        IDbContextFactory<AppDbContext> dbFactory)
    {
        InitializeComponent();
        _router    = router;
        _context   = context;
        _dbFactory = dbFactory;

        var contextLabel = context switch
        {
            DownloadContext.Hub      => "Download Hub",
            DownloadContext.RssFeed  => "RSS Auto-download",
            DownloadContext.FillGaps => "Fill-Gaps",
            _                        => "Download"
        };
        ContextLabel.Text    = $"Context: {contextLabel}";
        ContextRun.Text      = contextLabel;

        CancelButton.Click  += (_, _) => Close(null);
        ConfirmButton.Click += async (_, _) => await ConfirmAsync();

        Opened += async (_, _) => await LoadOptionsAsync();
    }

    private async Task LoadOptionsAsync()
    {
        var displayNames = new Dictionary<string, (string Name, string Status)>
        {
            ["QBittorrent"]  = ("qBittorrent", "Requires qBittorrent running with Web API enabled"),
            ["SystemDefault"]= ("System Default (FDM, etc.)", "Opens with your OS default torrent handler"),
            ["MonoTorrent"]  = ("MonoTorrent", "In-app download — no external client needed")
        };

        _options = [];

        foreach (var backend in _router.All)
        {
            var available = await backend.IsAvailableAsync();
            var (name, status) = displayNames.TryGetValue(backend.BackendType, out var d)
                ? d : (backend.BackendType, string.Empty);

            _options.Add(new BackendOptionVm
            {
                Backend           = backend,
                DisplayName       = name,
                StatusText        = status,
                IsAvailable       = available,
                AvailabilityBadge = available ? "✓ Ready" : "✗ Unavailable",
                AvailabilityColor = available ? "#10B981" : "#9CA3AF"
            });
        }

        // Auto-select: prefer the currently active backend, or first available
        var activeMatch = _options.FirstOrDefault(
            o => o.Backend.BackendType == _router.ActiveBackendType && o.IsAvailable);
        var toSelect = activeMatch
            ?? _options.FirstOrDefault(o => o.IsAvailable)
            ?? _options.FirstOrDefault();

        if (toSelect != null)
            toSelect.IsSelected = true;

        BackendList.ItemsSource = _options;

        ConfirmButton.IsEnabled = _options.Any(o => o.IsSelected && o.IsAvailable);

        // Update Confirm enabled state when selection changes
        foreach (var opt in _options)
        {
            opt.WhenAnyValue(x => x.IsSelected).Subscribe(_ =>
                ConfirmButton.IsEnabled = _options.Any(o => o.IsSelected && o.IsAvailable));
        }
    }

    private async Task ConfirmAsync()
    {
        var selected = _options.FirstOrDefault(o => o.IsSelected);
        if (selected == null || !selected.IsAvailable) return;

        if (RememberCheckBox.IsChecked == true)
        {
            var key = _context switch
            {
                DownloadContext.Hub      => "RememberedBackend_Hub",
                DownloadContext.RssFeed  => "RememberedBackend_RSS",
                DownloadContext.FillGaps => "RememberedBackend_FillGaps",
                _                        => "RememberedBackend_Hub"
            };

            await using var db = await _dbFactory.CreateDbContextAsync();
            var config = await db.AppConfigs
                .FirstOrDefaultAsync(c => c.Key == key);
            if (config != null)
                config.Value = selected.Backend.BackendType;
            else
                db.AppConfigs.Add(new AppConfig
                    { Key = key, Value = selected.Backend.BackendType });
            await db.SaveChangesAsync();
        }

        Close(selected.Backend);
    }
}
