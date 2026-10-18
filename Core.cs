using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Lumen;

public class Game : INotifyPropertyChanged
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Args { get; set; } = "";
    public string? SteamId { get; set; }
    public string? Description { get; set; }
    public int DescVer { get; set; }          // 7 = description from HowLongToBeat
    public bool DescManual { get; set; }      // typed by the user, never overwritten
    public string? TrailerUrl { get; set; }   // your link: YouTube / direct file / local path
    public string? MusicUrl { get; set; }     // your link: YouTube / direct file / local path
    public string? HltbUrl { get; set; }      // howlongtobeat.com/game/<id>

    // Custom art (URL or local file). Empty = auto-find via SteamGridDB.
    public string? GridUrl { get; set; }
    public string? HeroUrl { get; set; }
    public string? LogoUrl { get; set; }

    // Cached local files
    public string? Grid { get; set; }
    public string? Hero { get; set; }
    public string? Logo { get; set; }
    public string? TrailerFile { get; set; }
    public string? MusicFile { get; set; }
    public string? CustomTheme { get; set; }   // user-picked audio, wins over MusicFile

    public double Main { get; set; }
    public double Extra { get; set; }
    public double Full { get; set; }
    public bool ManualHours { get; set; }
    public bool Favorite { get; set; }        // favourites are always shown first
    public string? HltbGameId { get; set; }   // game id on howlongtobeat.com (used for the description)
    public bool HltbIdTried { get; set; }
    public bool HltbTried { get; set; }       // looked up once; if it failed, enter the times manually

    // Extra HowLongToBeat details shown next to the description
    public string? Genres { get; set; }
    public string? Developer { get; set; }
    public string? Publisher { get; set; }
    public string? Release { get; set; }        // oldest known release date, ISO yyyy-MM-dd
    public List<Dlc> Dlc { get; set; } = new(); // "additional content" (DLC) play times
    public int MetaVer { get; set; }            // 1 = genres / developer / publisher / release / DLC loaded

    [JsonIgnore] public string ReleaseText => Fmt.Date(Release);

    [JsonIgnore] public bool HasMeta =>
        !string.IsNullOrWhiteSpace(Genres) || !string.IsNullOrWhiteSpace(Developer)
        || !string.IsNullOrWhiteSpace(Publisher) || ReleaseText.Length > 0 || Dlc.Count > 0;

    public bool Fetched { get; set; }
    public bool TrailerTried { get; set; }
    public bool MusicTried { get; set; }
    public int MusicVer { get; set; }         // 1 = downloaded/remuxed with ffmpeg (fixes the DASH m4a noise)

    [JsonIgnore] public bool NoGrid => Grid is null;

    public event PropertyChangedEventHandler? PropertyChanged;
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
}

// One "additional content" (DLC / expansion) entry from HowLongToBeat, with its play times in hours.
public class Dlc
{
    public string Name { get; set; } = "";
    public double Main { get; set; }
    public double Extra { get; set; }
    public double Full { get; set; }
    [JsonIgnore] public string MainH => Fmt.Hours(Main);
    [JsonIgnore] public string ExtraH => Fmt.Hours(Extra);
    [JsonIgnore] public string FullH => Fmt.Hours(Full);
}

// Small display formatters shared by the model and the window.
public static class Fmt
{
    // Same style as the main Main / Main+ / 100% boxes (e.g. "25.6 h"), so the two line up.
    public static string Hours(double h) =>
        h <= 0 ? "—" : h < 1 ? $"{h * 60:0} min" : $"{h:0.#} h";

    static string Ord(int d) => d % 100 is >= 11 and <= 13 ? "th"
        : (d % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" };

    // "2022-02-25" -> "February 25th, 2022"
    public static string Date(string? iso) =>
        DateTime.TryParse(iso, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? $"{d:MMMM} {d.Day}{Ord(d.Day)}, {d.Year}" : "";
}

public class Settings
{
    public string SgdbKey { get; set; } = "";
    public bool KeyAsked { get; set; }
    public double UiScale { get; set; } = 1.0;
    public double TextScale { get; set; } = 1.0;   // info text size (play times, description, genres), on top of UiScale
}

public static class Store
{
    public static readonly string Dir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Lumen");
    public static readonly string Cache = System.IO.Path.Combine(Dir, "cache");
    static readonly JsonSerializerOptions J = new() { WriteIndented = true };
    static string F(string n) => System.IO.Path.Combine(Dir, n);

    public static void Log(string msg)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var p = F("log.txt");
            if (File.Exists(p) && new FileInfo(p).Length > 400_000) File.Delete(p);
            File.AppendAllText(p, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}\n");
        }
        catch { }
    }

    public static ObservableCollection<Game> Games = new();
    public static Settings Settings = new();

    public static void Load()
    {
        Directory.CreateDirectory(Cache);
        try { Games = JsonSerializer.Deserialize<ObservableCollection<Game>>(File.ReadAllText(F("games.json"))) ?? new(); } catch { }
        try { Settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(F("settings.json"))) ?? new(); } catch { }
    }

    public static void Save()
    {
        try
        {
            File.WriteAllText(F("games.json"), JsonSerializer.Serialize(Games, J));
            File.WriteAllText(F("settings.json"), JsonSerializer.Serialize(Settings, J));
        }
        catch { }
    }
}

// ================= Artwork + HowLongToBeat =================
public static class Meta
{
    internal static readonly HttpClient H = new() { Timeout = TimeSpan.FromSeconds(30) };

    static Meta() => H.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");

    public static async Task Fill(Game g, bool force = false)
    {
        await Task.WhenAll(Art(g, force), Task.Run(async () => { await Hltb(g); await Desc(g); }));
        g.Fetched = true;
        Store.Save();
        g.Refresh();
    }

    static string? First(JsonNode? n, string field) =>
        (n?["data"] as JsonArray)?.FirstOrDefault()?[field]?.ToString();

