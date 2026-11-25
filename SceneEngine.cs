using System.Text.RegularExpressions;

namespace WingScenes;

public record DcaAssignment(int Dca, string Key, string Name, List<int> Channels, List<string> Members, bool IsNew, int? PrevDca);
public record ScenePlan(List<DcaAssignment> Active, List<int> AbsentChannels, List<string> Warnings);

public class SceneEngine
{
    readonly ShowStore _store;
    readonly WingClient _wing;
    readonly SemaphoreSlim _busy = new(1, 1);
    readonly List<string> _log = new();
    DateTime _lastPress = DateTime.MinValue;

    public SceneEngine(ShowStore store, WingClient wing)
    {
        _store = store; _wing = wing;
        _wing.EventReceived += OnWingEvent;
    }

    // Zwischenspeicher des DCA-Zustands der Wing (Index 1..16), gepflegt über Subscription-Events und Auslesen
    readonly string?[] _dcaName = new string?[17];
    readonly bool?[] _dcaMuted = new bool?[17];

    public record DcaView(int Dca, string Name, bool? Muted, bool Absent);

    /// <summary>16 DCAs für die Anzeige. Verbunden: Zustand der Wing. Sonst: zuletzt gemerkte Belegung ohne Mute-Zustand.</summary>
    public List<DcaView> DcaSnapshot()
    {
        var show = _store.Show;
        var fallback = new Dictionary<int, string>();
        foreach (var (key, dca) in show.Assignment)
        {
            var id = key[(key.IndexOf(':') + 1)..];
            var name = key.StartsWith("group:") ? show.Groups.FirstOrDefault(g => g.Id == id)?.Name : show.Roles.FirstOrDefault(r => r.Id == id)?.Name;
            if (name != null) fallback[dca] = name;
        }
        var list = new List<DcaView>();
        for (int d = 1; d <= 16; d++)
        {
            string name; bool? muted;
            lock (_dcaName)
            {
                name = _wing.Connected ? _dcaName[d] ?? "" : fallback.GetValueOrDefault(d, "");
                muted = _wing.Connected ? _dcaMuted[d] : null;
            }
            list.Add(new DcaView(d, name, muted, d == show.Settings.AbsentDca));
        }
        return list;
    }

    /// <summary>Liest Namen und Mute-Zustand aller 16 DCAs von der Wing.</summary>
    public async Task RefreshDcasAsync()
    {
        for (int d = 1; d <= 16; d++)
        {
            try
            {
                var n = await _wing.GetAsync($"/dca/{d}/name", 500, 1);
                var m = await _wing.GetAsync($"/dca/{d}/mute", 500, 1);
                lock (_dcaName) { _dcaName[d] = n.Length > 0 ? n[0]?.ToString() ?? "" : ""; _dcaMuted[d] = ToBool(m); }
            }
            catch (TimeoutException) { }
        }
    }

    public IReadOnlyList<string> Log { get { lock (_log) return _log.TakeLast(60).ToList(); } }
    void AddLog(string s) { lock (_log) _log.Add($"{DateTime.Now:HH:mm:ss}  {s}"); Console.WriteLine(s); }

