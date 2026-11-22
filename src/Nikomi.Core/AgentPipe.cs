using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Win32.SafeHandles;

namespace Nikomi.Core;

/// <summary>
/// How the console program talks to the running app: a named pipe whose access list holds the same
/// Windows user alone. Both ends check the other: the console program that the pipe's owner is its own
/// user, the app that the pipe is its own and that each client runs as its user on this PC. Another
/// account can still take the name first, which keeps agents from reaching Nikomi but lets it read
/// nothing. One question and one answer per connection, each a single line of JSON. No network port is opened.
/// </summary>
public static class AgentPipe
{
    public const int MaxRequestBytes = 64 * 1024;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Name(string dataDir) => "Nikomi.Agent." + DataFolder.InstanceId(dataDir);

    /// <summary>
    /// Asks the running app. Null when no Nikomi is listening (it is not running, or agent access is
    /// off); an "error" answer when it is there but the exchange failed. <paramref name="cancel"/> gives up
    /// on the answer and closes the connection: it throws <see cref="OperationCanceledException"/>.
    /// </summary>
    public static async Task<JsonObject?> TryAskAsync(string pipeName, AgentRequest request, TimeSpan timeout, CancellationToken cancel = default)
    {
        // CurrentUserOnly also checks that the pipe's owner is this user, so another account cannot
        // stand in for Nikomi. Identification: whoever is at the other end may learn who we are, but
        // can never act as us, even if that check were ever bypassed.
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, TokenImpersonationLevel.Identification);
        try
        {
            await pipe.ConnectAsync(250, cancel);
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        cts.CancelAfter(timeout);
        try
        {
            byte[] line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request, Json) + "\n");
            await pipe.WriteAsync(line, cts.Token);
            await pipe.FlushAsync(cts.Token);
            string? answer = await ReadLineAsync(pipe, 8 << 20, cts.Token);
            return answer != null && JsonNode.Parse(answer) is JsonObject o ? o : AgentQueries.Error("Nikomi closed the connection without an answer.");
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            return AgentQueries.Error("Nikomi did not answer in time.");
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return AgentQueries.Error("Talking to Nikomi failed: " + ex.Message);
        }
    }

    /// <summary>Reads up to a line feed. Null when the other side closed first or sent more than allowed.</summary>
    internal static async Task<string?> ReadLineAsync(Stream stream, int maxBytes, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            int n = await stream.ReadAsync(chunk, ct);
            if (n == 0) return null;
            int end = Array.IndexOf(chunk, (byte)'\n', 0, n);
            buffer.Write(chunk, 0, end >= 0 ? end : n);
            if (end >= 0) return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
            if (buffer.Length > maxBytes) return null;
        }
    }
}

/// <summary>The app's end of the pipe. Exists only while the user has agent access turned on.</summary>
public sealed class AgentPipeServer : IDisposable
{
    private const int MaxConnections = 8;
    private const int BufferBytes = 64 * 1024;
    /// <summary>How long a client may take to send its question, and again to take its answer.</summary>
    private static readonly TimeSpan IoTimeout = TimeSpan.FromSeconds(5);

    private readonly string _name;
    private readonly Func<AgentRequest, Task<JsonObject>> _handler;
    private readonly CancellationTokenSource _stop = new();
    private int _active;
    private bool _squatLogged;
    private long _refusedLoggedAt = -60_000;   // Environment.TickCount64

    public AgentPipeServer(string pipeName, Func<AgentRequest, Task<JsonObject>> handler)
    {
        _name = pipeName;
        _handler = handler;
    }

    public void Start() => _ = Task.Run(AcceptLoopAsync);

