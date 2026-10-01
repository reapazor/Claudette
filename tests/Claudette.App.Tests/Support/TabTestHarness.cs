using System.Text.Json.Nodes;
using System.Threading.Channels;
using Claudette.App.Services;
using Claudette.App.ViewModels;
using Claudette.Core;
using Claudette.Core.Processes;
using Claudette.Core.Protocol;
using Claudette.Core.Sessions;
using Claudette.Core.Settings;
using Claudette.Platform.Processes;
using Microsoft.Extensions.Time.Testing;

namespace Claudette.App.Tests.Support;

/// <summary>Plays Claude Code's side of one session for view model tests.</summary>
internal sealed class ScriptedTransport : IClaudeTransport
{
    private ScriptedProcess _process = new();
    private readonly List<JsonObject> _sent = [];

    /// <summary>
    /// After the pretend process exited, stands in for the next one, so a tab can restart (after a sign-in, say).
    /// Everything sent so far is kept.
    /// </summary>
    public void RestartIfExited()
    {
        if (_process.HasExited)
        {
            _process = new ScriptedProcess();
        }
    }

    /// <summary>
    /// A new pretend process for the next session, while the current one carries on with its own: a second tab
    /// running alongside the first. What's emitted from now on goes to the new one.
    /// </summary>
    public void StartNextProcess() => _process = new ScriptedProcess();

    /// <summary>
    /// The current pretend process, as the session starting now sees it. Stopping it ends only that process, so a tab
    /// that finishes closing after the next one started (a test doesn't wait for all of it) can't end the next one's.
    /// </summary>
    public IClaudeTransport ForSession() => new SessionTransport(this, _process);

    /// <summary>Answers to control requests by subtype; null leaves the request unanswered, an exception answers with an error.</summary>
    public Dictionary<string, Func<JsonObject, JsonObject?>> Answers { get; } = new()
    {
        ["initialize"] = _ => new JsonObject
        {
            ["models"] = new JsonArray(
                new JsonObject { ["value"] = "default", ["resolvedModel"] = "claude-opus-5-5", ["displayName"] = "Default (recommended)", ["supportsEffort"] = true, ["supportedEffortLevels"] = new JsonArray("low", "high"), ["supportsAutoMode"] = true },
                new JsonObject { ["value"] = "opus", ["resolvedModel"] = "claude-opus-5-5", ["displayName"] = "Opus", ["supportsEffort"] = true, ["supportedEffortLevels"] = new JsonArray("low", "high"), ["supportsAutoMode"] = true },
                new JsonObject { ["value"] = "haiku", ["resolvedModel"] = "claude-haiku-4-5", ["displayName"] = "Haiku", ["supportsEffort"] = false }),
            ["current_permission_mode"] = "default",
        },
        ["get_context_usage"] = _ => new JsonObject { ["totalTokens"] = 1000, ["maxTokens"] = 200000, ["percentage"] = 0.5 },
    };

    public IReadOnlyList<JsonObject> Sent
    {
        get
        {
            lock (_sent)
            {
                return _sent.ToArray();
            }
        }
    }

    public IEnumerable<string> SentUserTexts => Sent.Where(m => m["type"]?.GetValue<string>() == "user").Select(m => m["message"]!["content"]!.GetValue<string>());

    public IEnumerable<string> SentControlSubtypes => Sent.Where(m => m["type"]?.GetValue<string>() == "control_request").Select(m => m["request"]!["subtype"]!.GetValue<string>());

    /// <summary>The pretend <c>claude</c> process id, for the process monitor.</summary>
    public int? ProcessId { get; set; } = 4242;

    public ChannelReader<string> Output => _process.Output.Reader;

    public Task<TransportExit> Completion => _process.Completion.Task;

    public ValueTask SendAsync(string line, CancellationToken cancellationToken = default) => Receive(_process, line);

