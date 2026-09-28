using System.Diagnostics;
using System.Text;
using System.Threading.Channels;

namespace Claudette.Core.Processes;

/// <summary>Starts real processes with redirected, UTF-8, line-based standard streams.</summary>
public sealed class ProcessLauncher : IProcessLauncher
{
    public IRunningProcess Start(ProcessStartSpec spec)
    {
        var startInfo = new ProcessStartInfo(spec.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in spec.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        if (spec.WorkingDirectory is not null)
        {
            startInfo.WorkingDirectory = spec.WorkingDirectory;
        }
        if (spec.Environment is not null)
        {
            startInfo.Environment.Clear();
            foreach (var (key, value) in spec.Environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start '{spec.FileName}'.");
        return new RunningProcess(process);
    }

    private sealed class RunningProcess : IRunningProcess
    {
        private readonly Process _process;
        private readonly Channel<string> _stdout = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleWriter = true });
        private readonly Channel<string> _stderr = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleWriter = true });
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly Task<int> _exited;

        public RunningProcess(Process process)
        {
            _process = process;
            _process.StandardInput.NewLine = "\n";
            _process.StandardInput.AutoFlush = false;
            var stdoutPump = PumpAsync(process.StandardOutput, _stdout.Writer);
            var stderrPump = PumpAsync(process.StandardError, _stderr.Writer);
            _exited = WaitForExitAsync(stdoutPump, stderrPump);
        }

        public int Id => _process.Id;

        public ChannelReader<string> StandardOutput => _stdout.Reader;

        public ChannelReader<string> StandardError => _stderr.Reader;

        public Task<int> Exited => _exited;

        public async ValueTask WriteLineAsync(string line, CancellationToken cancellationToken = default)
        {
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
                await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        public void CloseStandardInput()
        {
            try
            {
                _process.StandardInput.Close();
            }
            catch (IOException)
            {
                // The process already exited and closed the pipe.
            }
        }

        public void Kill()
        {
            try
            {
                if (!_process.HasExited)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Already exited.
            }
        }

        public async ValueTask DisposeAsync()
        {
            CloseStandardInput();
            Kill();
            try
            {
                await _exited.ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
                // Nothing left to wait for.
            }
            _process.Dispose();
            _writeLock.Dispose();
        }

        private static async Task PumpAsync(StreamReader reader, ChannelWriter<string> writer)
        {
            try
            {
                while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
                {
                    writer.TryWrite(line);
                }
            }
            catch (IOException)
            {
                // The pipe broke because the process ended.
            }
            finally
            {
                writer.TryComplete();
            }
        }

        private async Task<int> WaitForExitAsync(Task stdoutPump, Task stderrPump)
        {
            await _process.WaitForExitAsync().ConfigureAwait(false);
            await Task.WhenAll(stdoutPump, stderrPump).ConfigureAwait(false);
            return _process.ExitCode;
        }
    }
}