    /// <summary>Berechnet die DCA-Belegung einer Szene (ohne Wing-Zugriff).</summary>
    public static ScenePlan Plan(Show show, Scene scene)
    {
        var warnings = new List<string>();
        var prev = show.Assignment ?? new();
        var used = new HashSet<int>();
        int max = show.Settings.AbsentDca - 1;

        // 1) Szene -> Einträge (Rolle oder Gruppe) mit Kanälen
        var entries = new List<(string Key, string Name, List<int> Chans, List<string> Members)>();
        foreach (var item in scene.Items)
        {
            List<Role> roles; string name;
            if (item.Type == "group")
            {
                var g = show.Groups.FirstOrDefault(x => x.Id == item.Id);
                if (g == null) { warnings.Add("Gruppe nicht gefunden: " + item.Id); continue; }
                roles = g.RoleIds.Select(id => show.Roles.FirstOrDefault(r => r.Id == id)).Where(r => r != null).Select(r => r!).ToList();
                name = g.Name;
            }
            else
            {
                var r = show.Roles.FirstOrDefault(x => x.Id == item.Id);
                if (r == null) { warnings.Add("Rolle nicht gefunden: " + item.Id); continue; }
                roles = new() { r }; name = r.Name;
            }
            foreach (var r in roles.Where(r => r.Channel is < 1 or > 40)) warnings.Add($"Rolle \"{r.Name}\" hat keinen Kanal");
            var chans = roles.Where(r => r.Channel is >= 1 and <= 40).Select(r => r.Channel).Distinct().Where(c => used.Add(c)).ToList();
            if (chans.Count == 0) continue;
            entries.Add(($"{item.Type}:{item.Id}", name, chans, roles.Select(r => r.Name).ToList()));
        }

        // 2) DCA-Nummern vergeben
        var assigned = new Dictionary<string, int>();
        var taken = new HashSet<int>();
        if (show.Settings.StableDcas)
            foreach (var e in entries)
                if (prev.TryGetValue(e.Key, out var d) && d >= 1 && d <= max && taken.Add(d)) assigned[e.Key] = d;
        int next = 1;
        foreach (var e in entries)
        {
            if (assigned.ContainsKey(e.Key)) continue;
            while (next <= max && taken.Contains(next)) next++;
            if (next > max) { warnings.Add($"Zu viele Rollen/Gruppen in \"{scene.Name}\" (max. {max}): \"{e.Name}\" ohne DCA"); continue; }
            taken.Add(next); assigned[e.Key] = next;
        }

        var active = entries.Where(e => assigned.ContainsKey(e.Key))
            .Select(e => new DcaAssignment(assigned[e.Key], e.Key, e.Name, e.Chans, e.Members,
                IsNew: !prev.ContainsKey(e.Key), PrevDca: prev.TryGetValue(e.Key, out var pd) ? pd : null))
            .OrderBy(a => a.Dca).ToList();

        var activeChans = active.SelectMany(a => a.Channels).ToHashSet();
        var absent = show.Roles.Where(r => r.Channel is >= 1 and <= 40).Select(r => r.Channel).Distinct()
            .Where(c => !activeChans.Contains(c)).OrderBy(c => c).ToList();
        return new ScenePlan(active, absent, warnings);
    }

    static string Trunc(string s) => s.Length <= 16 ? s : s[..16];

    /// <summary>Tags setzen: vorhandene #D-Tags entfernen, neuen #Dn anhängen, andere Tags erhalten.</summary>
    public static string WithDcaTag(string current, int dca)
    {
        current ??= "";
        char sep = current.Contains(',') ? ',' : current.Contains(' ') ? ' ' : ',';
        var tokens = Regex.Split(current, @"[,\s]+").Where(t => t.Length > 0 && !Regex.IsMatch(t, @"^#D\d+$")).ToList();
        tokens.Add("#D" + dca);
        return string.Join(sep, tokens);
    }

    static bool? ToBool(object[] args) => args.LastOrDefault() switch
    {
        int i => i != 0,
        float f => f >= 0.5f,
        string s => s is "1" or "true" or "ON",
        _ => null
    };