    // Steam store data (description, trailers). Remembers the matched appid on the game.
    internal static async Task<JsonNode?> SteamData(Game g)
    {
        try
        {
            if (g.SteamId is null)
            {
                var s = JsonNode.Parse(await H.GetStringAsync(
                    $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(g.Name)}&cc=us&l=en"));
                var items = s?["items"] as JsonArray;
                if (items is null || items.Count == 0) return null;
                var it = items.FirstOrDefault(i => string.Equals(i?["name"]?.ToString(), g.Name, StringComparison.OrdinalIgnoreCase)) ?? items[0];
                g.SteamId = it?["id"]?.ToString();
                if (g.SteamId is null) return null;
            }
            var d = JsonNode.Parse(await H.GetStringAsync(
                $"https://store.steampowered.com/api/appdetails?appids={g.SteamId}&cc=us&l=en"));
            return d?[g.SteamId]?["data"];
        }
        catch (Exception ex) { Store.Log("Steam lookup failed: " + ex.Message); return null; }
    }

    // Cut long text at a sentence end (or a word boundary), never mid-word.
    internal static string Short(string s, int max = 420)
    {
        s = Regex.Replace(s, @"\s+", " ").Trim();
        if (s.Length <= max) return s;
        var cut = s[..max];
        var i = cut.LastIndexOfAny(new[] { '.', '!', '?' });
        if (i > 80) return cut[..(i + 1)];
        var sp = cut.LastIndexOf(' ');
        return (sp > 40 ? cut[..sp] : cut).TrimEnd(' ', ',', ';', ':', '-') + "…";
    }

    static string Plain(string html) =>
        Regex.Replace(System.Net.WebUtility.HtmlDecode(Regex.Replace(html, "<.*?>", " ")), @"\s+", " ").Trim();

    // Steam's whole "About this game" section as plain text (paragraph breaks and bullet points kept).
    static string? FullText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return null;
        var h = Regex.Replace(html, @"<\s*li[^>]*>", "\n• ", RegexOptions.IgnoreCase);
        h = Regex.Replace(h, @"<\s*(br|/p|/h\d|/ul|/ol|/div)[^>]*>", "\n", RegexOptions.IgnoreCase);
        h = System.Net.WebUtility.HtmlDecode(Regex.Replace(h, "<.*?>", " "));
        var lines = h.Split('\n').Select(x => Regex.Replace(x, @"[ \t\u00a0]+", " ").Trim()).ToList();
        var sb = new StringBuilder();
        int blanks = 0;
        foreach (var l in lines)
        {
            if (l.Length == 0) { blanks++; continue; }
            if (sb.Length > 0) sb.Append(blanks > 0 || l.Length > 0 ? "\n" : "").Append(blanks > 0 ? "\n" : "");
            sb.Append(l);
            blanks = 0;
        }
        var s = sb.ToString().Trim();
        return s.Length > 0 ? s : null;
    }

    internal const int DescVersion = 7;   // 7 = description from HowLongToBeat
    internal const int MetaVersion = 1;   // 1 = genres / developer / publisher / release / DLC loaded

    // Description + extra details come from the game's HowLongToBeat page (this is the one place we read it).
    internal static async Task Desc(Game g)
    {
        var id = HltbId(g.HltbUrl) ?? g.HltbGameId;
        bool needDesc = string.IsNullOrWhiteSpace(g.Description) || (!g.DescManual && g.DescVer < DescVersion);
        bool needMeta = g.MetaVer < MetaVersion;
        if (id is null || (!needDesc && !needMeta)) return;
        await HltbPage(g, id);
    }

    // Genres, developer, publisher, oldest release date and "additional content" (DLC) times, all from the
    // Next.js payload. Safe to call repeatedly: it only fills fields the user hasn't overridden.
    static void ReadDetails(Game g, JsonNode root)
    {
        var node = FindKey(root, "profile_summary") ?? FindKey(root, "profile_dev") ?? FindKey(root, "profile_genre");
        if (node != null)
        {
            if (!g.DescManual)
            {
                var summary = node["profile_summary"]?.ToString();
                if (!string.IsNullOrWhiteSpace(summary)) { g.Description = FullText(summary); g.DescVer = DescVersion; }
                else if (string.IsNullOrWhiteSpace(g.Description)) { g.Description = ""; g.DescVer = DescVersion; }
            }
            g.Genres = Clean(node["profile_genre"]?.ToString());
            g.Developer = Clean(node["profile_dev"]?.ToString());
            g.Publisher = Clean(node["profile_pub"]?.ToString());
            g.Release = OldestRelease(node);
        }

        var rels = root["props"]?["pageProps"]?["game"]?["data"]?["relationships"] as JsonArray;
        var dlc = new List<Dlc>();
        if (rels != null)
            foreach (var r in rels)
            {
                if (r is not JsonObject ro) continue;
                var name = Clean(ro["game_name"]?.ToString());
                if (name is null) continue;
                double H(string k) => double.TryParse(ro[k]?.ToString(), out var v) ? v / 3600.0 : 0;
                var d = new Dlc { Name = name, Main = H("comp_main"), Extra = H("comp_plus"), Full = H("comp_100") };
                if (d.Main > 0 || d.Extra > 0 || d.Full > 0) dlc.Add(d);
            }
        g.Dlc = dlc;
        g.MetaVer = MetaVersion;
    }

    static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // Oldest of the four regional release dates (the user asked for the earliest one).
    static string? OldestRelease(JsonObject g)
    {
        DateTime? best = null;
        foreach (var k in new[] { "release_world", "release_na", "release_eu", "release_jp" })
        {
            var d = AsDate(g[k]?.ToString());
            if (d is { } dt && (best is null || dt < best.Value)) best = dt;
        }
        return best?.ToString("yyyy-MM-dd");
    }

