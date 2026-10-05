using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Sentrychan.Core.Sources;
using Sentrychan.UI.ViewModels;
using Sentrychan.UI.Views;
using Sentrychan.UI.Views.Dialogs;

namespace Sentrychan.Tests.UiSmoke;

/// <summary>
/// The screens added for the Mac tester's feedback load and draw: the sources check, the problem
/// report, the first-start question, and Settings with and without advanced options. Set
/// SENTRYCHAN_SCREENSHOTS to a folder for pictures.
/// </summary>
public sealed class NewScreensSmokeTests
{
    private static void Settle(int rounds = 6) { for (var i = 0; i < rounds; i++) Dispatcher.UIThread.RunJobs(); }

    private static void Capture(TopLevel window, string name)
    {
        Settle();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (Environment.GetEnvironmentVariable("SENTRYCHAN_SCREENSHOTS") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            frame!.Save(Path.Combine(dir, name + ".png"));
        }
    }

    [AvaloniaFact]
    public void The_sources_check_shows_each_line_and_a_verdict()
    {
        var vm = new SourcesCheckViewModel { ImportSummary = "2 RSS feeds added; preferred groups added: GroupA; 1 source pack(s) installed and ready." };
        vm.Show(
        [
            new("Pack Example.Sources.dll", CheckState.Ok, "episode search (Example), 6 manga sources"),
            new("Feed example.test", CheckState.Ok, "Looks good: 75 releases, each with a download link."),
            new("Feed broken.test", CheckState.Problem, "That's a web page, not a feed."),
            new("Preferred groups", CheckState.Ok, "GroupA, GroupB"),
            new("Sources folder", CheckState.Warning, "\"packs\" isn't in a shape the app reads; it's taken in automatically on the next start."),
            new("Mihon extensions", CheckState.Info, "Off"),
        ], live: true);
        Assert.Equal("1 problem found — see the red lines below.", vm.Verdict);
        Assert.Equal(6, vm.Rows.Count);

        var dialog = new SourcesCheckDialog { DataContext = vm };
        dialog.Show();
        Capture(dialog, "sources-check");
        dialog.Close();
    }

    [AvaloniaFact]
    public void The_problem_report_and_first_start_dialogs_draw()
    {
        var report = new ProblemReportDialog();
        report.Show();
        Capture(report, "problem-report");
        report.Close();

        var done = ProblemReportDialog.ForExisting(Path.Combine(Path.GetTempPath(), "Sentrychan-Preview-report-20261005-120000.zip"));
        done.Show();
        Capture(done, "problem-report-existing");
        done.Close();

        var install = new InstallFinishedDialog();
        install.Show();
        Capture(install, "install-finished");
        install.Close();
    }

    [AvaloniaFact]
    public void Settings_hide_the_technical_options_until_asked()
    {
        var vm = new SettingsViewModel();
        var window = new Window { Width = 1200, Height = 1400, Content = new SettingsView { DataContext = vm } };
        window.Show();
        Capture(window, "settings-general");
        var hiddenText = Texts(window);
        Assert.DoesNotContain("Check feeds every (minutes)", hiddenText);
        Assert.Contains("Preferred quality", hiddenText);

        vm.ShowAdvanced = true;
        Capture(window, "settings-general-advanced");
        Assert.Contains("Check feeds every (minutes)", Texts(window));

        vm.SelectedTab = SettingsViewModel.SourcesTab;
        Capture(window, "settings-sources-advanced");
        window.Close();
    }

    private static List<string> Texts(Window window) =>
        window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text ?? "").ToList();
}