    /// <summary>Records what the session sent, and answers its control requests on its own process's output.</summary>
    private ValueTask Receive(ScriptedProcess process, string line)
    {
        var message = JsonNode.Parse(line)!.AsObject();
        lock (_sent)
        {
            _sent.Add(message);
        }
        if (message["type"]?.GetValue<string>() == "control_request")
        {
            var id = message["request_id"]!.GetValue<string>();
            var request = message["request"]!.AsObject();
            var subtype = request["subtype"]!.GetValue<string>();
            if (Answers.TryGetValue(subtype, out var answer))
            {
                try
                {
                    if (answer(request) is { } response)
                    {
                        process.Write(OutgoingMessages.ControlSuccess(id, response).ToJsonString());
                    }
                }
                catch (Exception ex)
                {
                    process.Write(OutgoingMessages.ControlError(id, ex.Message).ToJsonString());
                }
            }
            else
            {
                process.Write(OutgoingMessages.ControlSuccess(id, []).ToJsonString());
            }
        }
        return ValueTask.CompletedTask;
    }

    public void Emit(JsonObject message) => _process.Write(message.ToJsonString());

    public void Emit(string line) => _process.Write(line);

    /// <summary>A complete turn: init, a text reply and a result with usage.</summary>
    public void EmitTurn(string reply = "ok", string model = "claude-opus-5-5")
    {
        Emit(new JsonObject { ["type"] = "system", ["subtype"] = "init", ["session_id"] = "s1", ["model"] = model, ["permissionMode"] = "default" });
        Emit(new JsonObject
        {
            ["type"] = "assistant",
            ["message"] = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = reply }) },
        });
        Emit(new JsonObject
        {
            ["type"] = "result",
            ["subtype"] = "success",
            ["is_error"] = false,
            ["result"] = reply,
            ["session_id"] = "s1",
            ["modelUsage"] = new JsonObject
            {
                [model] = new JsonObject { ["inputTokens"] = 100, ["outputTokens"] = 20, ["cacheReadInputTokens"] = 0, ["cacheCreationInputTokens"] = 0, ["costUSD"] = 0.01 },
            },
        });
    }

    public void CloseInput() => Exit(0);

    public void Terminate() => Exit(-1);

    public void Exit(int code, string standardError = "") => _process.Exit(code, standardError);

    public ValueTask DisposeAsync()
    {
        Exit(0);
        return ValueTask.CompletedTask;
    }

    /// <summary>One pretend <c>claude</c> process: its output, and how it ended.</summary>
    private sealed class ScriptedProcess
    {
        public Channel<string> Output { get; } = Channel.CreateUnbounded<string>();

        public TaskCompletionSource<TransportExit> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool HasExited => Completion.Task.IsCompleted;

        public void Write(string line) => Output.Writer.TryWrite(line);

        public void Exit(int code, string standardError)
        {
            Output.Writer.TryComplete();
            Completion.TrySetResult(new TransportExit(code, standardError));
        }
    }

    /// <summary>A session's connection to its own pretend process; what it sends is recorded with everything else.</summary>
    private sealed class SessionTransport(ScriptedTransport transport, ScriptedProcess process) : IClaudeTransport
    {
        public ChannelReader<string> Output => process.Output.Reader;

        public Task<TransportExit> Completion => process.Completion.Task;

        public int? ProcessId => transport.ProcessId;

        public ValueTask SendAsync(string line, CancellationToken cancellationToken = default) => transport.Receive(process, line);

        public void CloseInput() => process.Exit(0, "");

        public void Terminate() => process.Exit(-1, "");

        public ValueTask DisposeAsync()
        {
            process.Exit(0, "");
            return ValueTask.CompletedTask;
        }
    }
}

internal sealed class ScriptedSessionFactory(ScriptedTransport transport, TimeProvider time) : IClaudeSessionFactory
{
    public List<ClaudeLaunchOptions> Launches { get; } = [];

    /// <summary>While set, starts fail with this, as a <c>claude</c> that couldn't start would.</summary>
    public Exception? StartFailure { get; set; }

    /// <summary>Each session started gets a pretend process of its own, for tests with more than one tab running.</summary>
    public bool ProcessPerSession { get; set; }

