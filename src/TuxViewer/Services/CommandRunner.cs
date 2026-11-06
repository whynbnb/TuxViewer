using System.Diagnostics;
using System.Text;

namespace TuxViewer.Services;

public sealed record CommandResult(int ExitCode, string StdOut, string StdErr)
{
    public bool Success => ExitCode == 0;
}

public static class CommandRunner
{
    private static readonly Dictionary<string, string?> ExeCache = new(StringComparer.Ordinal);
    private static readonly Lock CacheLock = new();

    public static bool Exists(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) return false;
        if (executable.Contains('/')) return File.Exists(executable);

        lock (CacheLock)
        {
            if (ExeCache.TryGetValue(executable, out var cached)) return cached is not null;
        }

        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        string? found = null;
        foreach (var dir in path.Split(':', StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, executable);
            if (File.Exists(candidate))
            {
                found = candidate;
                break;
            }
        }

        lock (CacheLock) ExeCache[executable] = found;
        return found is not null;
    }

    public static async Task<CommandResult?> TryRunAsync(
        string executable,
        IReadOnlyList<string> args,
        int timeoutMs = 4000,
        CancellationToken cancellationToken = default,
        string? stdin = null)
    {
        if (!Exists(executable)) return null;

        var psi = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = stdin is not null,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        try
        {
            using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            if (!process.Start()) return new CommandResult(-1, "", $"无法启动 {executable}");

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            if (stdin is not null)
            {
                await process.StandardInput.WriteAsync(stdin);
                await process.StandardInput.FlushAsync(cancellationToken);
                process.StandardInput.Close();
            }

            using var timeoutCts = new CancellationTokenSource(timeoutMs);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);
            try
            {
                await process.WaitForExitAsync(linked.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited) process.Kill(entireProcessTree: true);
                }
                catch { /* best effort */ }

                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(cancellationToken);

                return new CommandResult(-2, "", $"命令超时（{timeoutMs} ms）：{executable}");
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            return new CommandResult(process.ExitCode, stdout.Trim(), stderr.Trim());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new CommandResult(-1, "", ex.Message);
        }
    }

    public static async Task<CommandResult?> TryRunAsync(
        string executable,
        params string[] args)
        => await TryRunAsync(executable, args, 4000, CancellationToken.None);

    public static async Task<string?> ReadLinkAsync(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists && info.LinkTarget is null) return null;
            return info.LinkTarget;
        }
        catch
        {
            return null;
        }
    }
}