    public async Task ApplySceneAsync(int index)
    {
        var show = _store.Show;
        if (index < 0 || index >= show.Scenes.Count) throw new ArgumentOutOfRangeException(nameof(index));
        await _busy.WaitAsync();
        try
        {
            var set = show.Settings;
            var scene = show.Scenes[index];
            var plan = Plan(show, scene);
            AddLog($"Szene {index + 1}: {scene.Name}");
            foreach (var w in plan.Warnings) AddLog("  Warnung: " + w);

            // Mute-Zustände der alten DCAs lesen, BEVOR irgendetwas überschrieben wird
            var carried = new Dictionary<string, bool>();
            foreach (var a in plan.Active.Where(a => !a.IsNew && a.PrevDca is int pd && pd != a.Dca && set.StayingRolesKeepMute))
            {
                try { var m = ToBool(await _wing.GetAsync($"/dca/{a.PrevDca}/mute")); if (m != null) carried[a.Key] = m.Value; }
                catch (TimeoutException) { AddLog($"  Warnung: Mute-Zustand von DCA {a.PrevDca} nicht lesbar ({a.Name})"); }
            }

            async Task SetTag(int ch, int dca)
            {
                string cur = "";
                try { var r = await _wing.GetAsync($"/ch/{ch}/tags"); cur = r.Length > 0 ? r[0]?.ToString() ?? "" : ""; } catch (TimeoutException) { }
                var next = WithDcaTag(cur, dca);
                if (next != cur) await _wing.SendAsync($"/ch/{ch}/tags", next);
            }

            foreach (var a in plan.Active)
            {
                foreach (var ch in a.Channels) await SetTag(ch, a.Dca);
                await _wing.SendAsync($"/dca/{a.Dca}/name", Trunc(a.Name));

                string mute;
                if (a.IsNew) { await _wing.SendAsync($"/dca/{a.Dca}/mute", set.NewRolesStartMuted ? 1 : 0); mute = set.NewRolesStartMuted ? "neu, stumm" : "neu, offen"; }
                else if (!set.StayingRolesKeepMute) { await _wing.SendAsync($"/dca/{a.Dca}/mute", 0); mute = "bleibt, geöffnet"; }
                else if (a.PrevDca != a.Dca)
                {
                    if (carried.TryGetValue(a.Key, out var m)) { await _wing.SendAsync($"/dca/{a.Dca}/mute", m ? 1 : 0); mute = m ? "bleibt, stumm übernommen" : "bleibt, offen übernommen"; }
                    else mute = "bleibt, Zustand unverändert";
                }
                else mute = "bleibt, Zustand unverändert";
                AddLog($"  DCA {a.Dca} {a.Name} ({mute})");
            }

            int absentDca = set.AbsentDca;
            var usedDcas = plan.Active.Select(a => a.Dca).ToHashSet();
            foreach (var ch in plan.AbsentChannels) await SetTag(ch, absentDca);
            for (int d = 1; d < absentDca; d++)
                if (!usedDcas.Contains(d)) await _wing.SendAsync($"/dca/{d}/name", "");
            await _wing.SendAsync($"/dca/{absentDca}/name", "ABWESEND");
            await _wing.SendAsync($"/dca/{absentDca}/mute", 1);

            await UpdateAutomixAsync(set.AutomixGroup, plan);

            show.Assignment = plan.Active.ToDictionary(a => a.Key, a => a.Dca);
            show.CurrentScene = index;
            _store.Replace(show);
            AddLog($"  {plan.AbsentChannels.Count} Kanäle auf DCA {absentDca} (abwesend, stumm)");
            await RefreshDcasAsync(); // Schreibzugriffe werden von der Wing nicht als Event zurückgemeldet
        }
        finally { _busy.Release(); }
    }

    /// <summary>
    /// Automix der Wing läuft über den Post-Insert des Kanals (Modus AUTO_X / AUTO_Y).
    /// Auftretende Kanäle werden der Gruppe hinzugefügt, abwesende Rollenkanäle entfernt.
    /// Ein Post-Insert, auf dem ein Effekt aktiv ist, wird nie überschrieben.
    /// </summary>
    async Task UpdateAutomixAsync(string group, ScenePlan plan)
    {
        if (group is not ("X" or "Y")) return;
        var target = "AUTO_" + group;
        await _wing.SendAsync($"/cfg/amix/{group.ToLowerInvariant()}", 1);

        async Task<(string Mode, bool On, string Insert)?> Read(int ch)
        {
            try
            {
                var mode = await _wing.GetAsync($"/ch/{ch}/postins/mode");
                var on = await _wing.GetAsync($"/ch/{ch}/postins/on");
                var ins = await _wing.GetAsync($"/ch/{ch}/postins/ins");
                return (mode.FirstOrDefault()?.ToString() ?? "", ToBool(on) == true, ins.FirstOrDefault()?.ToString() ?? "");
            }
            catch (TimeoutException) { AddLog($"  Warnung: Post-Insert von Kanal {ch} nicht lesbar, Automix unverändert"); return null; }
        }

        var added = new List<int>();
        foreach (var ch in plan.Active.SelectMany(a => a.Channels).OrderBy(c => c))
        {
            if (await Read(ch) is not { } s) continue;
            if (s.On && s.Mode == "FX" && s.Insert is not ("" or "NONE"))
            {
                AddLog($"  Warnung: Kanal {ch} nutzt den Post-Insert für {s.Insert}, nicht in den Automix aufgenommen");
                continue;
            }
            if (s.Mode != target) await _wing.SendAsync($"/ch/{ch}/postins/mode", target);
            if (!s.On || s.Mode != target) await _wing.SendAsync($"/ch/{ch}/postins/on", 1);
            added.Add(ch);
        }

        var removed = new List<int>();
        foreach (var ch in plan.AbsentChannels)
        {
            if (await Read(ch) is not { } s) continue;
            if (!s.Mode.StartsWith("AUTO_")) continue; // Effekt-Insert o.ä.: nicht anfassen
            if (s.On) await _wing.SendAsync($"/ch/{ch}/postins/on", 0);
            removed.Add(ch);
        }

        static string List(List<int> l) => l.Count == 0 ? "–" : string.Join(", ", l);
        AddLog($"  Automix {group}: Kanäle {List(added)} aktiv, {List(removed)} entfernt");
        if (added.Count > 16) AddLog($"  Warnung: {added.Count} Kanäle im Automix, die Wing sieht laut Doku 16 vor");
    }

