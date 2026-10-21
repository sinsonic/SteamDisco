// SteamNewApps - daily Steam watcher.
//
// 1) Ask Steam which apps changed since the last run (PICS, anonymous).
// 2) Look up each unknown app (Game, DLC, Demo, …).
// 3) If its Steam ID (or name) is already in gamelist.json → ignore.
// 4) Otherwise enrich from Steam Store (tags, categories, followers when available),
//    append to gamelist.json with created_at, and refresh the daily view.
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
// gamelist.json: { "meta": {…}, "games": { "<id>": { name, type, tags, categories, followers?, created_at?, … } } }
// First Steam run only baselines the change number and reports nothing.

using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
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

static JsonArray ToJsonArray(IEnumerable<string> items)
{
    var arr = new JsonArray();
    foreach (var s in items) arr.Add(s);
    return arr;
}

// Steam store category ids → display names (player modes + common features).
// Source: Steamworks store category documentation.
var CategoryNames = new Dictionary<int, string>
{
    [1] = "Multi-player",
    [2] = "Single-player",
    [8] = "Valve Anti-Cheat enabled",
    [9] = "Co-op",
    [13] = "Captions available",
    [14] = "Commentary available",
    [15] = "Stats",
    [16] = "Includes Source SDK",
    [17] = "Includes level editor",
    [18] = "Partial Controller Support",
    [20] = "MMO",
    [22] = "Steam Achievements",
    [23] = "Steam Cloud",
    [24] = "Shared/Split Screen",
    [25] = "Steam Leaderboards",
    [27] = "Cross-Platform Multiplayer",
    [28] = "Full controller support",
    [29] = "Steam Trading Cards",
    [30] = "Steam Workshop",
    [35] = "In-App Purchases",
    [36] = "Online PvP",
    [37] = "Shared/Split Screen PvP",
    [38] = "Online Co-op",
    [39] = "Shared/Split Screen Co-op",
    [40] = "SteamVR Collectibles",
    [41] = "Remote Play on Phone",
    [42] = "Remote Play on Tablet",
    [43] = "Remote Play on TV",
    [44] = "Remote Play Together",
    [47] = "LAN PvP",
    [48] = "LAN Co-op",
    [49] = "Shared/Split Screen",
    [51] = "Steam Workshop",
    [52] = "Tracked Controller Support",
    [53] = "VR Supported",
    [54] = "VR Only",
    [61] = "HDR available",
    [62] = "Family Sharing",
    [63] = "Steam Timeline",
    [64] = "Adjustable Text Size",
    [65] = "Keyboard Only Option",
    [66] = "Color Alternatives",
    [67] = "Camera Comfort",
    [68] = "Custom Volume Controls",
    [69] = "Stereo Sound",
    [70] = "Surround Sound",
    [71] = "Narrated Game Menus",
    [72] = "Playable without Timed Input",
    [73] = "Save Anytime",
    [74] = "Playable without Timed Input",
    [75] = "Camera Comfort",
};

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
const int StoreBatchSize = 50;
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

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
http.DefaultRequestHeaders.UserAgent.ParseAdd("SteamDisco/1.0 (+https://github.com/sinsonic/SteamDisco)");
http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

async Task<Dictionary<int, string>> LoadTagNamesAsync()
{
    var map = new Dictionary<int, string>();
    try
    {
        var json = await http.GetStringAsync(
            "https://api.steampowered.com/IStoreService/GetTagList/v1/?language=english");
        var doc = JsonNode.Parse(json);
        if (doc?["response"]?["tags"] is JsonArray tags)
        {
            foreach (var t in tags.OfType<JsonObject>())
            {
                var id = t["tagid"]?.GetValue<int>() ?? 0;
                var name = t["name"]?.GetValue<string>();
                if (id > 0 && !string.IsNullOrEmpty(name)) map[id] = name;
            }
        }
        Console.WriteLine($"Loaded {map.Count} Steam tag names.");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("WARNING: could not load Steam tag list: " + ex.Message);
    }
    return map;
}