    static DateTime? AsDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (Regex.IsMatch(s, @"^\d{4}-\d{2}-\d{2}"))
            return DateTime.TryParseExact(s[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        if (long.TryParse(s, out var n) && n > 100_000)
            return DateTimeOffset.FromUnixTimeSeconds(n).UtcDateTime.Date;
        return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d2) ? d2 : null;
    }

    // A link the user pasted (or an auto-found one) is only kept when the reply really is an image:
    // an HTML error page or a hotlink-protected file must fail loudly instead of being cached and
    // then showing as an empty box.
    static async Task<string?> Download(string? url, Game g, string kind)
    {
        if (url is null) return null;
        try
        {
            var bytes = await H.GetByteArrayAsync(url);
            if (!Assets.LooksLikeImage(bytes))
            {
                Store.Log($"Artwork: the {kind} link for '{g.Name}' is not an image ({url})");
                return null;
            }
            return Assets.SaveArt(g, kind, bytes);
        }
        catch (Exception ex)
        {
            Store.Log($"Artwork: the {kind} link for '{g.Name}' failed: {ex.Message} ({url})");
            return null;
        }
    }

    static async Task<string?> Custom(string? src, Game g, string kind)
    {
        if (string.IsNullOrWhiteSpace(src)) return null;
        src = src.Trim().Trim('"');
        if (src.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return await Download(src, g, kind);
        try
        {
            if (File.Exists(src))
            {
                var bytes = await File.ReadAllBytesAsync(src);
                if (Assets.LooksLikeImage(bytes)) return Assets.SaveArt(g, kind, bytes);
                Store.Log($"Artwork: the {kind} file for '{g.Name}' is not an image ({src})");
            }
            else Store.Log($"Artwork: the {kind} file for '{g.Name}' was not found ({src})");
        }
        catch (Exception ex)
        {
            Store.Log($"Artwork: the {kind} file for '{g.Name}' failed: {ex.Message} ({src})");
        }
        return null;
    }

    static async Task Art(Game g, bool force)
    {
        try
        {
            var grid = await Custom(g.GridUrl, g, "grid");
            var hero = await Custom(g.HeroUrl, g, "hero");
            var logo = await Custom(g.LogoUrl, g, "logo");
            bool ng = grid is null && (force || g.Grid is null);
            bool nh = hero is null && (force || g.Hero is null);
            bool nl = logo is null && (force || g.Logo is null);
            if (ng || nh || nl)
            {
                var s = await Sgdb(g, ng, nh, nl);
                grid ??= s.grid; hero ??= s.hero; logo ??= s.logo;
            }
            g.Grid = grid ?? g.Grid;
            g.Hero = hero ?? g.Hero;
            g.Logo = logo ?? g.Logo;
        }
        catch { }
    }

    static async Task<(string? grid, string? hero, string? logo)> Sgdb(Game g, bool ng, bool nh, bool nl)
    {
        var key = Store.Settings.SgdbKey;
        if (string.IsNullOrWhiteSpace(key)) return default;
        try
        {
            async Task<JsonNode?> Get(string p)
            {
                using var r = new HttpRequestMessage(HttpMethod.Get, "https://www.steamgriddb.com/api/v2/" + p);
                r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
                using var res = await H.SendAsync(r);
                return JsonNode.Parse(await res.Content.ReadAsStringAsync());
            }
            async Task<string?> Pic(string p, string kind)
            {
                try { return await Download(First(await Get(p), "url"), g, kind); }
                catch { return null; }
            }

            var id = First(await Get("search/autocomplete/" + Uri.EscapeDataString(g.Name)), "id");
            if (id is null) return default;
            Task<string?> none = Task.FromResult<string?>(null);
            var grid = ng ? Pic($"grids/game/{id}?dimensions=600x900&types=static&mimes=image/png,image/jpeg", "grid") : none;
            var hero = nh ? Pic($"heroes/game/{id}?types=static&mimes=image/png,image/jpeg", "hero") : none;
            var logo = nl ? Pic($"logos/game/{id}?types=static&mimes=image/png", "logo") : none;
            await Task.WhenAll(grid, hero, logo);
            return (grid.Result, hero.Result, logo.Result);
        }
        catch { return default; }
    }

    // ---- HowLongToBeat (no official API): 1) your link  2) site search API  3) web search + game page ----
    // Lookups run one at a time, retry on temporary failures, and try several spellings of the game's name.
    static string? hltbEp;
    static readonly SemaphoreSlim hltbGate = new(1, 1);
    static readonly HttpClient HT = CreateHt();

    static HttpClient CreateHt()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        return c;
    }

    static string Norm(string s) => Regex.Replace(s.ToLowerInvariant(), "[^a-z0-9]", "");

    static string? HltbId(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (s.All(char.IsDigit)) return s;
        var m = Regex.Match(s, @"game/(\d+)");
        return m.Success ? m.Groups[1].Value : null;
    }