    public async Task StepAsync(int delta)
    {
        var show = _store.Show;
        int next = show.CurrentScene + delta;
        if (show.CurrentScene < 0) next = delta > 0 ? 0 : show.Scenes.Count - 1;
        if (next < 0 || next >= show.Scenes.Count) { AddLog("Keine weitere Szene in dieser Richtung"); return; }
        await ApplySceneAsync(next);
    }

    /// <summary>Vergisst die gemerkte DCA-Belegung (z.B. nach Neustart des Pults): die nächste Szene gilt dann als Neuanfang.</summary>
    public void ResetAssignment()
    {
        _store.Show.Assignment = new();
        _store.Show.CurrentScene = -1;
        _store.Replace(_store.Show);
        AddLog("Gemerkte DCA-Belegung zurückgesetzt");
    }

    /// <summary>Konfiguriert die beiden Userbuttons so, dass sie je eine freie Mute-Gruppe umschalten.</summary>
    public async Task SetupButtonsAsync()
    {
        var s = _store.Show.Settings;
        async Task One(string prefix, int mg, string label)
        {
            await _wing.SendAsync(prefix + "/mode", "MGRP");
            await _wing.SendAsync(prefix + "/mgrp", $"MGRP.{mg}");
            await _wing.SendAsync(prefix + "/name", label);
            await _wing.SendAsync($"/mgrp/{mg}/mute", 0);
            await _wing.SendAsync($"/mgrp/{mg}/name", label);
        }
        await One(s.NextButton, s.NextMuteGroup, "SZENE +");
        await One(s.PrevButton, s.PrevMuteGroup, "SZENE -");
        AddLog($"Userbuttons {s.NextButton} / {s.PrevButton} auf Mute-Gruppen {s.NextMuteGroup}/{s.PrevMuteGroup} eingerichtet");
    }

    void OnWingEvent(string addr, object[] args)
    {
        var m = Regex.Match(addr, @"^/dca/(\d+)/(mute|name)$");
        if (m.Success && int.TryParse(m.Groups[1].Value, out var dn) && dn is >= 1 and <= 16)
        {
            lock (_dcaName)
            {
                if (m.Groups[2].Value == "mute") _dcaMuted[dn] = ToBool(args);
                else if (args.Length > 0) _dcaName[dn] = args[0]?.ToString() ?? "";
            }
            return;
        }
        var s = _store.Show.Settings;
        int dir; int mg;
        if (addr ==$"/mgrp/{s.NextMuteGroup}/mute") { dir = +1; mg = s.NextMuteGroup; }
        else if (addr == $"/mgrp/{s.PrevMuteGroup}/mute") { dir = -1; mg = s.PrevMuteGroup; }
        else return;
        // Wert 1 = Taster gedrückt (Mute-Gruppe aktiv); 0 = unser eigenes Zurücksetzen
        if (ToBool(args) != true) return;
        _ = Task.Run(async () =>
        {
            try
            {
                if ((DateTime.UtcNow - _lastPress).TotalMilliseconds > 250)
                {
                    _lastPress = DateTime.UtcNow;
                    AddLog(dir > 0 ? "Userbutton: nächste Szene" : "Userbutton: vorherige Szene");
                    await StepAsync(dir);
                }
            }
            catch (Exception e) { AddLog("Fehler: " + e.Message); }
            finally { try { await _wing.SendAsync($"/mgrp/{mg}/mute", 0); } catch { } }
        });
    }
}