    public async Task<ClaudeSession> StartAsync(ClaudeLaunchOptions options, CancellationToken cancellationToken = default)
    {
        Launches.Add(options);
        if (StartFailure is { } failure)
        {
            throw failure;
        }
        if (ProcessPerSession && Launches.Count > 1)
        {
            transport.StartNextProcess();
        }
        else
        {
            transport.RestartIfExited();
        }
        var session = new ClaudeSession(transport.ForSession(), time);
        await session.InitializeAsync(options.Hooks, cancellationToken);
        return session;
    }
}

internal sealed class InlineDispatcher : IUiDispatcher
{
    // Stands in for the one UI thread: every harness shares it, and tests read view model state under it too.
    private static readonly Lock UiThread = new();

    public void Post(Action action) => RunOnUiThread(action);

    /// <summary>
    /// Serializes like a UI thread would, with a synchronization context so an <c>await</c> in posted work resumes
    /// under the lock too, as it resumes on Avalonia's UI thread in the app.
    /// </summary>
    private static void RunOnUiThread(Action action)
    {
        lock (UiThread)
        {
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(UiContext.Instance);
            try
            {
                action();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        }
    }

    /// <summary>Evaluates <paramref name="read"/> as if on the UI thread, so it can't see a collection mid-change.</summary>
    public static T Read<T>(Func<T> read)
    {
        lock (UiThread)
        {
            return read();
        }
    }

    /// <summary>Runs continuations later on a pool thread, one at a time under the lock, like a UI thread's queue.</summary>
    private sealed class UiContext : SynchronizationContext
    {
        public static readonly UiContext Instance = new();

        public override void Post(SendOrPostCallback d, object? state) =>
            ThreadPool.UnsafeQueueUserWorkItem(_ => RunOnUiThread(() => d(state)), null);

        public override void Send(SendOrPostCallback d, object? state) => RunOnUiThread(() => d(state));

        public override SynchronizationContext CreateCopy() => this;
    }
}

internal sealed class NoPlatform : IPlatformServices
{
    public List<string> OpenedUrls { get; } = [];

    public string? Clipboard { get; private set; }

    /// <summary>What the folder picker returns: null is Cancel.</summary>
    public string? FolderToPick { get; set; }

    public Task<string?> PickFolderAsync(string title) => Task.FromResult(FolderToPick);

    /// <summary>What the file picker returns: null is Cancel.</summary>
    public string? FileToPick { get; set; }

    public Task<string?> PickFileAsync(string title) => Task.FromResult(FileToPick);

    /// <summary>What the save dialog returns: null is Cancel.</summary>
    public string? SavePathToPick { get; set; }

    /// <summary>What the last save dialog suggested, and offered.</summary>
    public (string Name, IReadOnlyList<SaveFileType> Types)? LastSaveDialog { get; private set; }

    public Task<string?> PickSaveFileAsync(string title, string suggestedName, IReadOnlyList<SaveFileType> types)
    {
        LastSaveDialog = (suggestedName, types);
        return Task.FromResult(SavePathToPick);
    }

    public Task OpenUrlAsync(string url)
    {
        OpenedUrls.Add(url);
        return Task.CompletedTask;
    }

    public Task RevealFolderAsync(string path) => Task.CompletedTask;

    public Task SetClipboardTextAsync(string text)
    {
        Clipboard = text;
        return Task.CompletedTask;
    }

    /// <summary>Files, an image or text "on the clipboard", for paste tests.</summary>
    public List<string> ClipboardFiles { get; } = [];

    public byte[]? ClipboardImage { get; set; }

    public Task<IReadOnlyList<string>> GetClipboardFilesAsync() => Task.FromResult<IReadOnlyList<string>>(ClipboardFiles.ToArray());

    public Task<string?> GetClipboardTextAsync() => Task.FromResult(Clipboard);

    public Task<byte[]?> GetClipboardImageAsync() => Task.FromResult(ClipboardImage);

    public List<string> OpenedFiles { get; } = [];

    public Task OpenFileAsync(string path)
    {
        OpenedFiles.Add(path);
        return Task.CompletedTask;
    }
}

/// <summary>A process tree the test controls: which children are "running", and whether they were killed.</summary>
internal sealed class FakeProcessTree(int rootPid, TimeProvider time) : ProcessTree(rootPid)
{
    public List<(int Pid, string Name)> Children { get; } = [];