    // "The Witcher 3: Wild Hunt (GOTY)" -> full name, cleaned name, no punctuation, and the part before the subtitle.
    static List<string> Variants(string name)
    {
        var clean = Regex.Replace(name, @"[™®©]|\(.*?\)|\[.*?\]", " ");
        var raw = new[]
        {
            name,
            clean,
            Regex.Replace(clean, @"[^\p{L}\p{N}\s]", " "),
            Regex.Split(clean, @"\s*(?::| - | – )\s*")[0]
        };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var v in raw)
        {
            var t = Regex.Replace(v, @"\s+", " ").Trim();
            if (t.Length > 1 && seen.Add(t)) list.Add(t);
        }
        return list;
    }

    // One attempt only (no retries, no loops). If it fails, type the times / description in Edit.
    // Also finds the game's HowLongToBeat id, which the description is read from.
    public static async Task Hltb(Game g)
    {
        var link = HltbId(g.HltbUrl);
        bool needTimes = !g.ManualHours && !g.HltbTried;
        bool needId = link is null && g.HltbGameId is null && !g.HltbIdTried && !g.DescManual && g.DescVer < 7;
        if (!needTimes && !needId) return;
        g.HltbTried = true;
        g.HltbIdTried = true;
        try
        {
            bool ok;
            if (link != null) ok = await HltbPage(g, link);
            else
            {
                var ep = hltbEp ?? "search/site";
                ok = await HltbTry(g, ep, g.Name) > 0;
                if (ok) hltbEp = ep;
            }
            Store.Log(ok ? $"HLTB: '{g.Name}' -> id {g.HltbGameId}, {g.Main:0.#}/{g.Extra:0.#}/{g.Full:0.#} h"
                         : $"HLTB: nothing for '{g.Name}' - enter times / description manually in Edit");
        }
        catch (Exception ex) { Store.Log("HLTB: " + ex.Message); }
    }

    static string Snip(string s) => s.Length > 200 ? s[..200] : s;

    static int Score(string n, string alias, string key)
    {
        if (n.Length == 0) return 0;
        if (n == key) return 100;
        if (n.StartsWith(key)) return 80;
        if (key.StartsWith(n) && n.Length >= 5) return 70;
        if (n.Contains(key)) return 60;
        if (alias.Length > 0 && alias.Contains(key)) return 60;
        return 0;
    }

    // Best search result for the game; never falls back to an unrelated first result.
    static JsonNode? Best(JsonArray arr, string name)
    {
        var keys = Variants(name).Select(Norm).Where(k => k.Length >= 2).Distinct().ToList();
        if (keys.Count == 0) return null;
        JsonNode? best = null;
        int top = 0;
        foreach (var x in arr)
        {
            var n = Norm(x?["game_name"]?.ToString() ?? "");
            var al = Norm(x?["game_alias"]?.ToString() ?? "");
            int s = keys.Max(k => Score(n, al, k));
            if (s > top) { top = s; best = x; }
        }
        return top >= 60 ? best : null;
    }

    // 1 = found times, 0 = endpoint works but no match, -1 = endpoint failed (blocked / changed / offline)
    static async Task<int> HltbTry(Game g, string ep, string term)
    {
        try
        {
            var init = JsonNode.Parse(await HT.GetStringAsync(
                $"https://howlongtobeat.com/api/{ep}/init?t={DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}"));
            var tok = init?["token"]?.ToString();
            if (tok is null) { Store.Log($"HLTB /api/{ep}/init: no token"); return -1; }
            string? hk = init?["hpKey"]?.ToString(), hv = init?["hpVal"]?.ToString();

            var body = new JsonObject
            {
                ["searchType"] = "games",
                ["searchTerms"] = new JsonArray(term.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(s => (JsonNode)s).ToArray()),
                ["searchPage"] = 1,
                ["size"] = 20,
                ["searchOptions"] = JsonNode.Parse("{\"games\":{\"userId\":0,\"platform\":\"\",\"sortCategory\":\"popular\",\"rangeCategory\":\"main\",\"rangeTime\":{\"min\":0,\"max\":0},\"gameplay\":{\"perspective\":\"\",\"flow\":\"\",\"genre\":\"\",\"difficulty\":\"\"},\"rangeYear\":{\"min\":\"\",\"max\":\"\"},\"modifier\":\"\"},\"users\":{\"sortCategory\":\"postcount\"},\"lists\":{\"sortCategory\":\"follows\"},\"filter\":\"\",\"sort\":0,\"randomizer\":0}"),
                ["useCache"] = true
            };
            if (hk != null && hv != null) body[hk] = hv;

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://howlongtobeat.com/api/" + ep)
            { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
            req.Headers.Add("x-auth-token", tok);
            if (hk != null && hv != null) { req.Headers.Add("x-hp-key", hk); req.Headers.Add("x-hp-val", hv); }
            req.Headers.Add("Origin", "https://howlongtobeat.com");
            req.Headers.Referrer = new Uri("https://howlongtobeat.com/");
            req.Headers.Accept.ParseAdd("*/*");

            using var res = await HT.SendAsync(req);
            var text = await res.Content.ReadAsStringAsync();
            if (!res.IsSuccessStatusCode) { Store.Log($"HLTB /api/{ep}: HTTP {(int)res.StatusCode} {Snip(text)}"); return -1; }
            var arr = JsonNode.Parse(text)?["data"] as JsonArray;
            if (arr is null) { Store.Log($"HLTB /api/{ep}: unexpected reply {Snip(text)}"); return -1; }

            var hit = Best(arr, g.Name);
            if (hit is null) { Store.Log($"HLTB /api/{ep}: no match for '{term}' ({arr.Count} results)"); return 0; }

            g.HltbGameId = hit["game_id"]?.ToString() ?? g.HltbGameId;
            if (g.ManualHours) return 1;
            double Hrs(string k) => double.TryParse(hit[k]?.ToString(), out var v) ? v / 3600.0 : 0;
            double m = Hrs("comp_main"), x = Hrs("comp_plus"), f = Hrs("comp_100");
            if (m <= 0 && x <= 0 && f <= 0) return g.HltbGameId != null ? 1 : 0;
            g.Main = m; g.Extra = x; g.Full = f;
            return 1;
        }
        catch (Exception ex) { Store.Log($"HLTB /api/{ep}: {ex.Message}"); return -1; }
    }

    // Read the times + extra details straight from the game's page (Next.js embeds them as JSON; raw-HTML scan as a backup).
    static async Task<bool> HltbPage(Game g, string id)
    {
        try
        {
            var html = await HT.GetStringAsync("https://howlongtobeat.com/game/" + id);
            double main = 0, plus = 0, full = 0;

            var m = Regex.Match(html, "<script id=\"__NEXT_DATA__\"[^>]*>(.*?)</script>", RegexOptions.Singleline);
            if (m.Success)
            {
                JsonNode? root = null;
                try { root = JsonNode.Parse(m.Groups[1].Value); } catch { }
                var node = root is null ? null : FindKey(root, "comp_main");
                if (node != null)
                {
                    double Hrs(string k) => double.TryParse(node[k]?.ToString(), out var v) ? v / 3600.0 : 0;
                    main = Hrs("comp_main"); plus = Hrs("comp_plus"); full = Hrs("comp_100");
                }
                if (root != null) ReadDetails(g, root);
            }
            if (main <= 0 && plus <= 0 && full <= 0)
            {
                double Raw(string k)
                {
                    var r = Regex.Match(html, @"\\?""" + k + @"\\?""\s*:\s*(\d+)");
                    return r.Success && double.TryParse(r.Groups[1].Value, out var v) ? v / 3600.0 : 0;
                }
                main = Raw("comp_main"); plus = Raw("comp_plus"); full = Raw("comp_100");
            }
            if (main <= 0 && plus <= 0 && full <= 0) { Store.Log($"HLTB page {id}: no times found"); return false; }
            if (!g.ManualHours) { g.Main = main; g.Extra = plus; g.Full = full; }
            return true;
        }
        catch (Exception ex) { Store.Log($"HLTB page {id}: {ex.Message}"); return false; }
    }

    static JsonObject? FindKey(JsonNode? n, string key)
    {
        if (n is JsonObject o)
        {
            if (o.ContainsKey(key)) return o;
            foreach (var kv in o) { var r = FindKey(kv.Value, key); if (r != null) return r; }
        }
        else if (n is JsonArray a)
        {
            foreach (var x in a) { var r = FindKey(x, key); if (r != null) return r; }
        }
        return null;
    }
}

