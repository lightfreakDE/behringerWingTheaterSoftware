namespace WingScenes;

public class Actor { public string Id { get; set; } = ""; public string Name { get; set; } = ""; }

public class Role
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? ActorId { get; set; }
    /// <summary>Wing-Eingangskanal (1..40), 0 = nicht zugewiesen.</summary>
    public int Channel { get; set; }
}

public class Group { public string Id { get; set; } = ""; public string Name { get; set; } = ""; public List<string> RoleIds { get; set; } = new(); }

public class SceneItem { public string Type { get; set; } = "role"; public string Id { get; set; } = ""; }

public class Scene { public string Id { get; set; } = ""; public string Name { get; set; } = ""; public List<SceneItem> Items { get; set; } = new(); }

public class Settings
{
    public string WingIp { get; set; } = "";
    /// <summary>Userbutton für "nächste Szene" (OSC-Pfad bis inkl. /bu bzw. /bd).</summary>
    public string NextButton { get; set; } = "/$ctl/user/1/1/bu";
    public string PrevButton { get; set; } = "/$ctl/user/1/2/bu";
    /// <summary>Freie Mute-Gruppen (1..8), die als Taster-Signal dienen. Dürfen KEINE Kanäle enthalten.</summary>
    public int NextMuteGroup { get; set; } = 7;
    public int PrevMuteGroup { get; set; } = 8;
    public int AbsentDca { get; set; } = 16;
    /// <summary>An: wer schon in der vorherigen Szene dabei war, behält seine DCA. Aus: DCAs werden je Szene neu von 1 an vergeben.</summary>
    public bool StableDcas { get; set; } = true;
    /// <summary>An: neu auftretende Rollen starten stumm. Aus: sie sind sofort offen.</summary>
    public bool NewRolesStartMuted { get; set; } = true;
    /// <summary>An: Rollen, die bleiben, behalten ihren Mute-Zustand. Aus: sie werden beim Szenenwechsel geöffnet.</summary>
    public bool StayingRolesKeepMute { get; set; } = true;
}

public class Show
{
    public List<Actor> Actors { get; set; } = new();
    public List<Role> Roles { get; set; } = new();
    public List<Group> Groups { get; set; } = new();
    public List<Scene> Scenes { get; set; } = new();
    public Settings Settings { get; set; } = new();
    public int CurrentScene { get; set; } = -1;
    /// <summary>Aktuelle Belegung der Wing: Rolle/Gruppe ("role:id" / "group:id") -> DCA-Nummer.</summary>
    public Dictionary<string, int> Assignment { get; set; } = new();
}
