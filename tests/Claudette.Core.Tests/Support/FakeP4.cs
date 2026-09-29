using System.Globalization;
using System.Threading.Channels;
using Claudette.Core.Processes;

namespace Claudette.Testing;

/// <summary>
/// A pretend <c>p4</c> for Perforce ticket handling tests (DESIGN.md §18): answers <c>-ztag info</c>, <c>set -q</c>,
/// <c>-ztag login -s</c> and <c>login</c> (reading the password from standard input) from state the test sets, and
/// records every command and what was written to it. Anything else started through it exits 1 straight away, unless
/// <see cref="Other"/> answers it. Shared by the Core and App tests.
/// </summary>
internal sealed class FakeP4(TimeProvider? time = null) : IProcessLauncher
{
    private readonly List<FakeP4Run> _runs = [];

    /// <summary>The clock tickets expire by; settable, for a harness that makes its clock after its launcher.</summary>
    public TimeProvider Time { get; set; } = time ?? TimeProvider.System;

    public string User { get; set; } = "matt";

    public string Client { get; set; } = "matt-ws";

    /// <summary>What <c>p4 set -q P4PORT</c> says; null when P4PORT isn't set anywhere.</summary>
    public string? Port { get; set; } = "ssl:perforce:1666";

    public string ServerAddress { get; set; } = "perforce:1666";

    /// <summary>The workspace root; null leaves it out of <c>p4 info</c>.</summary>
    public string? Root { get; set; }

    /// <summary>False reports the client as <c>*unknown*</c>: not a workspace.</summary>
    public bool IsWorkspace { get; set; } = true;

    public string Password { get; set; } = "s3cret";

    /// <summary>When the ticket expires; null means not logged in at all.</summary>
    public DateTimeOffset? TicketExpires { get; set; }

    public TimeSpan TicketLifetime { get; set; } = TimeSpan.FromHours(12);

    /// <summary>The server can't be reached.</summary>
    public bool Unreachable { get; set; }

    /// <summary>P4LOGINSSO is set.</summary>
    public bool SingleSignOn { get; set; }

    /// <summary>What <c>p4 set -q P4PASSWD</c> says.</summary>
    public string? ConfiguredPassword { get; set; }

    /// <summary>When set, a login waits for it before answering, to test logins that overlap.</summary>
    public TaskCompletionSource? HoldLogins { get; set; }

    /// <summary>Answers anything that isn't <c>p4</c>: exit code, standard output. Null exits 1.</summary>
    public Func<ProcessStartSpec, (int ExitCode, string Output)?>? Other { get; set; }

    public string Executable { get; set; } = "p4";

    public IReadOnlyList<FakeP4Run> Runs
    {
        get
        {
            lock (_runs)
            {
                return _runs.ToArray();
            }
        }
    }

    public IReadOnlyList<FakeP4Run> Logins => Runs.Where(r => r.Command == "login" && !r.Arguments.Contains("-s")).ToArray();

    public int StatusChecks => Runs.Count(r => r.Command == "login" && r.Arguments.Contains("-s"));

    /// <summary>Logs in the way the user would in a terminal.</summary>
    public void LogInYourself() => TicketExpires = Time.GetUtcNow() + TicketLifetime;

    /// <summary>Everything started, <c>p4</c> or not.</summary>
    public IReadOnlyList<ProcessStartSpec> Started
    {
        get
        {
            lock (_runs)
            {
                return _started.ToArray();
            }
        }
    }

    private readonly List<ProcessStartSpec> _started = [];

    public IRunningProcess Start(ProcessStartSpec spec)
    {
        var run = new FakeP4Run(spec);
        lock (_runs)
        {
            _started.Add(spec);
            if (spec.FileName == Executable)
            {
                _runs.Add(run);
            }
        }
        return new FakeP4Process(this, run);
    }

