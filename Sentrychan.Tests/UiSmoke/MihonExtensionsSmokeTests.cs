using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Sentrychan.Core.Interfaces;
using Sentrychan.Core.MihonBridge;
using Sentrychan.Core.Services;
using Sentrychan.Tests.MihonBridge;
using Sentrychan.UI.ViewModels;
using Sentrychan.UI.Views;
using Sentrychan.UI.Views.Dialogs;

namespace Sentrychan.Tests.UiSmoke;

/// <summary>Settings → Sources → Mihon extensions and a source's settings dialog, over recorded server answers.</summary>
public sealed class MihonExtensionsSmokeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("sentrychan-mihonui-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class Secret(bool on) : ISecretModeService
    {
        public bool IsSecretModeActive => on;
    }

    private static void Pump()
    {
        for (var i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();
    }

    private static void Capture(Window window, string name)
    {
        Pump();
        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        if (Environment.GetEnvironmentVariable("SENTRYCHAN_SCREENSHOTS") is { Length: > 0 } dir)
        {
            Directory.CreateDirectory(dir);
            frame!.Save(Path.Combine(dir, name + ".png"));
        }
    }

    private MihonBridgeService Bridge(MemoryConfig config, FakeSuwayomi server)
    {
        var layout = new BridgeLayout(_root);
        Directory.CreateDirectory(layout.ServerDir("vT"));
        File.WriteAllText(Path.Combine(layout.ServerDir("vT"), ".installed"), "");
        return new MihonBridgeService(config, new MangaSourceRegistry([]), NullLogger<MihonBridgeService>.Instance,
            new MihonBridgeServiceTests.FakeLauncher(), layout, "vT", new BridgeAsset("x", new string('0', 64)),
            new Uri("https://example.test/x"), server, TimeSpan.FromSeconds(5));
    }

    [AvaloniaFact]
    public async Task The_extensions_section_renders_off_confirming_and_running()
    {
        var config = new MemoryConfig();
        var server = new FakeSuwayomi();
        using var bridge = Bridge(config, server);
        var vm = new MihonExtensionsViewModel(bridge, config, new Secret(false));
        var window = new Window
        {
            Width = 900, Height = 900,
            Content = new ScrollViewer { Content = new MihonExtensionsView { DataContext = vm, Margin = new(24) } },
        };
        window.Show();

        await vm.LoadAsync();
        Assert.True(vm.IsOff);
        Capture(window, "mihon-extensions-off");
        vm.BeginEnableCommand.Execute().Subscribe();
        Capture(window, "mihon-extensions-confirm");

        // Turned on (already installed here) and started.
        config.Values[MihonBridgeService.EnabledKey] = true;
        await bridge.InitializeAsync();
        await bridge.RefreshSourcesAsync();
        await vm.LoadAsync();
        Assert.True(vm.IsRunning);
        Assert.Single(vm.Repos);
        // The adult extension stays hidden outside secret mode; the mixed one too.
        Assert.Equal(["Example Reader"], vm.Extensions.Select(e => e.Name));
        Assert.True(vm.Extensions[0].CanConfigure);
        Capture(window, "mihon-extensions-running");

        vm.ExtensionFilter = "nothing like this";
        Assert.False(vm.HasExtensions);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Secret_mode_shows_adult_extensions()
    {
        var config = new MemoryConfig { Values = { [MihonBridgeService.EnabledKey] = true } };
        using var bridge = Bridge(config, new FakeSuwayomi());
        var vm = new MihonExtensionsViewModel(bridge, config, new Secret(true));
        await bridge.ClientAsync();
        await vm.LoadAsync();
        Assert.Equal(3, vm.Extensions.Count);
    }

    [AvaloniaFact]
    public async Task A_sources_settings_render_every_kind()
    {
        var config = new MemoryConfig { Values = { [MihonBridgeService.EnabledKey] = true } };
        var server = new FakeSuwayomi();
        using var bridge = Bridge(config, server);
        var sources = await bridge.RefreshSourcesAsync();
        var vm = new SourcePreferencesViewModel(sources[0], bridge);
        var dialog = new SourcePreferencesDialog { DataContext = vm };
        dialog.Show();
        await vm.LoadAsync();

        Assert.Equal(4, vm.Rows.Count); // the hidden one isn't shown
        Assert.Equal("High", vm.Rows[1].SelectedEntry);
        Assert.Equal("High", vm.Rows[1].Summary); // "%s" → the current value
        Capture(dialog, "mihon-source-settings");

        vm.Rows[0].BoolValue = true;
        await Task.Delay(50);
        Assert.Equal("""{"position":0,"switchState":true}""", server.Last("SetPreference")["variables"]!["change"]!.ToJsonString());
        dialog.Close();
    }
}