// ================= Trailers + theme music (downloaded and cached locally) =================
public static class Assets
{
    // YouTube no longer serves a combined progressive mp4 for most videos, so we take the best H.264 video
    // plus the m4a audio and let ffmpeg merge them into one playable mp4.
    const string TrailerFmt = "bv*[height<=480][vcodec^=avc1]+ba[ext=m4a]/bv*[height<=480][ext=mp4]+ba[ext=m4a]/b[height<=480]/bv*+ba/b";
    const string MusicFmt = "140/bestaudio[ext=m4a]";

    static readonly HttpClient H = new() { Timeout = TimeSpan.FromMinutes(10) };
    static readonly SemaphoreSlim gate = new(1, 1);   // one download at a time
    static bool updated, denoTried, ffmpegTried;

    static Assets() => H.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");

    static string Dir(string sub)
    {
        var d = System.IO.Path.Combine(Store.Dir, "media", sub);
        Directory.CreateDirectory(d);
        return d;
    }

    static string Bin => System.IO.Path.Combine(Store.Dir, "bin");
    static string YtPath => System.IO.Path.Combine(Bin, "yt-dlp.exe");
    static string DenoPath => System.IO.Path.Combine(Bin, "deno.exe");
    static string FfmpegPath => System.IO.Path.Combine(Bin, "ffmpeg.exe");
    // Use game name as base; replace invalid filename chars with underscore, trim.
    static string SafeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "game";
        var n = string.Concat(name.Trim().Select(c =>
            char.IsLetterOrDigit(c) || c == ' ' || c == '_' || c == '-' || c == '.' || c == '(' || c == ')' || c == '&'
                ? c : '_'));
        n = Regex.Replace(n, @"\s+", " ").Trim();
        n = Regex.Replace(n, @"[_]{2,}", "_").Trim('_', '.');
        // '%' and '#' are placeholders in yt-dlp output templates, never use them in file names.
        n = n.Replace('%', '_').Replace('#', '_');
        return string.IsNullOrWhiteSpace(n) ? "game" : n;
    }

    static string Stem(Game g) => SafeName(g.Name);
    static async Task Save(string url, string file)
    {
        var tmp = file + ".part";
        using (var s = await H.GetStreamAsync(url))
        using (var f = File.Create(tmp))
            await s.CopyToAsync(f);
        File.Move(tmp, file, true);
    }

    public static void DeleteTrailer(Game g)
    {
        try { if (g.TrailerFile != null) File.Delete(g.TrailerFile); } catch { }
        g.TrailerFile = null;
    }

    public static void DeleteMusic(Game g)
    {
        try { if (g.MusicFile != null) File.Delete(g.MusicFile); } catch { }
        g.MusicFile = null;
        g.MusicVer = 0;
    }

    public static void Delete(Game g) { DeleteTrailer(g); DeleteMusic(g); }

    // Cached cover/banner/logo images live in media/art, named after the game ("<title>_grid.png").
    // Drop the game's own images when it goes away or is renamed.
    public static void DeleteArt(Game g)
    {
        try
        {
            foreach (var f in new[] { g.Grid, g.Hero, g.Logo })
                if (f is not null) { try { File.Delete(f); } catch { } }
            foreach (var f in Directory.GetFiles(Store.Cache, g.Id + "_*")) File.Delete(f);   // older id-named art
            g.Grid = g.Hero = g.Logo = null;
        }
        catch (Exception ex) { Store.Log("Delete art failed: " + ex.Message); }
    }

    // Save downloaded cover/banner/logo bytes under media/art, named after the game. Any older copy
    // (including one with a different extension) is replaced so the folder doesn't accumulate strays.
    public static string SaveArt(Game g, string kind, byte[] data)
    {
        var dir = Dir("art");
        var stem = Stem(g) + "_" + kind;
        foreach (var old in Directory.GetFiles(dir, stem + ".*")) { try { File.Delete(old); } catch { } }
        var file = System.IO.Path.Combine(dir, stem + ImgExt(data));
        File.WriteAllBytes(file, data);
        return file;
    }

    // Sniff the image format so the cached file gets a real extension (WPF would read it either way).
    static string ImgExt(byte[] b)
    {
        if (b.Length >= 8 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return ".png";
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return ".jpg";
        if (b.Length >= 6 && b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46) return ".gif";
        if (b.Length >= 12 && b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46 &&
            b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50) return ".webp";
        if (b.Length >= 2 && b[0] == 0x42 && b[1] == 0x4D) return ".bmp";
        return ".img";
    }

    // True only for bytes that really are an image. A pasted artwork link can easily answer with an
    // HTML error page (wrong link, hotlink protection); caching that as a "logo" would leave the app
    // showing nothing at all, so those replies are rejected and logged instead.
    internal static bool LooksLikeImage(byte[] b)
    {
        int i = 0;
        while (i < b.Length && (b[i] == 0xEF || b[i] == 0xBB || b[i] == 0xBF || b[i] is 0x20 or 0x0A or 0x0D or 0x09)) i++;
        if (i < b.Length && b[i] == '<') return false;   // HTML / XML (an error page)
        return ImgExt(b) != ".img";
    }

    // Older builds named cached files after the game Id (or kept art in the old "cache" folder).
    // Rename them to "<game title>" so the folders stay readable; a stale duplicate is removed.
    public static bool Adopt(Game g)
    {
        bool changed = false;
        try
        {
            var t = AdoptOne(g.TrailerFile, Dir("trailers"), Stem(g));
            if (t is not null && t != g.TrailerFile) { g.TrailerFile = t; changed = true; }
            var m = AdoptOne(g.MusicFile, Dir("music"), Stem(g));
            if (m is not null && m != g.MusicFile) { g.MusicFile = m; changed = true; }
            var gr = AdoptArt(g.Grid, Stem(g), "grid");
            if (gr is not null && gr != g.Grid) { g.Grid = gr; changed = true; }
            var hr = AdoptArt(g.Hero, Stem(g), "hero");
            if (hr is not null && hr != g.Hero) { g.Hero = hr; changed = true; }
            var lo = AdoptArt(g.Logo, Stem(g), "logo");
            if (lo is not null && lo != g.Logo) { g.Logo = lo; changed = true; }
        }
        catch (Exception ex) { Store.Log("Adopt media failed: " + ex.Message); }
        return changed;
    }

    // Returns the title-named path for an existing art file (or the original when it can't be moved).
    static string? AdoptArt(string? file, string stem, string kind)
    {
        if (file is null || !File.Exists(file)) return file;
        var ext = System.IO.Path.GetExtension(file);
        if (string.IsNullOrWhiteSpace(ext)) return file;
        var target = System.IO.Path.Combine(Dir("art"), stem + "_" + kind + ext);
        if (string.Equals(file, target, StringComparison.OrdinalIgnoreCase)) return file;
        try
        {
            if (File.Exists(target)) { File.Delete(file); return target; }  // same title-named file already there
            File.Move(file, target);
            return target;
        }
        catch (Exception ex) { Store.Log("Adopt art failed: " + ex.Message); return file; }
    }

    // Returns the title-named path for an existing media file (or the original when it can't be renamed).
    static string? AdoptOne(string? file, string dir, string stem)
    {
        if (file is null || !File.Exists(file)) return file;
        var ext = System.IO.Path.GetExtension(file);
        if (string.IsNullOrWhiteSpace(ext)) return file;
        var target = System.IO.Path.Combine(dir, stem + ext);
        if (string.Equals(file, target, StringComparison.OrdinalIgnoreCase)) return file;
        try
        {
            if (File.Exists(target)) { File.Delete(file); return target; }  // same title-named file already there
            File.Move(file, target);
            return target;
        }
        catch (Exception ex) { Store.Log("Adopt rename failed: " + ex.Message); return file; }
    }

    // Removes media/art files no game claims any more (left behind by removals, renames, failed downloads).
    public static void Purge(IEnumerable<Game> games)
    {
        try
        {
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var artStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var g in games)
            {
                if (g.TrailerFile is not null) claimed.Add(System.IO.Path.GetFullPath(g.TrailerFile));
                if (g.MusicFile is not null) claimed.Add(System.IO.Path.GetFullPath(g.MusicFile));
                if (g.Grid is not null) claimed.Add(System.IO.Path.GetFullPath(g.Grid));
                if (g.Hero is not null) claimed.Add(System.IO.Path.GetFullPath(g.Hero));
                if (g.Logo is not null) claimed.Add(System.IO.Path.GetFullPath(g.Logo));
                titles.Add(Stem(g));
                foreach (var k in new[] { "grid", "hero", "logo" }) artStems.Add(Stem(g) + "_" + k);
            }
            foreach (var sub in new[] { "trailers", "music", "art" })
            {
                var d = Dir(sub);
                if (!Directory.Exists(d)) continue;
                foreach (var f in Directory.GetFiles(d))
                {
                    if (claimed.Contains(System.IO.Path.GetFullPath(f))) continue;
                    var noext = System.IO.Path.GetFileNameWithoutExtension(f);
                    if (titles.Contains(noext) || artStems.Contains(noext)) continue;  // in-use title name
                    try { File.Delete(f); Store.Log("Purged stale media: " + System.IO.Path.GetFileName(f)); }
                    catch { }
                }
            }
            // the old art cache is no longer used: drop anything no game still points at
            foreach (var f in Directory.GetFiles(Store.Cache))
            {
                if (claimed.Contains(System.IO.Path.GetFullPath(f))) continue;
                try { File.Delete(f); Store.Log("Purged stale media: " + System.IO.Path.GetFileName(f)); }
                catch { }
            }
        }
        catch (Exception ex) { Store.Log("Purge failed: " + ex.Message); }
    }

    // ---- Link handling: local file, direct video/audio URL, or anything yt-dlp understands (YouTube etc.) ----
    static async Task<string?> FromLink(string link, string dir, string stem, string fmt, int maxMb)
    {
        link = link.Trim().Trim('"');
        try
        {
            if (File.Exists(link))
            {
                var f = System.IO.Path.Combine(dir, stem + System.IO.Path.GetExtension(link));
                File.Copy(link, f, true);
                return f;
            }
            if (Uri.TryCreate(link, UriKind.Absolute, out var u) && u.Scheme.StartsWith("http"))
            {
                var ext = System.IO.Path.GetExtension(u.AbsolutePath).ToLowerInvariant();
                if (ext is ".mp4" or ".m4a" or ".mp3" or ".webm" or ".wav" or ".wma")
                {
                    var f = System.IO.Path.Combine(dir, stem + ext);
                    await Save(link, f);
                    return f;
                }
            }
        }
        catch (Exception ex) { Store.Log("Link download failed: " + ex.Message); }
        return await Yt(link, dir, stem, fmt, null, maxMb);
    }

    // ---- Trailer ----
    public static async Task Trailer(Game g)
    {
        await gate.WaitAsync();
        try
        {
            if (g.TrailerFile != null && File.Exists(g.TrailerFile)) return;
            var dir = Dir("trailers");
            var stem = Stem(g);
            if (!string.IsNullOrWhiteSpace(g.TrailerUrl))
                g.TrailerFile = await FromLink(g.TrailerUrl, dir, stem, TrailerFmt, 150);
            else
            {
                g.TrailerFile = await SteamTrailer(g);
                foreach (var q in new[] { "official game trailer", "game trailer" })
                    if (g.TrailerFile is null)
                        g.TrailerFile = await Yt($"ytsearch3:{Clean(g.Name)} {q}", dir, stem, TrailerFmt,
                                                 "duration>10 & duration<600", 60);
            }
            Store.Log($"Trailer for '{g.Name}': {g.TrailerFile ?? "NOT FOUND"}");
        }
        catch (Exception ex) { Store.Log("Trailer error: " + ex.Message); }
        finally { g.TrailerTried = true; Store.Save(); gate.Release(); }
    }

    static async Task<string?> SteamTrailer(Game g)
    {
        try
        {
            var d = await Meta.SteamData(g);
            var m = (d?["movies"] as JsonArray)?.FirstOrDefault();
            if (m is null) { Store.Log($"Steam: no movies listed for '{g.Name}'"); return null; }

            var urls = new List<string>();
            foreach (var k in new[] { "480", "max" })
                if (m["mp4"]?[k]?.ToString() is { Length: > 0 } direct) urls.Add(direct);

            // Steam removed direct links from its API; try the classic file names next to the thumbnail.
            var thumb = m["thumbnail"]?.ToString();
            if (thumb != null && thumb.Contains('/'))
            {
                var baseUrl = thumb[..(thumb.LastIndexOf('/') + 1)];
                foreach (var f in new[] { "movie480.mp4", "movie_max.mp4", "movie480_h264.mp4", "movie_max_h264.mp4" })
                    urls.Add(baseUrl + f);
            }

            foreach (var u0 in urls)
            {
                try
                {
                    var file = System.IO.Path.Combine(Dir("trailers"), Stem(g) + ".mp4");
                    await Save(u0.Replace("http://", "https://"), file);
                    return file;
                }
                catch { }
            }
            Store.Log($"Steam: no downloadable trailer file for '{g.Name}' (tried {urls.Count} urls)");
        }
        catch (Exception ex) { Store.Log("Steam trailer: " + ex.Message); }
        return null;
    }

    // ---- Music: your YouTube/audio link, else full track from YouTube (m4a), else 30s iTunes preview ----
    public static async Task Music(Game g)
    {
        await gate.WaitAsync();
        try
        {
            if (g.MusicFile != null && File.Exists(g.MusicFile)) return;
            var dir = Dir("music");
            var stem = Stem(g);
            if (!string.IsNullOrWhiteSpace(g.MusicUrl))
                g.MusicFile = await FromLink(g.MusicUrl, dir, stem, MusicFmt, 40);
            else
            {
                foreach (var q in new[] { "main theme OST", "soundtrack theme" })
                    if (g.MusicFile is null)
                        g.MusicFile = await Yt($"ytsearch3:{Clean(g.Name)} {q}", dir, stem, MusicFmt,
                                               "duration>45 & duration<420", 15);
                g.MusicFile ??= await Itunes(g);
            }
            Store.Log($"Music for '{g.Name}': {g.MusicFile ?? "NOT FOUND"}");
        }
        catch (Exception ex) { Store.Log("Music error: " + ex.Message); }
        finally
        {
            g.MusicTried = true;
            if (g.MusicFile is not null && File.Exists(g.MusicFile)) g.MusicVer = 1;
            Store.Save();
            gate.Release();
        }
    }

    static async Task<string?> Itunes(Game g)
    {
        try
        {
            var j = JsonNode.Parse(await H.GetStringAsync(
                $"https://itunes.apple.com/search?media=music&entity=song&limit=25&term={Uri.EscapeDataString(g.Name + " main theme")}"));
            var hits = (j?["results"] as JsonArray)?
                .Where(x => $"{x?["trackName"]} {x?["collectionName"]}".Contains(g.Name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (hits is null || hits.Count == 0) return null;
            var pick = hits.FirstOrDefault(x =>
            {
                var t = x?["trackName"]?.ToString() ?? "";
                return t.Contains("theme", StringComparison.OrdinalIgnoreCase) || t.Contains("main", StringComparison.OrdinalIgnoreCase)
                    || t.Contains("title", StringComparison.OrdinalIgnoreCase);
            }) ?? hits[0];
            var url = pick?["previewUrl"]?.ToString();
            if (url is null) return null;
            var file = System.IO.Path.Combine(Dir("music"), Stem(g) + ".m4a");
            await Save(url, file);
            return file;
        }
        catch { return null; }
    }

    // ---- yt-dlp + deno (YouTube needs a JavaScript runtime now); both auto-downloaded into %AppData%\Lumen\bin ----
    static string Clean(string s) => s.Replace("\"", "").Trim();

    static string? Find(string dir, string stem)
    {
        var files = Directory.GetFiles(dir, stem + ".*")
            .Where(f => !f.EndsWith(".part") && !f.EndsWith(".ytdl") && !f.EndsWith(".temp")).ToList();
        // Prefer the merged file (stem.mp4); a leftover ".fNNN." fragment only if merging produced nothing.
        return files.FirstOrDefault(f => System.IO.Path.GetFileNameWithoutExtension(f) == stem) ?? files.FirstOrDefault();
    }

    // Complete file whose name is exactly "<stem>.<ext>" — never a ".fNNN." fragment or partial download.
    static string? FindExact(string dir, string stem)
    {
        if (!Directory.Exists(dir)) return null;
        return Directory.GetFiles(dir, stem + ".*")
            .FirstOrDefault(f => !f.EndsWith(".part") && !f.EndsWith(".ytdl") && !f.EndsWith(".temp")
                                 && System.IO.Path.GetFileNameWithoutExtension(f) == stem);
    }

    static async Task<bool> EnsureYt()
    {
        if (File.Exists(YtPath)) return true;
        try
        {
            Directory.CreateDirectory(Bin);
            await Save("https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe", YtPath);
            return true;
        }
        catch (Exception ex) { Store.Log("yt-dlp download failed: " + ex.Message); return false; }
    }

    static async Task EnsureDeno()
    {
        if (File.Exists(DenoPath) || denoTried) return;
        denoTried = true;
        try
        {
            Directory.CreateDirectory(Bin);
            var zip = System.IO.Path.Combine(Bin, "deno.zip");
            await Save("https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip", zip);
            ZipFile.ExtractToDirectory(zip, Bin, true);
            File.Delete(zip);
        }
        catch (Exception ex) { Store.Log("deno download failed: " + ex.Message); }
    }

    // yt-dlp needs ffmpeg to merge the separate video/audio streams YouTube now serves and to
    // remux its DASH m4a audio into a container media players can actually read (the "compression noise").
    static async Task EnsureFfmpeg()
    {
        if (File.Exists(FfmpegPath) || ffmpegTried) return;
        ffmpegTried = true;
        try
        {
            Directory.CreateDirectory(Bin);
            var zip = System.IO.Path.Combine(Bin, "ffmpeg.zip");
            await Save("https://github.com/yt-dlp/FFmpeg-Builds/releases/latest/download/ffmpeg-master-latest-win64-gpl.zip", zip);
            var tmp = System.IO.Path.Combine(Bin, "ffmpeg_tmp");
            if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            ZipFile.ExtractToDirectory(zip, tmp, true);
            File.Delete(zip);
            foreach (var f in Directory.GetFiles(tmp, "ffmpeg.exe", SearchOption.AllDirectories)) File.Copy(f, FfmpegPath, true);
            foreach (var f in Directory.GetFiles(tmp, "ffprobe.exe", SearchOption.AllDirectories))
                File.Copy(f, System.IO.Path.Combine(Bin, "ffprobe.exe"), true);
            Directory.Delete(tmp, true);
            Store.Log("ffmpeg installed: " + FfmpegPath);
        }
        catch (Exception ex) { Store.Log("ffmpeg download failed: " + ex.Message); }
    }

    static async Task<int> Exec(string args, int sec)
    {
        try
        {
            var psi = new ProcessStartInfo(YtPath, args)
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi);
            if (p is null) return -1;
            var so = p.StandardOutput.ReadToEndAsync();
            var se = p.StandardError.ReadToEndAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(sec));
            try { await p.WaitForExitAsync(cts.Token); }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { }
                Store.Log("yt-dlp timed out: " + args);
                return -1;
            }
            var text = ((await so).Trim() + "\n" + (await se).Trim()).Trim();
            if (text.Length > 1500) text = text[^1500..];
            Store.Log($"yt-dlp exit {p.ExitCode}: {args}\n{text}");
            return p.ExitCode;
        }
        catch (Exception ex) { Store.Log("yt-dlp failed to start: " + ex.Message); return -1; }
    }

    // target = "ytsearch3:query" (needs a filter) or a direct link (filter = null).
    static async Task<string?> Yt(string target, string dir, string stem, string fmt, string? filter, int maxMb)
    {
        if (!await EnsureYt()) return null;
        await EnsureDeno();
        await EnsureFfmpeg();
        // File names always match the game title; only an exact, complete match counts as already-downloaded.
        if (FindExact(dir, stem) is { } already) return already;

        var js = File.Exists(DenoPath) ? $"--js-runtimes \"deno:{DenoPath}\" --remote-components ejs:github " : "";
        var ff = File.Exists(FfmpegPath) ? $"--ffmpeg-location \"{FfmpegPath}\" --merge-output-format mp4 " : "";
        var flt = filter is null ? "" : $"--match-filter \"{filter}\" --max-downloads 1 ";
        var args = $"\"{target}\" -f \"{fmt}\" {js}{flt}{ff}--max-filesize {maxMb}M --no-playlist " +
                   $"-o \"{System.IO.Path.Combine(dir, stem)}.%(ext)s\"";

        await Exec(args, 300);
        var file = Find(dir, stem);
        if (file is null && !updated)
        {
            updated = true;                 // YouTube changes often; update yt-dlp once and retry
            await Exec("-U", 120);
            await Exec(args, 300);
            file = Find(dir, stem);
        }
        return file;
    }
}
