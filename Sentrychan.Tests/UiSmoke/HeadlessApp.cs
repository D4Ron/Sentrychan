using Avalonia;
using Avalonia.Headless;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(Sentrychan.Tests.UiSmoke.HeadlessApp))]

namespace Sentrychan.Tests.UiSmoke;

/// <summary>
/// The app's look without its start-up: the same theme and shared styles, on Avalonia's headless
/// platform with real Skia rendering so views can be captured. The real App builds the main
/// window from the running services, which a test doesn't have.
/// </summary>
public sealed class HeadlessApp : Application
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<HeadlessApp>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Dark;
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://Sentrychan.Tests"))
        {
            Source = new Uri("avares://Sentrychan.UI/Assets/Styles/GlobalStyles.axaml"),
        });
    }
}
