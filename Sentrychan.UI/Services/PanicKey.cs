using Avalonia.Input;
using System;

namespace Sentrychan.UI.Services;

/// <summary>
/// Ctrl+Shift+H, anywhere in the app: instantly leaves secret mode, locks it again, closes
/// any player and returns to the library. Every private surface listens to
/// <see cref="Triggered"/> and closes itself.
/// </summary>
public static class PanicKey
{
    public static event Action? Triggered;

    public static bool Matches(KeyEventArgs e) =>
        e.Key == Key.H && e.KeyModifiers.HasFlag(KeyModifiers.Control) && e.KeyModifiers.HasFlag(KeyModifiers.Shift);

    public static void Trigger() => Triggered?.Invoke();
}
