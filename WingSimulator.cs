using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace WingScenes;

/// <summary>Sehr einfacher Wing-Ersatz zum Testen ohne Pult: Get/Set auf beliebige Knoten, /*S-Subscription.</summary>
public sealed class WingSimulator : IDisposable
{
    readonly ConcurrentDictionary<string, object> _values = new();
    UdpClient? _udp;
    CancellationTokenSource? _cts;
    IPEndPoint? _subscriber;
    DateTime _subUntil = DateTime.MinValue;

    public bool Running => _udp != null;
    public int Port { get; private set; }

    public void Start(int port = WingClient.Port)
    {
        if (Running) return;
        _udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port)); // wirft SocketException, wenn der Port belegt ist
        Port = port;
        _cts = new CancellationTokenSource();
        var udp = _udp; var ct = _cts.Token;
        _ = Task.Run(() => Loop(udp, ct));
        Console.WriteLine($"[SIM] Wing-Simulator auf 127.0.0.1:{port}");
    }

    public void Stop()
    {
        _cts?.Cancel();
        _udp?.Dispose();
        _udp = null; _cts = null; _subscriber = null;
        _values.Clear();
        Console.WriteLine("[SIM] Wing-Simulator gestoppt");
    }

    /// <summary>Simuliert einen Userbutton-Druck (schaltet die zugeordnete Mute-Gruppe ein, wie die Wing es melden würde).</summary>
    public async Task<bool> PressAsync(int muteGroup)
    {
        var udp = _udp;
        if (udp == null || _subscriber == null || DateTime.UtcNow >= _subUntil) return false;
        _values[$"/mgrp/{muteGroup}/mute"] = 1;
        var b = Osc.Encode($"/mgrp/{muteGroup}/mute", 1);
        await udp.SendAsync(b, b.Length, _subscriber);
        Console.WriteLine($"[SIM] Userbutton gedrückt (Mute-Gruppe {muteGroup})");
        return true;
    }

    async Task Loop(UdpClient udp, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var r = await udp.ReceiveAsync(ct);
                var msg = Osc.Decode(r.Buffer);
                if (msg is null) continue;
                var (addr, args) = msg.Value;
                if (addr == "/?")
                {
                    var b = Osc.Encode("/?", "WING,127.0.0.1,PGM,ngc-full,SIM,0.0.0-sim");
                    await udp.SendAsync(b, b.Length, r.RemoteEndPoint);
                }
                else if (addr == "/*S") { _subscriber = r.RemoteEndPoint; _subUntil = DateTime.UtcNow.AddSeconds(10); }
                else if (args.Length == 0)
                {
                    object v = _values.TryGetValue(addr, out var x) ? x : (addr.EndsWith("/tags") || addr.EndsWith("/name") ? "" : 0);
                    var b = Osc.Encode(addr, v);
                    await udp.SendAsync(b, b.Length, r.RemoteEndPoint);
                }
                else
                {
                    _values[addr] = args[0];
                    Console.WriteLine($"[SIM] {addr} = {args[0]}  (von Port {r.RemoteEndPoint.Port})");
                    var sub = _subscriber;
                    if (sub != null && DateTime.UtcNow < _subUntil && !r.RemoteEndPoint.Equals(sub))
                    {
                        var b = Osc.Encode(addr, args[0]);
                        await udp.SendAsync(b, b.Length, sub);
                    }
                }
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { }
        }
    }

    public void Dispose() => Stop();

    /// <summary>Eigenständiger Betrieb (exe --sim): Konsole, n/p + Enter simuliert die Userbuttons.</summary>
    public static async Task RunStandaloneAsync()
    {
        using var sim = new WingSimulator();
        sim.Start();
        Console.WriteLine("[SIM] Eingabe + Enter:  n = Userbutton 'nächste Szene',  p = Userbutton 'vorherige Szene'");
        while (true)
        {
            var line = (await Task.Run(Console.ReadLine))?.Trim().ToLowerInvariant();
            if (line is null) { await Task.Delay(Timeout.Infinite); return; }
            int mg = line == "n" ? 7 : line == "p" ? 8 : 0; // Standard-Mute-Gruppen der Userbuttons
            if (mg != 0 && !await sim.PressAsync(mg)) Console.WriteLine("[SIM] Kein Tool verbunden/abonniert");
        }
    }
}
