using System;
namespace Sentrychan.UI.Services;

public interface IThemeService
{
    bool IsSecretMode { get; }
    bool HasUnlockedThisSession { get; }
    bool TryUnlock(string password);

    /// <summary>Requires the unlock code again before secret mode can be re-entered.</summary>
    void Lock() { }
    void ActivateSecretMode();
    void DeactivateSecretMode();
    void ToggleSecretMode();
    event Action<bool>? ThemeChanged;

    /// <summary>Applies a named colour theme (Yoru/Shiro/Sakura/Neon) live.</summary>
    void ApplyNamedTheme(string themeName);
}
