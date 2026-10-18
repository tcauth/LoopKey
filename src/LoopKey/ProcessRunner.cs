using System.Diagnostics;
using System.Text;

namespace LoopKey;

internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public string CombinedOutput => $"{StandardOutput}\n{StandardError}".Trim();
}

internal static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string?>? environment,
        CancellationToken cancellationToken)
    {
        using var process = Create(fileName, arguments, environment);
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                stdout.AppendLine(eventArgs.Data);
            }
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                stderr.AppendLine(eventArgs.Data);
            }
        };

        if (!process.Start())
        {
            throw new CliException($"Failed to start {fileName}.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);
        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    public static ManagedProcess Start(
        string fileName,
        IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string?>? environment = null) =>
        new(Create(fileName, arguments, environment));

    public static ManagedProcess StartRaw(
        string fileName,
        string arguments,
        IReadOnlyDictionary<string, string?>? environment = null) =>
        new(CreateRaw(fileName, arguments, environment));

    public static async Task<ProcessResult> RunRawAsync(
        string fileName,
        string arguments,
        IReadOnlyDictionary<string, string?>? environment,
        CancellationToken cancellationToken)
    {
        using var process = CreateRaw(fileName, arguments, environment);
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                stdout.AppendLine(eventArgs.Data);
            }
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                stderr.AppendLine(eventArgs.Data);
            }
        };

        if (!process.Start())
        {
            throw new CliException($"Failed to start {fileName}.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);
        return new ProcessResult(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    private static Process Create(
        string fileName,
        IEnumerable<string> arguments,
        IReadOnlyDictionary<string, string?>? environment)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        return new Process { StartInfo = startInfo, EnableRaisingEvents = true };
    }

    private static Process CreateRaw(
        string fileName,
        string arguments,
        IReadOnlyDictionary<string, string?>? environment)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        if (environment is not null)
        {
            foreach (var pair in environment)
            {
                startInfo.Environment[pair.Key] = pair.Value;
            }
        }

        return new Process { StartInfo = startInfo, EnableRaisingEvents = true };
    }
}

internal sealed class ManagedProcess : IAsyncDisposable
{
    private readonly Process process;
    private readonly StringBuilder output = new();
    private readonly TaskCompletionSource<int> exit =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ManagedProcess(Process process)
    {
        this.process = process;
        process.OutputDataReceived += OnOutput;
        process.ErrorDataReceived += OnOutput;
        process.Exited += (_, _) => exit.TrySetResult(process.ExitCode);
    }

    public int Id => process.Id;
    public string Output
    {
        get
        {
            lock (output)
            {
                return output.ToString();
            }
        }
    }
    public bool HasExited => process.HasExited;

    public void Start()
    {
        if (!process.Start())
        {
            throw new CliException($"Failed to start {process.StartInfo.FileName}.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    public async Task WaitForOutputAsync(
        Func<string, bool> predicate,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        while (!timeoutSource.IsCancellationRequested)
        {
            if (predicate(Output))
            {
                return;
            }

            if (HasExited)
            {
                throw new CliException(
                    $"{process.StartInfo.FileName} exited before becoming ready.\n{Output.Trim()}");
            }

            await Task.Delay(100, timeoutSource.Token);
        }

        throw new CliException($"{process.StartInfo.FileName} did not become ready before the timeout.");
    }

    public Task<int> WaitForExitAsync(CancellationToken cancellationToken) =>
        exit.Task.WaitAsync(cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }

        process.Dispose();
    }

    private void OnOutput(object sender, DataReceivedEventArgs eventArgs)
    {
        if (eventArgs.Data is not null)
        {
            lock (output)
            {
                output.AppendLine(eventArgs.Data);
            }
        }
    }
}
