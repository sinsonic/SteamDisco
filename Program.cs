// SteamNewApps - daily Steam watcher.
//
// 1) Ask Steam which apps changed since the last run (PICS, anonymous).
// 2) Look up each unknown app (Game, DLC, Demo, …).
// 3) If its Steam ID (or name) is already in gamelist.json → ignore.
// 4) Otherwise append it to gamelist.json with created_at, and refresh the daily view.
//
// The main archive is gamelist.json (filter by created_at anytime).
// latestPath (e.g. new_today.json) is rewritten each run as "everything with
// created_at on today's UTC date" — so same-day re-runs accumulate, next day
// starts fresh. A dated copy new_YYYY-MM-DD.json is also written and kept.
//
// Usage:
//   SteamNewApps <gamelistPath> <latestPath>
//   SteamNewApps --import-csv <csvPath> <gamelistPath>   # one-shot CSV → JSON
//
// gamelist.json: { "meta": {…}, "games": { "<id>": { name, type, created_at?, … } } }
// First Steam run only baselines the change number and reports nothing.

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using SteamKit2;

static string NormName(string? name)
{
    if (string.IsNullOrWhiteSpace(name)) return "";
    var sb = new StringBuilder(name.Length);
    foreach (var ch in name.Trim().ToLowerInvariant())
        if (!char.IsWhiteSpace(ch)) sb.Append(ch);
    return sb.ToString();
}

JsonObject GetOrAdd(JsonObject parent, string key)
{
    if (parent[key] is JsonObject existing) return existing;
    var created = new JsonObject();
    parent[key] = created;
    return created;
}

static string? Kv(KeyValue common, string name) =>
    common[name] == KeyValue.Invalid ? null : common[name].Value;

static string? ReleaseDateIso(KeyValue common)
{
    if (long.TryParse(Kv(common, "steam_release_date"), out var unix) && unix > 0)
        return DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.ToString("yyyy-MM-dd");
    return null;
}

static void SaveJson(string path, JsonObject root)
{
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
    if (File.Exists(path))
        File.Copy(path, Path.ChangeExtension(path, null) + ".backup.json", true);
    var tmp = path + ".tmp";
    File.WriteAllText(tmp, root.ToJsonString());
    File.Move(tmp, path, true);
}

// ---------- CSV → gamelist.json ----------
if (args is ["--import-csv", var csvPath, var outPath])
{
    csvPath = Path.GetFullPath(csvPath);
    outPath = Path.GetFullPath(outPath);
    if (!File.Exists(csvPath))
    {
        Console.Error.WriteLine("CSV not found: " + csvPath);
        return 1;
    }

    JsonObject importRoot;
    if (File.Exists(outPath))
        importRoot = JsonNode.Parse(File.ReadAllText(outPath))?.AsObject() ?? new JsonObject();
    else
        importRoot = new JsonObject();

    var importMeta = GetOrAdd(importRoot, "meta");
    var importGames = GetOrAdd(importRoot, "games");
    var imported = 0;

    using var reader = new StreamReader(csvPath);
    var header = reader.ReadLine() ?? "";
    var cols = header.Trim().TrimStart('\ufeff').Split(',');
    static string Clean(string s) => s.Trim().Trim('"');
    var idIdx = Array.FindIndex(cols, c => Clean(c).Equals("Steam ID", StringComparison.OrdinalIgnoreCase));
    var nameIdx = Array.FindIndex(cols, c => Clean(c).Equals("Name", StringComparison.OrdinalIgnoreCase));
    var dateIdx = Array.FindIndex(cols, c => Clean(c).Equals("First release date", StringComparison.OrdinalIgnoreCase));
    if (idIdx < 0)
    {
        Console.Error.WriteLine("CSV missing 'Steam ID' column");
        return 1;
    }

    static List<string> SplitCsv(string line)
    {
        var fields = new List<string>();
        var cur = new StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch == '"')
            {
                if (inQuotes && i + 1 < line.Length && line[i + 1] == '"') { cur.Append('"'); i++; }
                else inQuotes = !inQuotes;
            }
            else if (ch == ',' && !inQuotes) { fields.Add(cur.ToString()); cur.Clear(); }
            else cur.Append(ch);
        }
        fields.Add(cur.ToString());
        return fields;
    }

    while (!reader.EndOfStream)
    {
        var line = reader.ReadLine();
        if (string.IsNullOrWhiteSpace(line)) continue;
        var fields = SplitCsv(line);
        if (idIdx >= fields.Count) continue;
        var id = fields[idIdx].Trim();
        if (string.IsNullOrEmpty(id) || importGames.ContainsKey(id)) continue;
        var name = nameIdx >= 0 && nameIdx < fields.Count ? fields[nameIdx] : "";
        string? released = null;
        if (dateIdx >= 0 && dateIdx < fields.Count && DateTime.TryParse(fields[dateIdx],
                CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dt))
            released = dt.ToString("yyyy-MM-dd");

        importGames[id] = new JsonObject
        {
            ["name"] = name,
            ["type"] = "Game",
            ["released"] = released,
            ["source"] = "csv",
        };
        imported++;
    }

    importMeta["version"] = 1;
    importMeta["count"] = importGames.Count;
    importMeta["source_csv"] = Path.GetFileName(csvPath);
    importMeta["imported_at"] = DateTime.UtcNow.ToString("o");
    SaveJson(outPath, importRoot);
    Console.WriteLine($"Imported {imported} new game(s). Gamelist now has {importGames.Count} entries → {outPath}");
    return 0;
}

