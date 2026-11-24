using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;

namespace Noch.Core;

/// Listens for noch-hook.exe on a pipe that only the current Windows user can open.
public sealed class HookServer(AgentHub hub, string? pipeName = null) : IDisposable
{
    static readonly HashSet<string> Shells = new(StringComparer.OrdinalIgnoreCase) { "bash", "sh", "cmd", "powershell", "pwsh", "conhost", "env" };
    const int MaxPayload = 32 * 1024 * 1024;
    readonly CancellationTokenSource _stop = new();
    readonly string _name = pipeName ?? DefaultPipe;
    public static string DefaultPipe => "noch-" + WindowsIdentity.GetCurrent().User!.Value;

    public void Start() { for (var i = 0; i < 4; i++) _ = AcceptLoop(); }

    async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(_name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                _ = Serve(pipe);
            }
            catch (OperationCanceledException) { pipe?.Dispose(); return; }
            catch { pipe?.Dispose(); try { await Task.Delay(200, _stop.Token).ConfigureAwait(false); } catch { return; } }
        }
    }

    async Task Serve(NamedPipeServerStream pipe)
    {
        await using var _ = pipe;
        try
        {
            using var gone = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            var header = Encoding.UTF8.GetString(await ReadBlock(pipe, 4096, gone.Token).ConfigureAwait(false)).Split('\n');
            var body = await ReadBlock(pipe, MaxPayload, gone.Token).ConfigureAwait(false);
            var answer = "";
            if (header.Length == 3 && Parse(body) is { } json)
            {
                var chain = header[2].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split(':')).Select(p => (Pid: int.Parse(p[0]), Name: p[1])).ToList();
                var agent = chain.FirstOrDefault(p => !Shells.Contains(Path.GetFileNameWithoutExtension(p.Name)));
                var work = hub.HandleAsync(new HookEvent(header[0], long.Parse(header[1]), [.. chain.Select(p => p.Pid)], json, agent.Pid), gone.Token);
                if (!work.IsCompleted)
                {
                    // The client never sends more, so a finished read means it has gone away.
                    var watch = pipe.ReadAsync(new byte[1], gone.Token).AsTask();
                    if (await Task.WhenAny(work, watch).ConfigureAwait(false) == watch) gone.Cancel();
                }
                answer = await work.ConfigureAwait(false);
            }
            if (!pipe.IsConnected) return;
            var bytes = Encoding.UTF8.GetBytes(answer);
            await pipe.WriteAsync(BitConverter.GetBytes(bytes.Length)).ConfigureAwait(false);
            await pipe.WriteAsync(bytes).ConfigureAwait(false);
            await pipe.FlushAsync().ConfigureAwait(false);
            // Let the client read before the pipe closes under it.
            pipe.WaitForPipeDrain();
        }
        catch { /* a broken client must never take the notch down */ }
    }

    static JsonObject? Parse(byte[] body)
    {
        try { return JsonNode.Parse(body.AsSpan(body is [0xEF, 0xBB, 0xBF, ..] ? 3 : 0)) as JsonObject; } catch { return null; }
    }

    static async Task<byte[]> ReadBlock(Stream s, int max, CancellationToken ct)
    {
        var len = new byte[4];
        await s.ReadExactlyAsync(len, ct).ConfigureAwait(false);
        var n = BitConverter.ToInt32(len);
        if (n < 0 || n > max) throw new InvalidDataException();
        var buf = new byte[n];
        await s.ReadExactlyAsync(buf, ct).ConfigureAwait(false);
        return buf;
    }

    public void Dispose() => _stop.Cancel();
}