async Task<Dictionary<string, JsonObject>> FetchStoreItemsAsync(IReadOnlyList<uint> appIds, Dictionary<int, string> tagNames)
{
    var result = new Dictionary<string, JsonObject>();
    if (appIds.Count == 0) return result;

    for (var i = 0; i < appIds.Count; i += StoreBatchSize)
    {
        var batch = appIds.Skip(i).Take(StoreBatchSize).ToList();
        var payload = new
        {
            ids = batch.Select(id => new { appid = id }).ToArray(),
            context = new { language = "english", country_code = "US" },
            data_request = new
            {
                include_basic_info = true,
                include_tag_count = 20,
                include_categories = true,
                include_reviews = true,
            },
        };
        var url = "https://api.steampowered.com/IStoreBrowseService/GetItems/v1/?input_json=" +
                  Uri.EscapeDataString(JsonSerializer.Serialize(payload));

        try
        {
            var json = await http.GetStringAsync(url);
            var doc = JsonNode.Parse(json);
            var items = doc?["response"]?["store_items"] as JsonArray;
            if (items == null) continue;

            foreach (var item in items.OfType<JsonObject>())
            {
                var appId = item["appid"]?.GetValue<int>()
                            ?? item["id"]?.GetValue<int>()
                            ?? 0;
                if (appId == 0) continue;
                var id = appId.ToString();

                var tagList = new List<(int id, int weight)>();
                if (item["tags"] is JsonArray tags)
                {
                    foreach (var t in tags.OfType<JsonObject>())
                    {
                        var tid = t["tagid"]?.GetValue<int>() ?? 0;
                        var weight = t["weight"]?.GetValue<int>() ?? 0;
                        if (tid > 0) tagList.Add((tid, weight));
                    }
                }
                tagList.Sort((a, b) => b.weight.CompareTo(a.weight));
                var tagNamesResolved = tagList
                    .Select(t => tagNames.TryGetValue(t.id, out var n) ? n : null)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Cast<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var categoryIds = new List<int>();
                if (item["categories"] is JsonObject cats)
                {
                    foreach (var key in new[] { "supported_player_categoryids", "feature_categoryids" })
                    {
                        if (cats[key] is not JsonArray arr) continue;
                        foreach (var n in arr)
                        {
                            if (n != null && n.GetValueKind() == JsonValueKind.Number)
                                categoryIds.Add(n.GetValue<int>());
                        }
                    }
                }
                var categoryNames = categoryIds
                    .Distinct()
                    .Select(cid => CategoryNames.TryGetValue(cid, out var n) ? n : $"Category {cid}")
                    .ToList();

                // Prefer player-facing categories first in the list.
                categoryNames = categoryNames
                    .OrderBy(c => c.StartsWith("Category ", StringComparison.Ordinal) ? 1 : 0)
                    .ThenBy(c => c, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                long? followers = null; // Steam does not expose app follower counts via public API

                var row = new JsonObject
                {
                    ["tags"] = ToJsonArray(tagNamesResolved),
                    ["categories"] = ToJsonArray(categoryNames),
                    ["followers"] = followers,
                };

                if (item["basic_info"] is JsonObject basic)
                {
                    if (basic["short_description"]?.GetValue<string>() is { Length: > 0 } desc)
                        row["short_description"] = desc;
                    if (basic["developers"] is JsonArray devs)
                    {
                        var namesArr = new JsonArray();
                        foreach (var d in devs.OfType<JsonObject>())
                            if (d["name"]?.GetValue<string>() is { Length: > 0 } dn)
                                namesArr.Add(dn);
                        if (namesArr.Count > 0) row["developers"] = namesArr;
                    }
                    if (basic["publishers"] is JsonArray pubs)
                    {
                        var namesArr = new JsonArray();
                        foreach (var p in pubs.OfType<JsonObject>())
                            if (p["name"]?.GetValue<string>() is { Length: > 0 } pn)
                                namesArr.Add(pn);
                        if (namesArr.Count > 0) row["publishers"] = namesArr;
                    }
                }

                if (item["reviews"]?["summary_filtered"] is JsonObject rev)
                {
                    row["review_count"] = rev["review_count"]?.DeepClone();
                    row["review_score_label"] = rev["review_score_label"]?.DeepClone();
                    row["percent_positive"] = rev["percent_positive"]?.DeepClone();
                }

                result[id] = row;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"WARNING: store enrich batch failed: {ex.Message}");
        }

        if (i + StoreBatchSize < appIds.Count)
            await Task.Delay(200);
    }

    return result;
}

void ApplyEnrichment(JsonObject target, JsonObject enrich)
{
    foreach (var key in new[] { "tags", "categories", "short_description",
                 "developers", "publishers", "review_count", "review_score_label", "percent_positive" })
    {
        if (enrich[key] != null)
            target[key] = enrich[key]!.DeepClone();
    }
    // Always set followers (often null — Steam does not expose this publicly).
    target["followers"] = enrich["followers"]?.DeepClone();
}

JsonObject PublicRow(string id, JsonObject g)
{
    var row = new JsonObject
    {
        ["id"] = id,
        ["name"] = g["name"]?.GetValue<string>() ?? "",
        ["type"] = g["type"]?.GetValue<string>() ?? "",
        ["release_state"] = g["release_state"]?.GetValue<string>(),
        ["release_date"] = g["released"]?.GetValue<string>(),
        ["created_at"] = g["created_at"]?.GetValue<string>() ?? g["first_seen_at"]?.GetValue<string>(),
        ["store_url"] = "https://store.steampowered.com/app/" + id,
    };
    foreach (var key in new[] { "tags", "categories", "followers", "short_description",
                 "developers", "publishers", "review_count", "review_score_label", "percent_positive" })
    {
        if (g[key] != null) row[key] = g[key]!.DeepClone();
    }
    return row;
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

    void WriteTodayDigest(int newThisRun, int skippedNameMatch)
    {
        var day = DateTime.UtcNow.Date;
        var dayStr = day.ToString("yyyy-MM-dd");
        var todayRows = new List<JsonObject>();
        foreach (var (id, node) in games)
        {
            if (node is not JsonObject g) continue;
            var created = g["created_at"]?.GetValue<string>() ?? g["first_seen_at"]?.GetValue<string>();
            if (string.IsNullOrEmpty(created)) continue;
            if (!DateTimeOffset.TryParse(created, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var when))
                continue;
            if (when.UtcDateTime.Date != day) continue;
            todayRows.Add(PublicRow(id, g));
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
            ["date"] = dayStr,
            ["count"] = todayRows.Count,
            ["new_this_run"] = newThisRun,
            ["skipped_name_match"] = skippedNameMatch,
            ["by_type"] = new JsonObject(byType.ToDictionary(
                kv => kv.Key,
                kv => (JsonNode?)JsonValue.Create(kv.Value))),
            ["apps"] = appsArr,
        };

        Directory.CreateDirectory(Path.GetDirectoryName(latestPath)!);
        var latestTmp = latestPath + ".tmp";
        File.WriteAllText(latestTmp, digest.ToJsonString());
        File.Move(latestTmp, latestPath, true);

        var datedPath = Path.Combine(Path.GetDirectoryName(latestPath)!, $"new_{dayStr}.json");
        File.Copy(latestPath, datedPath, true);

        Console.WriteLine($"Today ({dayStr} UTC): {todayRows.Count} app(s) with created_at today.");
        foreach (var (type, count) in byType.OrderByDescending(kv => kv.Value)
                     .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            Console.WriteLine($"  {type}: {count}");
        Console.WriteLine($"Wrote {latestPath} and {datedPath}");
    }

    if (changes.RequiresFullAppUpdate || changes.RequiresFullUpdate)
    {
        // Steam only keeps a short change history. Exit 0 so CI still commits the
        // new baseline — otherwise the next run hits the same stale change number.
        meta["changeNumber"] = (long)changes.CurrentChangeNumber;
        SaveGamelist();
        WriteTodayDigest(0, 0);
        Console.Error.WriteLine($"WARNING: last change number {lastChange} is too old for Steam to list changes. " +
                                $"Re-baselined at {changes.CurrentChangeNumber}; apps created in the gap were missed.");
        return await Shutdown(0);
    }

    var candidates = changes.AppChanges.Keys
        .Where(id => !games.ContainsKey(id.ToString()))
        .ToList();
    Console.WriteLine($"Change {lastChange} -> {changes.CurrentChangeNumber}: " +
                      $"{changes.AppChanges.Count} apps changed, {candidates.Count} not in gamelist by ID.");

    var foundIds = new List<uint>();
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
                    continue;

                var id = appId.ToString();
                if (games.ContainsKey(id)) continue;

                var name = Kv(common, "name") ?? "";
                var norm = NormName(name);
                if (norm.Length > 0 && names.ContainsKey(norm))
                {
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
                    ["created_at"] = now,
                };
                if (norm.Length > 0) names.TryAdd(norm, id);
                foundIds.Add(appId);
                Console.WriteLine($"  + [{type}] {name} ({id})");
            }
        }
        Console.WriteLine($"  checked {Math.Min(i + BatchSize, candidates.Count)}/{candidates.Count}");
    }

    // Also backfill store details for anything created today that still lacks tags.
    var today = DateTime.UtcNow.Date;
    var needEnrich = new HashSet<uint>(foundIds);
    foreach (var (id, node) in games)
    {
        if (node is not JsonObject g) continue;
        var created = g["created_at"]?.GetValue<string>() ?? g["first_seen_at"]?.GetValue<string>();
        if (string.IsNullOrEmpty(created)) continue;
        if (!DateTimeOffset.TryParse(created, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var when) || when.UtcDateTime.Date != today)
            continue;
        if (g["tags"] is JsonArray { Count: > 0 }) continue;
        if (uint.TryParse(id, out var uid)) needEnrich.Add(uid);
    }

    if (needEnrich.Count > 0)
    {
        Console.WriteLine($"Enriching {needEnrich.Count} app(s) with Steam Store tags/categories...");
        var tagNames = await LoadTagNamesAsync();
        var enrich = await FetchStoreItemsAsync(needEnrich.ToList(), tagNames);
        foreach (var (id, data) in enrich)
        {
            if (games[id] is not JsonObject g) continue;
            ApplyEnrichment(g, data);
            var tagCount = (g["tags"] as JsonArray)?.Count ?? 0;
            var catCount = (g["categories"] as JsonArray)?.Count ?? 0;
            Console.WriteLine($"    {id}: {tagCount} tags, {catCount} categories");
        }
        Console.WriteLine($"Store details applied to {enrich.Count}/{needEnrich.Count} app(s).");
        Console.WriteLine("Note: Steam does not publish app follower counts via public API; followers stays null.");
    }

    meta["changeNumber"] = (long)changes.CurrentChangeNumber;
    SaveGamelist();
    WriteTodayDigest(foundIds.Count, skippedByName);
    Console.WriteLine($"Done. {foundIds.Count} new this run." +
                      (skippedByName > 0 ? $" ({skippedByName} skipped, name already known)" : "") +
                      $" Gamelist: {games.Count} apps.");

    return await Shutdown(0);
}
catch (Exception ex)
{
    Console.Error.WriteLine("ERROR: " + ex.Message);
    return await Shutdown(1);
}