if (args.Length < 2)
{
    Console.Error.WriteLine("Usage: SteamNewApps <gamelistPath> <latestPath>");
    Console.Error.WriteLine("       SteamNewApps --import-csv <csvPath> <gamelistPath>");
    return 1;
}

var gamelistPath = Path.GetFullPath(args[0]);
var latestPath = Path.GetFullPath(args[1]);
const int BatchSize = 200;
var jobTimeout = TimeSpan.FromSeconds(90);

// ---------- load gamelist ----------
JsonObject root;
if (File.Exists(gamelistPath))
{
    root = JsonNode.Parse(File.ReadAllText(gamelistPath))?.AsObject()
           ?? throw new Exception("Gamelist is not a JSON object: " + gamelistPath);
}
else
{
    root = new JsonObject();
}
var meta = GetOrAdd(root, "meta");
var games = GetOrAdd(root, "games");
var lastChange = (uint)(meta["changeNumber"]?.GetValue<long>() ?? 0);

// name → first known id (for "already in list by name?" checks)
var names = new Dictionary<string, string>(StringComparer.Ordinal);
foreach (var (id, node) in games)
{
    if (node is not JsonObject g) continue;
    var n = NormName(g["name"]?.GetValue<string>());
    if (n.Length > 0) names.TryAdd(n, id);
}

void SaveGamelist()
{
    meta["version"] = 1;
    meta["count"] = games.Count;
    meta["last_run"] = DateTime.UtcNow.ToString("o");
    SaveJson(gamelistPath, root);
}

// ---------- connect to Steam ----------
var steamClient = new SteamClient();
var manager = new CallbackManager(steamClient);
var steamUser = steamClient.GetHandler<SteamUser>()!;
var steamApps = steamClient.GetHandler<SteamApps>()!;

var loggedOn = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
var finished = false;
using var cts = new CancellationTokenSource();

manager.Subscribe<SteamClient.ConnectedCallback>(_ => steamUser.LogOnAnonymous());
manager.Subscribe<SteamClient.DisconnectedCallback>(_ =>
{
    if (!finished) loggedOn.TrySetException(new Exception("Disconnected from Steam before finishing"));
});
manager.Subscribe<SteamUser.LoggedOnCallback>(cb =>
{
    if (cb.Result == EResult.OK) loggedOn.TrySetResult(true);
    else loggedOn.TrySetException(new Exception("Anonymous logon failed: " + cb.Result));
});

var pump = Task.Run(() =>
{
    while (!cts.IsCancellationRequested)
        manager.RunWaitCallbacks(TimeSpan.FromMilliseconds(500));
});

