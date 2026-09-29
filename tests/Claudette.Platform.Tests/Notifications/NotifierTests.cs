using System.Runtime.Versioning;
using Claudette.Core.Diffs;
using Claudette.Platform.Notifications;
using Claudette.Platform.Notifications.Linux;
using Claudette.Platform.Notifications.Windows;
using Claudette.Platform.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;

namespace Claudette.Platform.Tests.Notifications;

/// <summary>OS notifications (DESIGN.md §10). The Windows and macOS tests run on those OSes only, in CI.</summary>
public class NotifierTests
{
    private static readonly OsNotification Sample = new("NeedsInput:tab1", "fix login bug", "Allow this command? dotnet test");

    [Fact]
    public void NotifySend_is_used_when_installed()
    {
        Assert.NotNull(NotifySendNotifier.TryCreate(new FakeLauncher(), NullLogger.Instance, new Probe("/usr/bin/notify-send")));
        Assert.Null(NotifySendNotifier.TryCreate(new FakeLauncher(), NullLogger.Instance, new Probe(null)));
    }

    [Fact]
    public void NotifySend_arguments_end_the_options_before_the_text()
    {
        Assert.Equal(["--app-name=Claudette", "--action=default=Open", "--wait", "--", "fix login bug", "Allow this command? dotnet test"], NotifySendNotifier.Arguments(Sample, withActions: true));
        Assert.Equal(["--app-name=Claudette", "--", "-weird title", "body"], NotifySendNotifier.Arguments(Sample with { Title = "-weird title", Body = "body" }, withActions: false));
    }

    [Fact]
    public async Task NotifySend_reports_a_click()
    {
        var launcher = new FakeLauncher();
        using var notifier = new NotifySendNotifier("/usr/bin/notify-send", launcher, NullLogger.Instance);
        var clicked = new TaskCompletionSource<string>();
        notifier.Activated += id => clicked.TrySetResult(id);

        notifier.Show(Sample);
        launcher.Started[0].WriteOutput("default");
        launcher.Started[0].Exit(0);

        Assert.Equal("NeedsInput:tab1", await clicked.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Assert.Equal("/usr/bin/notify-send", launcher.Specs[0].FileName);
    }

    [Fact]
    public async Task NotifySend_without_actions_falls_back_to_plain_notifications()
    {
        var launcher = new FakeLauncher();
        using var notifier = new NotifySendNotifier("/usr/bin/notify-send", launcher, NullLogger.Instance);

        notifier.Show(Sample);
        launcher.Started[0].WriteError("Unknown option --action=default=Open");
        launcher.Started[0].Exit(1);
        await WaitFor(() => launcher.Started.Count == 2);

        Assert.DoesNotContain("--wait", launcher.Specs[1].Arguments);
        Assert.Contains("fix login bug", launcher.Specs[1].Arguments);
    }

    [Fact]
    public void NotifySend_replacing_a_notification_stops_waiting_for_the_old_one()
    {
        var launcher = new FakeLauncher();
        using var notifier = new NotifySendNotifier("/usr/bin/notify-send", launcher, NullLogger.Instance);

        notifier.Show(Sample);
        notifier.Show(Sample with { Body = "Allow editing a.txt?" });

        Assert.Equal(1, launcher.Started[0].KillCalls);
        Assert.Equal(2, launcher.Started.Count);
    }

    [Fact]
    public void Toast_xml_escapes_the_text()
    {
        var xml = ToastContent.Xml(new OsNotification("a&b", "Tom's <tab>", "\"quoted\" & more"));

        Assert.Contains("<text>Tom&apos;s &lt;tab&gt;</text>", xml);
        Assert.Contains("<text>&quot;quoted&quot; &amp; more</text>", xml);
        Assert.Contains("launch=\"a&amp;b\"", xml);
        Assert.NotNull(System.Xml.Linq.XDocument.Parse(xml));
    }

    [Fact]
    public void Null_notifier_shows_nothing()
    {
        Assert.False(NullNotifier.Instance.IsAvailable);
        NullNotifier.Instance.Show(Sample);
        NullNotifier.Instance.SetCount(3);
        Assert.Equal(AppIconSurface.None, NullNotifier.Instance.Surface);
        NullNotifier.Instance.ShowFrame([1, 2, 3], "Claude is working");
        NullNotifier.Instance.Flash(true);
    }

    /// <summary>
    /// Builds a real toast through WinRT without showing it: checks the hand-written interface ids and vtable order
    /// against Windows itself.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows10.0.10240")]
    public void Windows_builds_a_toast()
    {
        Assert.SkipUnless(OperatingSystem.IsWindowsVersionAtLeast(10, 0, 10240), "Windows only.");
        using var notifier = new WindowsToastNotifier(NullLogger<WindowsToastNotifier>.Instance);

        var toast = notifier.Create(Sample);
        var token = toast.AddActivated(new ToastActivatedHandler(() => { }));
        toast.RemoveActivated(token);

        Assert.True(notifier.IsAvailable);
    }

    /// <summary>The test host isn't an app bundle, so macOS notifications report themselves unavailable instead of crashing.</summary>
    [Fact]
    [SupportedOSPlatform("macos")]
    public void Mac_outside_a_bundle_is_unavailable()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS only.");
        using var notifier = new Claudette.Platform.Notifications.Mac.MacNotifier(NullLogger<Claudette.Platform.Notifications.Mac.MacNotifier>.Instance);

        Assert.False(notifier.IsAvailable);
        notifier.Show(Sample);
        notifier.Remove(Sample.Id);
    }

    [Fact]
    [SupportedOSPlatform("macos")]
    public void Mac_strings_round_trip_through_Objective_C()
    {
        Assert.SkipUnless(OperatingSystem.IsMacOS(), "macOS only.");
        var nsString = Claudette.Platform.Notifications.Mac.ObjC.NSString("Claude Code 2.1.290 is ready ✓");
        try
        {
            Assert.Equal("Claude Code 2.1.290 is ready ✓", Claudette.Platform.Notifications.Mac.ObjC.ToManagedString(nsString));
        }
        finally
        {
            Claudette.Platform.Notifications.Mac.ObjC.Release(nsString);
        }
    }

    /// <summary>Builds a real jump list without committing it: checks the shell interfaces against Windows itself.</summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void Windows_builds_a_jump_list()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows only.");
        var jumpList = new Claudette.Platform.Shell.Windows.WindowsJumpList(Environment.ProcessPath!, NullLogger.Instance);

        var list = jumpList.Build([new("api", Path.GetTempPath()), new("docs", Environment.CurrentDirectory)], out var added);
        list.AbortList();

        Assert.Equal(2, added);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
        Assert.True(condition());
    }

    private sealed class Probe(string? notifySend) : IFileProbe
    {
        public bool FileExists(string path) => path == notifySend;

        public string? FindOnPath(string fileName) => fileName == "notify-send" ? notifySend : null;

        public string ExpandEnvironmentVariables(string path) => path;
    }
}
