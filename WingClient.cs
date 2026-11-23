using System.Collections.Concurrent;
using System.Net.Sockets;

namespace WingScenes;

/// <summary>OSC-Verbindung zur Behringer Wing (UDP 2223), inkl. Subscription-Renew alle 5 s.</summary>
public sealed class WingClient : IDisposable
{
    public const int Port = 2223;
    UdpClient? _udp;
    CancellationTokenSource? _cts;
    readonly ConcurrentDictionary<string, TaskCompletionSource<object[]>> _pending = new();

    public bool Connected => _udp != null;
    public string Info { get; private set; } = "";
    /// <summary>Wird für jedes Event (Subscription) aufgerufen.</summary>
    public event Action<string, object[]>? EventReceived;

    public async Task ConnectAsync(string host, int port = Port)
    {
        Disconnect();
        var udp = new UdpClient();
        udp.Connect(host, port);
        _udp = udp;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _ = Task.Run(() => ReceiveLoop(udp, token));
        try
        {
            var reply = await GetAsync("/?", 1500, 2);
            Info = reply.Length > 0 ? reply[0]?.ToString() ?? "" : "";
        }
        catch { Disconnect(); throw new IOException($"Keine Antwort von {host}:{port}"); }
        await SendAsync("/*S");
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try { await Task.Delay(5000, token); await SendAsync("/*S"); } catch { }
            }
        });
    }

    public void Disconnect()
    {
        _cts?.Cancel();
        _udp?.Dispose();
        _udp = null; _cts = null; Info = "";
    }

    async Task ReceiveLoop(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var r = await udp.ReceiveAsync(ct);
                var msg = Osc.Decode(r.Buffer);
                if (msg is null) continue;
                var (addr, args) = msg.Value;
                if (_pending.TryRemove(addr, out var tcs)) tcs.TrySetResult(args);
                try { EventReceived?.Invoke(addr, args); } catch { }
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { /* z.B. ICMP port unreachable: weiter */ }
        }
    }

    public Task SendAsync(string address, params object[] args)
    {
        var udp = _udp ?? throw new InvalidOperationException("Nicht mit der Wing verbunden");
        var data = Osc.Encode(address, args);
        return udp.SendAsync(data, data.Length);
    }

    public async Task<object[]> GetAsync(string address, int timeoutMs = 800, int retries = 2)
    {
        for (int attempt = 0; ; attempt++)
        {
            var tcs = new TaskCompletionSource<object[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[address] = tcs;
            await SendAsync(address);
            var done = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
            if (done == tcs.Task) return tcs.Task.Result;
            _pending.TryRemove(address, out _);
            if (attempt >= retries) throw new TimeoutException($"Wing antwortet nicht auf {address}");
        }
    }

    public void Dispose() => Disconnect();
}