    public bool Killed { get; private set; }

    public List<int> Stopped { get; } = [];

    public override IReadOnlyList<ProcessSnapshot> Sample(bool includeCommandLines) =>
    [
        new ProcessSnapshot { Pid = RootPid, ParentPid = 1, Name = "claude", IsRoot = true, MemoryBytes = 100 << 20, CpuPercent = 1, FirstSeen = time.GetUtcNow() },
        .. Children.Select(c => new ProcessSnapshot
        {
            Pid = c.Pid, ParentPid = RootPid, Name = c.Name, MemoryBytes = 50 << 20, CpuPercent = 20,
            CommandLine = includeCommandLines ? $"{c.Name} --serve" : null, FirstSeen = time.GetUtcNow(),
        }),
    ];

    public override IReadOnlyList<int> DescendantIds() => Children.Select(c => c.Pid).ToArray();

    public override void KillAll()
    {
        Killed = true;
        Children.Clear();
    }

    public override Task StopAsync(int pid, TimeSpan grace, CancellationToken cancellationToken = default)
    {
        Stopped.Add(pid);
        Children.RemoveAll(c => c.Pid == pid);
        return Task.CompletedTask;
    }
}

internal sealed class FakeProcessTreeTracker(TimeProvider time) : IProcessTreeTracker
{
    public Dictionary<int, FakeProcessTree> Trees { get; } = [];

    public ProcessTree Track(int rootPid) => Trees.TryGetValue(rootPid, out var tree) ? tree : Trees[rootPid] = new FakeProcessTree(rootPid, time);

