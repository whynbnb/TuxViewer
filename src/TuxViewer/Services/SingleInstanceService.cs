using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace TuxViewer.Services;

public sealed class SingleInstanceRequest
{
    public int Version { get; set; } = 1;
    public string[] Args { get; set; } = [];
    public string? Cwd { get; set; }
    public int Pid { get; set; }
}

/// <summary>
/// Routes GUI invocations to the already running instance through a Unix
/// domain socket in $XDG_RUNTIME_DIR. The first instance also holds a lock
/// file so two processes cannot both become the primary instance.
/// </summary>
public sealed class SingleInstanceService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    private static SingleInstanceService? _active;

    private readonly Socket _listener;
    private readonly FileStream _lockStream;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _acceptLoop;
    private readonly Lock _gate = new();
    private readonly List<SingleInstanceRequest> _pending = [];
    private Func<SingleInstanceRequest, Task>? _handler;
    private bool _disposed;

    public static SingleInstanceService? Active => _active;

    public static string RuntimeDirectory
    {
        get
        {
            var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (string.IsNullOrWhiteSpace(runtime))
                runtime = Path.Combine(Path.GetTempPath(), $"TuxViewer-{Environment.UserName}");
            return Path.Combine(runtime, "TuxViewer");
        }
    }

    public static string SocketPath => Path.Combine(RuntimeDirectory, "instance.sock");
    private static string LockPath => Path.Combine(RuntimeDirectory, "instance.lock");

    /// <summary>
    /// Acquire the primary instance. Returns null when <paramref name="forwarded"/>
    /// is true (the running instance accepted the request) or when single-instance
    /// mode is disabled. Throws only for unexpected setup failures.
    /// </summary>
    public static SingleInstanceService? Acquire(IReadOnlyList<string> args, out bool forwarded)
    {
        forwarded = false;
        if (args.Any(arg => string.Equals(arg, "--new-instance", StringComparison.OrdinalIgnoreCase)))
            return null;

        var lockStream = TryAcquireLock();
        if (lockStream is null)
        {
            // Another process may be starting up. Give it a moment, then forward.
            for (var attempt = 0; attempt < 25; attempt++)
            {
                if (TryForward(args, out _))
                {
                    forwarded = true;
                    return null;
                }
                Thread.Sleep(100);
            }

            lockStream = TryAcquireLock();
            if (lockStream is null)
            {
                AppLog.Warn("Single instance lock is held but the socket is not reachable; starting without IPC.");
                return null;
            }
        }

        var service = new SingleInstanceService(lockStream);
        _active = service;
        AppLog.Info($"Single instance IPC listening at {SocketPath}");
        return service;
    }

    private SingleInstanceService(FileStream lockStream)
    {
        _lockStream = lockStream;
        Directory.CreateDirectory(RuntimeDirectory);
        TrySetDirectoryMode();

        try
        {
            if (File.Exists(SocketPath)) File.Delete(SocketPath);
        }
        catch { /* the bind below reports the real error */ }

        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
        _listener.Listen(8);
        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(SocketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch { /* optional hardening */ }

        _acceptLoop = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public void SetHandler(Func<SingleInstanceRequest, Task> handler)
    {
        SingleInstanceRequest[] pending;
        lock (_gate)
        {
            _handler = handler;
            pending = _pending.ToArray();
            _pending.Clear();
        }

        foreach (var request in pending) _ = handler(request);
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _listener.AcceptAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                AppLog.Warn($"Single instance accept failed: {ex.Message}");
                break;
            }

            _ = HandleClientAsync(client, cancellationToken);
        }
    }

    private async Task HandleClientAsync(Socket client, CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                using var stream = new NetworkStream(client, ownsSocket: false);
                using var reader = new StreamReader(stream, new UTF8Encoding(false), false, 4096, leaveOpen: true);
                using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true)
                {
                    AutoFlush = true,
                };

                var line = await reader.ReadLineAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(line)) return;

                var request = JsonSerializer.Deserialize<SingleInstanceRequest>(line, JsonOptions);
                if (request is null || request.Version != 1)
                {
                    await writer.WriteLineAsync("{\"ok\":false,\"error\":\"protocol\"}");
                    return;
                }

                Dispatch(request);
                await writer.WriteLineAsync("{\"ok\":true}");
            }
            catch (Exception ex)
            {
                AppLog.Warn($"Single instance request failed: {ex.Message}");
            }
        }
    }

    private void Dispatch(SingleInstanceRequest request)
    {
        Func<SingleInstanceRequest, Task>? handler;
        lock (_gate)
        {
            handler = _handler;
            if (handler is null) _pending.Add(request);
        }

        if (handler is not null) _ = handler(request);
    }

    private static bool TryForward(IReadOnlyList<string> args, out string? error)
    {
        error = null;
        if (!File.Exists(SocketPath)) return false;

        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            socket.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath), timeout.Token)
                .AsTask().GetAwaiter().GetResult();

            using var stream = new NetworkStream(socket, ownsSocket: false);
            using var reader = new StreamReader(stream, new UTF8Encoding(false), false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true)
            {
                AutoFlush = true,
            };

            var request = new SingleInstanceRequest
            {
                Args = args.ToArray(),
                Cwd = Environment.CurrentDirectory,
                Pid = Environment.ProcessId,
            };
            writer.WriteLine(JsonSerializer.Serialize(request, JsonOptions));
            var reply = reader.ReadLine();
            if (reply is not null && reply.Contains("\"ok\":true", StringComparison.Ordinal))
            {
                AppLog.Info("Forwarded GUI request to the running TuxViewer instance.");
                return true;
            }

            error = reply ?? "no reply";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static FileStream? TryAcquireLock()
    {
        try
        {
            Directory.CreateDirectory(RuntimeDirectory);
            TrySetDirectoryMode();
            return new FileStream(
                LockPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
        }
        catch
        {
            return null;
        }
    }

    private static void TrySetDirectoryMode()
    {
        try
        {
            if (OperatingSystem.IsWindows()) return;
            File.SetUnixFileMode(
                RuntimeDirectory,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch { /* not supported on every filesystem */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        try { _listener.Dispose(); } catch { /* ignore */ }
        try { _acceptLoop.Wait(TimeSpan.FromSeconds(1)); } catch { /* ignore */ }
        try { if (File.Exists(SocketPath)) File.Delete(SocketPath); } catch { /* ignore */ }
        _lockStream.Dispose();
        _cts.Dispose();
        if (ReferenceEquals(_active, this)) _active = null;
    }
}