async Task<int> Shutdown(int code)
{
    finished = true;
    try { steamUser.LogOff(); } catch { }
    await Task.Delay(500);
    steamClient.Disconnect();
    cts.Cancel();
    await pump;
    return code;
}

try
{
    Console.WriteLine($"Gamelist: {games.Count} known app(s).");
    Console.WriteLine("Connecting to Steam...");
    steamClient.Connect();
    await loggedOn.Task.WaitAsync(TimeSpan.FromSeconds(60));
    Console.WriteLine("Logged on anonymously.");

    if (lastChange == 0)
    {
        var baselineJob = steamApps.PICSGetChangesSince(0, false, false);
        baselineJob.Timeout = jobTimeout;
        var baseline = await baselineJob.ToTask();
        meta["changeNumber"] = (long)baseline.CurrentChangeNumber;
        SaveGamelist();
        Console.WriteLine($"Baseline set at change number {baseline.CurrentChangeNumber}. Nothing reported on this run.");
        return await Shutdown(0);
    }

    var changesJob = steamApps.PICSGetChangesSince(lastChange, true, false);
    changesJob.Timeout = jobTimeout;
    var changes = await changesJob.ToTask();

    if (changes.RequiresFullAppUpdate || changes.RequiresFullUpdate)
    {
        meta["changeNumber"] = (long)changes.CurrentChangeNumber;
        SaveGamelist();
        Console.Error.WriteLine($"WARNING: last change number {lastChange} is too old for Steam to list changes. " +
                                $"Re-baselined at {changes.CurrentChangeNumber}; apps created in the gap were missed.");
        return await Shutdown(2);
    }

    var candidates = changes.AppChanges.Keys
        .Where(id => !games.ContainsKey(id.ToString()))
        .ToList();
    Console.WriteLine($"Change {lastChange} -> {changes.CurrentChangeNumber}: " +
                      $"{changes.AppChanges.Count} apps changed, {candidates.Count} not in gamelist by ID.");

    var found = new List<JsonObject>();
    var skippedByName = 0;
    var now = DateTime.UtcNow.ToString("o");

    for (var i = 0; i < candidates.Count; i += BatchSize)
    {
        var batch = candidates.Skip(i).Take(BatchSize).ToList();

        var tokenJob = steamApps.PICSGetAccessTokens(batch, Array.Empty<uint>());
        tokenJob.Timeout = jobTimeout;
        var tokens = await tokenJob.ToTask();

        var requests = batch
            .Select(id => new SteamApps.PICSRequest(id, tokens.AppTokens.TryGetValue(id, out var t) ? t : 0))
            .ToList();

        var infoJob = steamApps.PICSGetProductInfo(requests, new List<SteamApps.PICSRequest>());
        infoJob.Timeout = jobTimeout;
        var resultSet = await infoJob.ToTask();

        foreach (var result in resultSet.Results)
        {
            foreach (var (appId, info) in result.Apps)
            {
                var common = info.KeyValues["common"];
                if (common is null || common == KeyValue.Invalid) continue;
                var type = Kv(common, "type");
                if (string.IsNullOrEmpty(type))
                    continue; // hidden / token-gated: retry when it changes again

                var id = appId.ToString();
                if (games.ContainsKey(id)) continue;

                var name = Kv(common, "name") ?? "";
                var norm = NormName(name);
                if (norm.Length > 0 && names.ContainsKey(norm))
                {
                    // Same title already in gamelist under another id — record id so we
                    // don't re-check it, but don't treat it as a new page (no created_at).
                    games[id] = new JsonObject
                    {
                        ["name"] = name,
                        ["type"] = type,
                        ["release_state"] = Kv(common, "releasestate"),
                        ["released"] = ReleaseDateIso(common),
                        ["source"] = "pics-name-match",
                        ["matched_name_of"] = names[norm],
                    };
                    skippedByName++;
                    continue;
                }

                var releaseState = Kv(common, "releasestate");
                var releaseDate = ReleaseDateIso(common);
                games[id] = new JsonObject
                {
                    ["name"] = name,
                    ["type"] = type,
                    ["release_state"] = releaseState,
                    ["released"] = releaseDate,
                    ["source"] = "pics",
                    ["created_at"] = now, // when we first added it to gamelist
                };
                if (norm.Length > 0) names.TryAdd(norm, id);

                found.Add(new JsonObject
                {
                    ["id"] = id,
                    ["name"] = name,
                    ["type"] = type,
                    ["release_state"] = releaseState,
                    ["release_date"] = releaseDate,
                    ["created_at"] = now,
                    ["store_url"] = "https://store.steampowered.com/app/" + id,
                });
                Console.WriteLine($"  + [{type}] {name} ({id})");
            }
        }
        Console.WriteLine($"  checked {Math.Min(i + BatchSize, candidates.Count)}/{candidates.Count}");
    }

    meta["changeNumber"] = (long)changes.CurrentChangeNumber;
    SaveGamelist();

    // Daily view = everything in gamelist with created_at on today's UTC date
    // (accumulates across same-day runs; next UTC day starts empty).
    var today = DateTime.UtcNow.Date;
    var todayStr = today.ToString("yyyy-MM-dd");
    var todayRows = new List<JsonObject>();
    foreach (var (id, node) in games)
    {
        if (node is not JsonObject g) continue;
        var created = g["created_at"]?.GetValue<string>() ?? g["first_seen_at"]?.GetValue<string>();
        if (string.IsNullOrEmpty(created)) continue;
        if (!DateTimeOffset.TryParse(created, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var when))
            continue;
        if (when.UtcDateTime.Date != today) continue;

        todayRows.Add(new JsonObject
        {
            ["id"] = id,
            ["name"] = g["name"]?.GetValue<string>() ?? "",
            ["type"] = g["type"]?.GetValue<string>() ?? "",
            ["release_state"] = g["release_state"]?.GetValue<string>(),
            ["release_date"] = g["released"]?.GetValue<string>(),
            ["created_at"] = created,
            ["store_url"] = "https://store.steampowered.com/app/" + id,
        });
    }

    todayRows = todayRows
        .OrderBy(a => a["type"]?.GetValue<string>() ?? "", StringComparer.OrdinalIgnoreCase)
        .ThenBy(a => a["name"]?.GetValue<string>() ?? "", StringComparer.OrdinalIgnoreCase)
        .ToList();

    var byType = todayRows
        .GroupBy(a => a["type"]?.GetValue<string>() ?? "Unknown")
        .ToDictionary(g => g.Key, g => g.Count());

    var appsArr = new JsonArray();
    foreach (var row in todayRows) appsArr.Add(row);

    var digest = new JsonObject
    {
        ["generated_at"] = DateTime.UtcNow.ToString("o"),
        ["date"] = todayStr,
        ["count"] = todayRows.Count,
        ["new_this_run"] = found.Count,
        ["skipped_name_match"] = skippedByName,
        ["by_type"] = new JsonObject(byType.ToDictionary(
            kv => kv.Key,
            kv => (JsonNode?)JsonValue.Create(kv.Value))),
        ["apps"] = appsArr,
    };

    Directory.CreateDirectory(Path.GetDirectoryName(latestPath)!);
    var latestTmp = latestPath + ".tmp";
    File.WriteAllText(latestTmp, digest.ToJsonString());
    File.Move(latestTmp, latestPath, true);

    // Keep a dated snapshot so tomorrow's rewrite of new_today.json does not lose history.
    var datedPath = Path.Combine(Path.GetDirectoryName(latestPath)!, $"new_{todayStr}.json");
    File.Copy(latestPath, datedPath, true);

    Console.WriteLine($"Done. {found.Count} new this run; {todayRows.Count} total with created_at={todayStr}." +
                      (skippedByName > 0 ? $" ({skippedByName} skipped, name already known)" : "") +
                      $" Gamelist: {games.Count} apps.");
    foreach (var (type, count) in byType.OrderByDescending(kv => kv.Value)
                 .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
        Console.WriteLine($"  {type}: {count}");
    Console.WriteLine($"Wrote {latestPath} and {datedPath}");

    return await Shutdown(0);
}
catch (Exception ex)
{
    Console.Error.WriteLine("ERROR: " + ex.Message);
    return await Shutdown(1);
}