    public ProcessTree? Find(int rootPid) => Trees.GetValueOrDefault(rootPid);
}

/// <summary>A tab wired to a scripted Claude Code, with a fake clock and a temporary data folder.</summary>
internal sealed class TabTestHarness : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"claudette-tabtest-{Guid.NewGuid():N}");

    /// <summary>The version tests run as, unless they pass one.</summary>
    public static readonly Core.Updates.AppVersion TestVersion = new(0, 1, 0);

    /// <param name="updater">Claude Code's installation, for update tests; when given, update checks are set up too.</param>
    /// <param name="launcher">Starts processes other than Claude Code, which the scripted sessions stand in for.</param>
    /// <param name="dispatcher">The UI thread: an inline stand-in for view model tests, Avalonia's own for rendered UI tests.</param>
    /// <param name="appInstaller">Installs Claudette's own updates; by default none can be.</param>
    /// <param name="http">Answers Claudette's own web requests; by default every request fails, so nothing reaches the network.</param>
    /// <param name="appVersion">
    /// Claudette's version; by default <see cref="TestVersion"/> rather than the built one, so the Settings snapshots
    /// don't change when the version does.
    /// </param>
    /// <param name="loginShell">Stands in for the user's login shell (DESIGN.md §13); by default none is read.</param>
    public TabTestHarness(Action<AppSettings>? configure = null, FakeClaudeUpdater? updater = null, IProcessLauncher? launcher = null, IUiDispatcher? dispatcher = null,
        Core.Updates.IAppInstaller? appInstaller = null, HttpMessageHandler? http = null, Core.Updates.AppVersion? appVersion = null, ILoginShell? loginShell = null)
    {
        Directory.CreateDirectory(Path.Combine(_root, "work"));
        Directory.CreateDirectory(ProjectsDirectory);
        Trees = new FakeProcessTreeTracker(Time);
        // Other programs' folders (the Epic launcher's, Unity Hub's) are empty ones here, never the real ones (DESIGN.md §18).
        var otherPrograms = new Core.ProjectTools.ProjectToolPaths(Path.Combine(_root, "home"), Path.Combine(_root, "appdata"), Path.Combine(_root, "localappdata"),
            Path.Combine(_root, "programdata"), Path.Combine(_root, "programfiles"), Path.Combine(_root, "applications"));
        Services = new AppServices(AppPaths.Under(_root), launcher ?? new ProcessLauncher(), Time, Platform, dispatcher ?? new InlineDispatcher(), processTrees: Trees, notifier: Notifier,
            appInstaller: appInstaller, httpHandler: http ?? new OfflineHandler(), appVersion: appVersion ?? TestVersion, loginShell: loginShell,
            projectToolPaths: otherPrograms, sleepBlocker: SleepBlocker, loginItems: LoginItems);
        Services.Notifications.UseBadge(Notifier);
        // The machine running the tests doesn't decide whether the Claude app is available (its ANTHROPIC_BASE_URL, say).
        Services.RemoteControl.EnvironmentVariable = _ => null;
        // Nor do its organization's managed Claude Code settings (DESIGN.md §7, "Starting mode").
        Services.ClaudeManagedSettingsDirectory = null;
        configure?.Invoke(Services.Settings);
        if (updater is not null)
        {
            Services.UpdaterFactory = _ => updater;
            Services.UseInstall(new Core.Installation.ClaudeInstall("claude", updater.Installed));
        }
        Services.ProjectsDirectory = ProjectsDirectory;
        Factory = new ScriptedSessionFactory(Transport, Time);
        Services.UseSessionFactory(Factory);
        Shell = new ShellViewModel(Services, () => OnAuthenticationRequired());
    }

    /// <summary>What the main window does when a tab finds Claude Code signed out (DESIGN.md §11).</summary>
    public Action OnAuthenticationRequired { get; set; } = () => { };

    public FakeProcessTreeTracker Trees { get; }

    public NoPlatform Platform { get; } = new();

    public FakeNotifier Notifier { get; } = new();

    /// <summary>Stands in for keeping the computer awake while tabs are connected to the Claude app (DESIGN.md §18).</summary>
    public FakeSleepBlocker SleepBlocker { get; } = new();

    /// <summary>Stands in for the OS's login entry (DESIGN.md §9, "Starting at login"): never the real Run key or LaunchAgents.</summary>
    public Core.Tests.Support.FakeLoginItems LoginItems { get; } = new();

    /// <summary>Stands in for Claude Code's <c>projects</c> folder.</summary>
    public string ProjectsDirectory => Path.Combine(_root, "projects");

    public string Root => _root;

    /// <summary>Writes a transcript where Claude Code would keep it.</summary>
    public string WriteTranscript(string sessionId, params string[] lines)
    {
        var folder = Path.Combine(ProjectsDirectory, "work-project");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, $"{sessionId}.jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    public FakeTimeProvider Time { get; } = new(DateTimeOffset.Parse("2026-09-28T12:00:00Z"));

    public ScriptedTransport Transport { get; } = new();

    public ScriptedSessionFactory Factory { get; }

    public AppServices Services { get; }

    public ShellViewModel Shell { get; }

    public string WorkFolder => Path.Combine(_root, "work");

    public async Task<TabViewModel> OpenTabAsync()
    {
        await Shell.OpenFolderAsync(WorkFolder);
        var tab = Shell.SelectedTab!;
        // Idle comes first; the start's own events and context usage request follow, and would undo a status a test sets.
        await Eventually(() => tab.Status == TabStatus.Idle && tab.IsSettled, "the tab to start");
        return tab;
    }

    /// <summary>
    /// Waits until <paramref name="condition"/> holds, checking every 10 ms. The deadline is generous because some
    /// waits are on real processes (git, for the file index), which a busy CI machine can take seconds to start.
    /// </summary>
    public static async Task Eventually(Func<bool> condition, string? what = null)
    {
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (InlineDispatcher.Read(condition))
            {
                return;
            }
            await Task.Delay(10);
        }
        Assert.Fail($"Timed out waiting for {what ?? "the condition"}.");
    }

    public async ValueTask DisposeAsync()
    {
        await Shell.DisposeAsync();
        await Services.DisposeAsync();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

internal static class SettingsExtensions
{
    public static QuickSuffix Suffix(this AppServices services, string id) => services.Settings.QuickSuffixes.Single(s => s.Id == id);
}

/// <summary>Fails every request, so a test that doesn't script Claudette's web requests can't reach the network.</summary>
public sealed class OfflineHandler : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        throw new HttpRequestException("Tests don't reach the network.");
}
