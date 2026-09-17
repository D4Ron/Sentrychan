using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Sentrychan.UI.Services;
using System;
using System.Threading.Tasks;

namespace Sentrychan.UI.Controls;

public partial class WelcomeOverlay : UserControl
{
    private QuoteService? _quoteService;
    private bool _isPlaying;

    public WelcomeOverlay()
    {
        InitializeComponent();
    }

    public void SetQuoteService(QuoteService quoteService)
    {
        _quoteService = quoteService;
    }

    public async void Play()
    {
        if (_quoteService == null) return;

        // Re-entry guard — duplicate triggers (double subscriptions, repeated
        // theme events) would restart the animation mid-play, making the
        // overlay appear to "load twice".
        if (_isPlaying) return;
        _isPlaying = true;
        try
        {
            await PlayCoreAsync();
        }
        finally
        {
            _isPlaying = false;
        }
    }

    private async Task PlayCoreAsync()
    {

        QuoteText.Text = _quoteService!.GetRandom();
        QuoteText.Opacity = 0;
        this.Opacity = 0;
        this.IsVisible = true;

        // Entry Fade
        var fadeIn = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(300),
            Children =
            {
                new KeyFrame { Cue = new Cue(0.0), Setters = { new Setter(OpacityProperty, 0.0) } },
                new KeyFrame { Cue = new Cue(1.0), Setters = { new Setter(OpacityProperty, 1.0) } }
            }
        };
        await fadeIn.RunAsync(this);
        // Avalonia animations REVERT to the property's set value on completion.
        // Persist the end state or the overlay snaps back to invisible while
        // still blocking input — the "barely shows, then flashes again" bug.
        this.Opacity = 1;

        // Logo Scale
        var logoScale = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(450),
            Easing = new CubicEaseOut(),
            Delay = TimeSpan.FromMilliseconds(50),
            Children =
            {
                new KeyFrame { Cue = new Cue(0.0), Setters = { new Setter(ScaleTransform.ScaleXProperty, 0.7), new Setter(ScaleTransform.ScaleYProperty, 0.7) } },
                new KeyFrame { Cue = new Cue(1.0), Setters = { new Setter(ScaleTransform.ScaleXProperty, 1.0), new Setter(ScaleTransform.ScaleYProperty, 1.0) } }
            }
        };
        // Ensure LogoImage has a ScaleTransform
        if (LogoImage.RenderTransform is not ScaleTransform)
            LogoImage.RenderTransform = new ScaleTransform(1, 1);
        _ = logoScale.RunAsync(LogoImage);

        // Quote Fade
        var quoteFade = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(300),
            Delay = TimeSpan.FromMilliseconds(300),
            Children =
            {
                new KeyFrame { Cue = new Cue(0.0), Setters = { new Setter(OpacityProperty, 0.0) } },
                new KeyFrame { Cue = new Cue(1.0), Setters = { new Setter(OpacityProperty, 1.0) } }
            }
        };
        await quoteFade.RunAsync(QuoteText);
        QuoteText.Opacity = 1;                      // persist (see note above)
        if (LogoImage.RenderTransform is ScaleTransform st)
        {
            st.ScaleX = 1;                          // logo otherwise reverts to 0.7
            st.ScaleY = 1;
        }

        // Long enough to read one short quote — no longer.
        await Task.Delay(1900);

        // Dismiss Fade
        var fadeOut = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(350),
            Children =
            {
                new KeyFrame { Cue = new Cue(0.0), Setters = { new Setter(OpacityProperty, 1.0) } },
                new KeyFrame { Cue = new Cue(1.0), Setters = { new Setter(OpacityProperty, 0.0) } }
            }
        };
        await fadeOut.RunAsync(this);

        this.Opacity = 0;                           // persist the faded-out state
        this.IsVisible = false;
    }
}
