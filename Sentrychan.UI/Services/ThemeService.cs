using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Sentrychan.Core.Interfaces;
using System;
using System.Collections.Generic;

namespace Sentrychan.UI.Services;

public class ThemeService : IThemeService, ISecretModeService
{
    /// <summary>ISecretModeService implementation — mirrors IsSecretMode.</summary>
    public bool IsSecretModeActive => _isSecretMode;

    // Secret mode unlock phrase — change this to personalise your build.
    // Enter this phrase in Settings → the unlock box at the bottom, then press Enter.
    private const string PASSWORD = "ZABEAST";

    private bool _isSecretMode;
    private bool _hasUnlockedThisSession;
    private string _currentTheme = "Yoru";

    public bool IsSecretMode => _isSecretMode;
    public bool HasUnlockedThisSession => _hasUnlockedThisSession;

    public event Action<bool>? ThemeChanged;

    // The single ResourceDictionary we push into Application.Resources.MergedDictionaries.
    // Application.Resources outranks Application.Styles resources in Avalonia's
    // DynamicResource lookup, so whatever we put here overrides the GlobalStyles
    // palette — and swapping the dictionary raises the change notification that
    // every DynamicResource consumer listens for. This is the ONLY thing that
    // reliably repaints a running Avalonia app.
    private ResourceDictionary? _override;

    // ── Palette keys (order matches the palette arrays below 1:1) ──────
    private static readonly string[] AllThemeKeys =
    {
        "AppBackgroundBrush", "SurfaceBackgroundBrush", "CardBackgroundBrush",
        "CardHoverBackgroundBrush", "CardHoverOverlayBrush", "AccentBrush",
        "AccentLightBrush", "AccentSubtleBrush", "TextPrimaryBrush", "TextSecondaryBrush",
        "TextTertiaryBrush", "TextMutedBrush", "BorderBrush", "BorderSubtleBrush",
        "BorderEmphasisBrush", "StatusGreenBrush", "StatusRedBrush", "StatusAmberBrush",
        "AccentOrangeBrush", "PosterPlaceholderA", "PosterPlaceholderB", "PosterPlaceholderC",
        "PosterPlaceholderD", "PosterPlaceholderE", "PosterPlaceholderF"
    };

    private static readonly string[] YoruHex =
    {
        "#0D0D1A","#12121F","#1A1A2E","#20203A","#90000000","#7C3AED","#A78BFA","#2D1B69",
        "#F0F0FF","#9090B8","#5A5A88","#3A3A60","#252540","#1C1C34","#7C3AED","#00C853",
        "#F44336","#FFBF00","#FF9800","#1A0A2E","#0A1A2E","#0A2E1A","#2E1A0A","#2E0A1A","#1A2E0A"
    };
    private static readonly string[] NeonHex =
    {
        "#060608","#0A0C0E","#0E1214","#141A1C","#90000000","#00FF88","#40FFAA","#002218",
        "#E8FFF2","#70CC90","#3A6A4A","#1E3428","#1A3422","#102018","#00FF88","#00FF88",
        "#FF4466","#FFDD00","#FF8800","#0A1A0C","#0A0C1A","#0C1A0C","#1A120A","#1A0A12","#121A0A"
    };
    private static readonly string[] SakuraHex =
    {
        "#180E14","#20121C","#2A1624","#361D2E","#90000000","#E8719A","#F4A0BB","#4A1830",
        "#FFF0F5","#D0A0B8","#8A5C70","#4A2E3C","#4A2035","#34182A","#E8719A","#00C875",
        "#FF4F6E","#FFCA58","#FF8A4A","#2E1228","#1A1228","#12281A","#28221A","#281A22","#22281A"
    };
    private static readonly string[] ShiroHex =
    {
        "#F4F4F8","#EAEAF0","#FFFFFF","#F0F0FA","#14000000","#6D28D9","#8B5CF6","#EDE9FE",
        "#1A1A2E","#4A4A6A","#7A7A9A","#B0B0C8","#D4D4E8","#E4E4F4","#6D28D9","#059669",
        "#DC2626","#D97706","#EA580C","#E8E0F4","#E0E8F4","#E0F4E8","#F4EEE0","#F4E0E8","#EEF4E0"
    };

    // Secret-mode overlay — only the keys it changes (rest inherit the base theme).
    private static readonly Dictionary<string, string> SecretOverlay = new()
    {
        ["AppBackgroundBrush"]       = "#1A0A0D",
        ["SurfaceBackgroundBrush"]   = "#1F0D12",
        ["CardBackgroundBrush"]      = "#2A1018",
        ["CardHoverBackgroundBrush"] = "#2E121C",
        ["AccentBrush"]              = "#DC2626",
        ["AccentLightBrush"]         = "#EF4444",
        ["AccentSubtleBrush"]        = "#7F1D1D",
        ["TextPrimaryBrush"]         = "#FFF0F4",
        ["TextSecondaryBrush"]       = "#C0A0A8",
        ["TextTertiaryBrush"]        = "#A06070",
        ["BorderSubtleBrush"]        = "#3A1E22",
        ["BorderEmphasisBrush"]      = "#DC2626",
    };

    private static string[] BaseHex(string themeName) => themeName switch
    {
        "Neon"   => NeonHex,
        "Sakura" => SakuraHex,
        "Shiro"  => ShiroHex,
        _        => YoruHex,
    };

    // ── Public API ─────────────────────────────────────────────────────
    public void ApplyNamedTheme(string themeName)
    {
        _currentTheme = string.IsNullOrWhiteSpace(themeName) ? "Yoru" : themeName;
        Rebuild();
    }

    public bool TryUnlock(string password)
    {
        if (password == PASSWORD)
        {
            _hasUnlockedThisSession = true;
            return true;
        }
        return false;
    }

    public void ActivateSecretMode()
    {
        if (_isSecretMode) return;
        _isSecretMode = true;
        Rebuild();
        ThemeChanged?.Invoke(true);
    }

    public void DeactivateSecretMode()
    {
        if (!_isSecretMode) return;
        _isSecretMode = false;
        Rebuild();
        ThemeChanged?.Invoke(false);
    }

    public void ToggleSecretMode()
    {
        if (_isSecretMode) DeactivateSecretMode();
        else ActivateSecretMode();
    }

    // ── Core: build the effective palette and swap it in ───────────────
    private void Rebuild()
    {
        Dispatcher.UIThread.Invoke(() =>
        {
            var app = Application.Current;
            if (app?.Resources == null) return;

            var hex = BaseHex(_currentTheme);
            var next = new ResourceDictionary();
            for (int i = 0; i < AllThemeKeys.Length; i++)
                next[AllThemeKeys[i]] = new SolidColorBrush(Color.Parse(hex[i]));

            if (_isSecretMode)
                foreach (var (k, v) in SecretOverlay)
                    next[k] = new SolidColorBrush(Color.Parse(v));

            var merged = app.Resources.MergedDictionaries;
            if (_override != null) merged.Remove(_override);
            merged.Add(next);
            _override = next;
        });
    }
}