    private async Task AcceptLoopAsync()
    {
        var ct = _stop.Token;
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                // CurrentUserOnly: the pipe's access list holds this user alone.
                pipe = new NamedPipeServerStream(_name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, BufferBytes, BufferBytes);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Write("Agent pipe could not be opened: " + ex.Message);
                try { await Task.Delay(10_000, ct); } catch (OperationCanceledException) { }
                continue;
            }
            // If another account created a pipe of this name first, Windows adds this instance to that
            // pipe, whose access list is the other account's. Such a pipe is not served.
            if (!IsOwnedByCurrentUser(pipe))
            {
                pipe.Dispose();
                if (!_squatLogged)
                    Log.Write("Agent pipe not opened: another Windows account holds its name. Agents cannot reach Nikomi until that ends.");
                _squatLogged = true;
                try { await Task.Delay(10_000, ct); } catch (OperationCanceledException) { }
                continue;
            }
            _squatLogged = false;
            try
            {
                await pipe.WaitForConnectionAsync(ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
                pipe.Dispose();
                continue;
            }
            _ = ServeAsync(pipe, ct);
        }
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken stop)
    {
        using var _ = pipe;
        if (Interlocked.Increment(ref _active) > MaxConnections) { Interlocked.Decrement(ref _active); return; }
        try
        {
            JsonObject answer;
            if (!IsLocalClient(pipe)) answer = AgentQueries.Error("Only programs on this PC may connect.");
            else if (!IsCurrentUserClient(pipe))
            {
                // The access list should already keep other accounts out; this holds even when it does not.
                answer = AgentQueries.Error("Only programs of the Windows user running Nikomi may connect.");
                // Logged at most once a minute, so a client in a loop cannot fill the log.
                long now = Environment.TickCount64, last = Interlocked.Read(ref _refusedLoggedAt);
                if (now - last >= 60_000 && Interlocked.CompareExchange(ref _refusedLoggedAt, now, last) == last)
                    Log.Write("Agent pipe: refused a connection from a program of another Windows account");
            }
            else
            {
                string? line;
                using (var read = CancellationTokenSource.CreateLinkedTokenSource(stop))
                {
                    read.CancelAfter(IoTimeout);
                    line = await AgentPipe.ReadLineAsync(pipe, AgentPipe.MaxRequestBytes, read.Token);
                }
                AgentRequest? request = null;
                if (line != null)
                    try { request = JsonSerializer.Deserialize<AgentRequest>(line, AgentPipe.Json); } catch (JsonException) { }
                answer = request == null ? AgentQueries.Error("The request could not be read.") : await _handler(request);
            }
            // A client that does not read its answer must not hold the connection open.
            using var write = CancellationTokenSource.CreateLinkedTokenSource(stop);
            write.CancelAfter(IoTimeout);
            await pipe.WriteAsync(Encoding.UTF8.GetBytes(answer.ToJsonString() + "\n"), write.Token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
            // The client went away or the app is closing: nothing to answer.
        }
        catch (Exception ex)
        {
            Log.Write("Agent request failed: " + ex);
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    /// <summary>
    /// A named pipe can also be reached from another PC by the same user. Windows names the client's
    /// computer for such a connection and reports "the pipe is local" otherwise; anything else is refused.
    /// </summary>
    private static bool IsLocalClient(NamedPipeServerStream pipe)
    {
        var name = new char[256];
        if (GetNamedPipeClientComputerName(pipe.SafePipeHandle, name, (uint)name.Length * sizeof(char))) return false;
        return Marshal.GetLastWin32Error() == ErrorPipeLocal;
    }

    private const int ErrorPipeLocal = 229;

    /// <summary>
    /// The pipe's access list lets only this user in, but only when Nikomi created the pipe. The program
    /// at the other end must run as the same Windows user as Nikomi; one that cannot be checked is refused.
    /// </summary>
    private static bool IsCurrentUserClient(NamedPipeServerStream pipe) =>
        GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint pid) && ProcessUser.IsCurrentUser((int)pid);

    /// <summary>
    /// True when this instance belongs to a pipe Nikomi's own account created. .NET makes the creator
    /// the owner (<see cref="PipeOptions.CurrentUserOnly"/>); a pipe another account made first keeps that
    /// account as its owner, and a standard user cannot make someone else the owner.
    /// </summary>
    private static bool IsOwnedByCurrentUser(NamedPipeServerStream pipe)
    {
        try
        {
            var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier));
            using var me = WindowsIdentity.GetCurrent();
            return owner != null && (owner.Equals(me.Owner) || owner.Equals(me.User));
        }
        catch (SystemException)   // access denied, a closed handle, or no owner to read
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetNamedPipeClientComputerNameW")]
    private static extern bool GetNamedPipeClientComputerName(SafePipeHandle pipe, [Out] char[] clientComputerName, uint clientComputerNameLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint clientProcessId);

    /// <summary>Stops listening: the waiting pipe is closed, so the name is gone and clients find nothing.</summary>
    public void Dispose() => _stop.Cancel();
}
