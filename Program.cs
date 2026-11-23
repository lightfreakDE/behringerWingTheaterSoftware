using WingScenes;

if (args.Contains("--sim"))
{
    // Wing-Simulator: dotnet run -- --sim
    await WingSimulator.RunStandaloneAsync();
    return;
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = AppContext.BaseDirectory });
if (builder.Configuration["urls"] is null && Environment.GetEnvironmentVariable("ASPNETCORE_URLS") is null)
    builder.WebHost.UseUrls("http://0.0.0.0:5080"); // auch vom Tablet/Handy erreichbar

var dataFile = Environment.GetEnvironmentVariable("WINGSCENES_DATA") ?? Path.Combine(builder.Environment.ContentRootPath, "show.json");
var store = new ShowStore(dataFile);
var wing = new WingClient();
var engine = new SceneEngine(store, wing);
var sim = new WingSimulator(); // eingebauter Wing-Ersatz zum Testen ohne Pult

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/show", () => store.Show);
app.MapPut("/api/show", (Show show) =>
{
    // Laufzeit-Zustand (aktuelle Szene, DCA-Belegung) bleibt serverseitig maßgeblich
    show.CurrentScene = Math.Clamp(store.Show.CurrentScene, -1, show.Scenes.Count - 1);
    show.Assignment = store.Show.Assignment;
    show.Settings.AbsentDca = Math.Clamp(show.Settings.AbsentDca, 2, 16);
    store.Replace(show);
    return Results.Ok();
});

app.MapGet("/api/status", () => new
{
    connected = wing.Connected,
    info = wing.Info,
    current = store.Show.CurrentScene,
    simulator = sim.Running,
    dcas = engine.DcaSnapshot(),
    log = engine.Log
});

app.MapGet("/api/plan/{index:int}", (int index) =>
    index >= 0 && index < store.Show.Scenes.Count
        ? Results.Ok(SceneEngine.Plan(store.Show, store.Show.Scenes[index]))
        : Results.NotFound());

app.MapPost("/api/connect", async (ConnectRequest req) =>
{
    try
    {
        await wing.ConnectAsync(req.Ip.Trim());
        await engine.RefreshDcasAsync();
        store.Show.Settings.WingIp = req.Ip.Trim();
        store.Replace(store.Show);
        return Results.Ok(new { info = wing.Info });
    }
    catch (Exception e) { return Results.Problem(e.Message, statusCode: 502); }
});
// Testmodus: Simulator starten und sich sofort damit verbinden
app.MapPost("/api/sim/start", async () =>
{
    try
    {
        int simPort = int.TryParse(Environment.GetEnvironmentVariable("WINGSCENES_SIM_PORT"), out var sp) ? sp : WingClient.Port;
        sim.Start(simPort);
        await wing.ConnectAsync("127.0.0.1", simPort);
        await engine.RefreshDcasAsync();
        return Results.Ok(new { info = wing.Info });
    }
    catch (System.Net.Sockets.SocketException)
    {
        return Results.Problem("Port 2223 ist belegt (läuft schon ein Simulator oder eine andere Wing-Software?)", statusCode: 409);
    }
    catch (Exception e) { sim.Stop(); return Results.Problem(e.Message, statusCode: 502); }
});
app.MapPost("/api/sim/stop", () => { wing.Disconnect(); sim.Stop(); return Results.Ok(); });
app.MapPost("/api/sim/press/{dir}", async (string dir) =>
{
    int mg = dir == "next" ? store.Show.Settings.NextMuteGroup : store.Show.Settings.PrevMuteGroup;
    return await sim.PressAsync(mg) ? Results.Ok() : Results.Problem("Simulator läuft nicht oder ist nicht verbunden", statusCode: 409);
});
app.MapPost("/api/disconnect", () => { wing.Disconnect(); return Results.Ok(); });

app.MapPost("/api/scene/{index:int}/apply", async (int index) =>
{
    try { await engine.ApplySceneAsync(index); return Results.Ok(); }
    catch (Exception e) { return Results.Problem(e.Message, statusCode: 502); }
});
app.MapPost("/api/next", async () => { try { await engine.StepAsync(+1); return Results.Ok(); } catch (Exception e) { return Results.Problem(e.Message, statusCode: 502); } });
app.MapPost("/api/prev", async () => { try { await engine.StepAsync(-1); return Results.Ok(); } catch (Exception e) { return Results.Problem(e.Message, statusCode: 502); } });
app.MapPost("/api/reset", () => { engine.ResetAssignment(); return Results.Ok(); });
app.MapPost("/api/buttons/setup", async () => { try { await engine.SetupButtonsAsync(); return Results.Ok(); } catch (Exception e) { return Results.Problem(e.Message, statusCode: 502); } });

var url = "http://localhost:5080";
Console.WriteLine($"Wing Szenen läuft: {url}  (Fenster offen lassen, Strg+C beendet)");
try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
try { app.Run(); }
catch (Exception e) { Console.WriteLine("FEHLER: " + e.Message); Console.WriteLine("Taste drücken zum Beenden …"); Console.ReadKey(); }

record ConnectRequest(string Ip);
