using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Sentrychan.UI.Views.Dialogs;

namespace Sentrychan.Tests.UiSmoke;

public class WhatsNewSmokeTests
{
    [AvaloniaFact]
    public void The_update_notes_window_shows_the_shipped_notes()
    {
        var notes = WhatsNewDialog.Notes();
        Assert.NotEmpty(notes);

        var dialog = new WhatsNewDialog(notes, "Since you last opened the app:");
        dialog.Show();
        for (var i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();

        var frame = dialog.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (Environment.GetEnvironmentVariable("SENTRYCHAN_SCREENSHOTS") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            frame!.Save(Path.Combine(dir, "whats-new.png"));
        }
        dialog.Close();
    }
}