    /// <summary>Plays <c>p4</c>: exit code, standard output, standard error.</summary>
    internal async Task<(int, string, string)> AnswerAsync(FakeP4Run run)
    {
        if (run.Spec.FileName != Executable)
        {
            return Other?.Invoke(run.Spec) is { } other ? (other.ExitCode, other.Output, "") : (1, "", "not p4");
        }
        const string connectFailed = "Perforce client error:\n\tConnect to server failed; check $P4PORT.\n\tTCP connect to perforce:1666 failed.";
        var now = Time.GetUtcNow();
        switch (run.Command)
        {
            case "info" when Unreachable:
                return (1, "", connectFailed);
            case "info":
                var root = Root is null ? "" : $"... clientRoot {Root}\n";
                return (0, $"... userName {run.User ?? User}\n... clientName {(IsWorkspace ? Client : "*unknown*")}\n{root}... serverAddress {ServerAddress}\n... serverVersion P4D/LINUX26X86_64/2025.1/1234567 (2025/06/01)\n", "");
            case "set":
                var name = run.Arguments.LastOrDefault() ?? "";
                var value = name switch
                {
                    "P4PORT" => Port,
                    "P4LOGINSSO" => SingleSignOn ? "/usr/local/bin/sso-login %user%" : null,
                    "P4PASSWD" => ConfiguredPassword,
                    _ => null,
                };
                return (0, value is null ? "" : $"{name}={value}\n", "");
            case "login" when Unreachable:
                return (1, "", connectFailed);
            case "login" when run.Arguments.Contains("-s"):
                if (TicketExpires is not { } expires)
                {
                    return (1, "", "Perforce password (P4PASSWD) invalid or unset.");
                }
                if (expires <= now)
                {
                    return (1, "", "Your session has expired, please login again.");
                }
                var seconds = ((long)(expires - now).TotalSeconds).ToString(CultureInfo.InvariantCulture);
                return (0, $"... User {User}\n... Expiration {seconds}\n... TicketExpiration {seconds}\n", "");
            case "login":
                if (HoldLogins is { } hold)
                {
                    await hold.Task;
                }
                if (run.Input.FirstOrDefault() != Password)
                {
                    return (1, "Enter password: ", "Password invalid.");
                }
                TicketExpires = Time.GetUtcNow() + TicketLifetime;
                return (0, $"Enter password: \nUser {run.User ?? User} logged in.\n", "");
            default:
                return (0, "", "");
        }
    }
}

/// <summary>One command started through <see cref="FakeP4"/>.</summary>
internal sealed class FakeP4Run
{
    private readonly List<string> _input = [];

    public FakeP4Run(ProcessStartSpec spec)
    {
        Spec = spec;
        var args = spec.Arguments.ToList();
        // Global options come first.
        var i = 0;
        while (i < args.Count && args[i].StartsWith('-') && args[i] != "-s")
        {
            switch (args[i])
            {
                case "-p":
                    Port = args[i + 1];
                    i += 2;
                    break;
                case "-u":
                    User = args[i + 1];
                    i += 2;
                    break;
                default:
                    i++;
                    break;
            }
        }
        Command = i < args.Count ? args[i] : "";
        Arguments = args.Skip(i + 1).ToArray();
    }

    public ProcessStartSpec Spec { get; }

    public string Command { get; }

    public IReadOnlyList<string> Arguments { get; }

    /// <summary>The <c>-p</c> global option, if given.</summary>
    public string? Port { get; }

    /// <summary>The <c>-u</c> global option, if given.</summary>
    public string? User { get; }

    /// <summary>Lines written to standard input.</summary>
    public IReadOnlyList<string> Input
    {
        get
        {
            lock (_input)
            {
                return _input.ToArray();
            }
        }
    }

    public void AddInput(string line)
    {
        lock (_input)
        {
            _input.Add(line);
        }
    }
}

/// <summary>Answers when standard input is closed, which <c>ProcessRunner</c> does right after writing any input.</summary>
internal sealed class FakeP4Process(FakeP4 p4, FakeP4Run run) : IRunningProcess
{
    private readonly Channel<string> _stdout = Channel.CreateUnbounded<string>();
    private readonly Channel<string> _stderr = Channel.CreateUnbounded<string>();
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _answered;

    public int Id => 7000;

    public ChannelReader<string> StandardOutput => _stdout.Reader;

    public ChannelReader<string> StandardError => _stderr.Reader;

    public Task<int> Exited => _exited.Task;

    public ValueTask WriteLineAsync(string line, CancellationToken cancellationToken = default)
    {
        run.AddInput(line);
        return ValueTask.CompletedTask;
    }

    public void CloseStandardInput()
    {
        if (Interlocked.Exchange(ref _answered, 1) == 0)
        {
            _ = AnswerAsync();
        }
    }

    private async Task AnswerAsync()
    {
        var (code, output, error) = await p4.AnswerAsync(run);
        Write(_stdout, output);
        Write(_stderr, error);
        _stdout.Writer.TryComplete();
        _stderr.Writer.TryComplete();
        _exited.TrySetResult(code);
    }

    private static void Write(Channel<string> channel, string text)
    {
        foreach (var line in text.Split('\n'))
        {
            if (line.Length > 0)
            {
                channel.Writer.TryWrite(line);
            }
        }
    }

    public void Kill()
    {
        _stdout.Writer.TryComplete();
        _stderr.Writer.TryComplete();
        _exited.TrySetResult(-1);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
