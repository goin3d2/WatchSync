using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WatchSync.Engine
{
    /// <summary>Another Emby server, through its REST API with an API key.</summary>
    public sealed class RemoteEmby : ISide
    {
        readonly Http _http;
        readonly Func<double> _watchedAt;
        // Emby 4.8+ leaves the play count and last-played date out unless asked for by name.
        const string Fields = "ProviderIds,UserData,UserDataPlayCount,UserDataLastPlayedDate,RunTimeTicks,SeriesId,SeriesName,ParentIndexNumber,IndexNumber,ProductionYear";

        public RemoteEmby(string url, string apiKey, Func<double> watchedAt)
        {
            url = (url ?? "").Trim().TrimEnd('/');
            if (!url.EndsWith("/emby", StringComparison.OrdinalIgnoreCase)) url += "/emby";
            _http = new Http(url, new Dictionary<string, string> { ["X-Emby-Token"] = (apiKey ?? "").Trim() });
            Label = "Emby at " + url;
            _watchedAt = watchedAt;
        }

        public string Label { get; }
        public bool StampsNow => false;
        public bool OneEpisodePerShow => true;   // Emby's Continue Watching shows the latest episode of each show
        public double WatchedAt => _watchedAt();

        public async Task<string> Test(CancellationToken ct)
        {
            var info = await _http.Get("/System/Info", ct).ConfigureAwait(false);
            var users = await Users(ct).ConfigureAwait(false);
            return $"{J.S(info, "ServerName")} (Emby {J.S(info, "Version")}), {users.Count} users";
        }

        public async Task<List<UserInfo>> Users(CancellationToken ct)
        {
            var a = Json.Arr(await _http.Get("/Users", ct).ConfigureAwait(false)) ?? new List<object>();
            return a.Select(u => new UserInfo { Id = J.S(u, "Id"), Name = J.S(u, "Name") }).ToList();
        }

        async Task<List<object>> Paged(string user, string query, CancellationToken ct, int max = int.MaxValue)
        {
            var all = new List<object>();
            for (int start = 0; ; start += 500)
            {
                var d = await _http.Get($"/Users/{user}/Items?{query}&Fields={Fields}&StartIndex={start}&Limit=500", ct).ConfigureAwait(false);
                var items = J.A(d, "Items");
                all.AddRange(items);
                if (items.Count < 500 || all.Count >= max || all.Count >= J.L(d, "TotalRecordCount")) break;
            }
            return all;
        }

        const string Base = "Recursive=true&IncludeItemTypes=Movie,Episode&IsMissing=false";

        public async Task<List<Entry>> States(string user, CancellationToken ct)
        {
            var played = await Paged(user, Base + "&IsPlayed=true", ct).ConfigureAwait(false);
            var resume = await Paged(user, Base + "&Filters=IsResumable", ct).ConfigureAwait(false);
            var seen = new HashSet<string>();
            var items = played.Concat(resume).Where(i => seen.Add(J.S(i, "Id"))).ToList();
            var shown = await Shown(user, ct, fresh: true).ConfigureAwait(false);
            lock (_lastResume) _lastResume[user] = new HashSet<string>(shown.ids);
            return MarkHidden(await ToEntries(items, ct).ConfigureAwait(false), shown);
        }

        readonly Dictionary<string, HashSet<string>> _lastResume = new Dictionary<string, HashSet<string>>();

        public async Task<List<Entry>> Changed(string user, long since, CancellationToken ct)
        {
            // Emby writes watch data to its database in batches, so "saved since" can miss the last
            // few minutes. Also take what was played since then, and anything that dropped out of
            // Continue Watching (removed, finished or marked) since the last look.
            var items = await Paged(user, Base + "&MinDateLastSavedForUser=" + Uri.EscapeDataString(J.Iso(Math.Max(0, since))), ct).ConfigureAwait(false);
            for (int start = 0; start < 2000; start += 100)
            {
                var d = await _http.Get($"/Users/{user}/Items?{Base}&SortBy=DatePlayed&SortOrder=Descending&Fields={Fields}&StartIndex={start}&Limit=100", ct).ConfigureAwait(false);
                var page = J.A(d, "Items");
                var recent = page.Where(i => J.IsoUnix(J.S(Json.Get(i, "UserData"), "LastPlayedDate")) >= since).ToList();
                items.AddRange(recent);
                if (page.Count < 100 || recent.Count < page.Count) break;
            }
            var shown = await Shown(user, ct, fresh: true).ConfigureAwait(false);
            var now = new HashSet<string>(shown.ids);
            items.AddRange(shown.items);
            HashSet<string> before;
            lock (_lastResume) { _lastResume.TryGetValue(user, out before); _lastResume[user] = now; }
            var gone = before == null ? new List<string>() : before.Where(id => !now.Contains(id)).ToList();
            for (int i = 0; i < gone.Count; i += 100)
            {
                var d = await _http.Get($"/Users/{user}/Items?Ids={string.Join(",", gone.Skip(i).Take(100))}&Fields={Fields}", ct).ConfigureAwait(false);
                items.AddRange(J.A(d, "Items"));
            }
            var seen = new HashSet<string>();
            return MarkHidden(await ToEntries(items.Where(i => seen.Add(J.S(i, "Id"))).ToList(), ct).ConfigureAwait(false), shown);
        }

        // The REST API doesn't say whether a title was removed from Continue Watching, so work it
        // out like for Plex: in progress, but neither it nor a later episode of its show is shown.
        readonly Dictionary<string, (long at, ResumeView v)> _shown = new Dictionary<string, (long, ResumeView)>();

        sealed class ResumeView
        {
            public List<object> items = new List<object>();
            public HashSet<string> ids = new HashSet<string>();
            public HashSet<string> shows = new HashSet<string>();
        }

        async Task<ResumeView> Shown(string user, CancellationToken ct, bool fresh = false)
        {
            lock (_shown) if (!fresh && _shown.TryGetValue(user, out var c) && Store.Now() - c.at < 20) return c.v;
            var r = await _http.Get($"/Users/{user}/Items/Resume?Limit=500&MediaTypes=Video&Fields={Fields}", ct).ConfigureAwait(false);
            var v = new ResumeView { items = J.A(r, "Items") };
            foreach (var i in v.items)
            {
                v.ids.Add(J.S(i, "Id"));
                if (J.S(i, "Type") == "Episode" && J.S(i, "SeriesId").Length > 0) v.shows.Add(J.S(i, "SeriesId"));
            }
            lock (_shown) _shown[user] = (Store.Now(), v);
            return v;
        }

        static List<Entry> MarkHidden(List<Entry> es, ResumeView shown)
        {
            foreach (var e in es)
                if (!e.Played && e.PositionMs > 0 && !e.Refs.Any(shown.ids.Contains) && !(e.Episode && e.Show != null && shown.shows.Contains(e.Show)))
                    e.Hidden = true;
            return es;
        }

        public async Task<List<Entry>> ContinueWatching(string user, int limit, CancellationToken ct)
        {
            var d = await _http.Get($"/Users/{user}/Items/Resume?Limit={limit}&MediaTypes=Video&Fields={Fields}", ct).ConfigureAwait(false);
            return await ToEntries(J.A(d, "Items"), ct).ConfigureAwait(false);
        }

        static string Pids(string tmdb, string imdb, string tvdb) =>
            string.Join(",", new[] { tmdb != null ? "tmdb." + tmdb : null, imdb != null ? "imdb." + imdb : null, tvdb != null ? "tvdb." + tvdb : null }.Where(x => x != null));

        public async Task<Entry> Find(string user, Entry other, CancellationToken ct)
        {
            var ids = other.Ids;
            var found = new List<object>();
            if (!other.Episode)
            {
                var p = Pids(ids.Tmdb, ids.Imdb, ids.Tvdb);
                // Only trust results that really carry the id, in case a server ignores the filter.
                if (p.Length > 0) found.AddRange((await Paged(user, "Recursive=true&IncludeItemTypes=Movie&AnyProviderIdEquals=" + Uri.EscapeDataString(p), ct, 50).ConfigureAwait(false)).Where(f => HasId(f, ids.Tmdb, ids.Imdb, ids.Tvdb)));
            }
            else
            {
                var sp = Pids(ids.ShowTmdb, ids.ShowImdb, ids.ShowTvdb);
                if (sp.Length > 0 && ids.Season != null && ids.Episode != null)
                {
                    var series = await Paged(user, "Recursive=true&IncludeItemTypes=Series&AnyProviderIdEquals=" + Uri.EscapeDataString(sp), ct, 10).ConfigureAwait(false);
                    foreach (var s in series.Where(s => HasId(s, ids.ShowTmdb, ids.ShowImdb, ids.ShowTvdb)))
                    {
                        // Emby's /Items ignores season and episode numbers, so ask for the season and pick the episode here.
                        var d = await _http.Get($"/Shows/{J.S(s, "Id")}/Episodes?UserId={user}&Season={ids.Season}&Fields={Fields}", ct).ConfigureAwait(false);
                        found.AddRange(J.A(d, "Items").Where(e => J.I(e, "ParentIndexNumber") == ids.Season && J.I(e, "IndexNumber") == ids.Episode));
                    }
                }
                if (found.Count == 0)
                {
                    var p = Pids(ids.Tmdb, ids.Imdb, ids.Tvdb);
                    if (p.Length > 0) found.AddRange((await Paged(user, "Recursive=true&IncludeItemTypes=Episode&AnyProviderIdEquals=" + Uri.EscapeDataString(p), ct, 20).ConfigureAwait(false)).Where(f => HasId(f, ids.Tmdb, ids.Imdb, ids.Tvdb)));
                }
            }
            if (found.Count == 0) return null;
            return LocalEmby.Merge(MarkHidden(await ToEntries(found, ct).ConfigureAwait(false), await Shown(user, ct).ConfigureAwait(false)));
        }

        static bool HasId(object item, string tmdb, string imdb, string tvdb)
        {
            var p = Json.Get(item, "ProviderIds");
            bool Eq(string name, string v) => v != null && string.Equals(Pid(p, name), v, StringComparison.OrdinalIgnoreCase);
            return Eq("Tmdb", tmdb) || Eq("Imdb", imdb) || Eq("Tvdb", tvdb);
        }

        public async Task<Entry> Get(string user, List<string> refs, CancellationToken ct)
        {
            var d = await _http.Get($"/Users/{user}/Items?Ids={string.Join(",", refs)}&Fields={Fields}", ct).ConfigureAwait(false);
            var items = J.A(d, "Items");
            return items.Count == 0 ? null : LocalEmby.Merge(MarkHidden(await ToEntries(items, ct).ConfigureAwait(false), await Shown(user, ct).ConfigureAwait(false)));
        }

        public async Task Apply(string user, Entry target, Change c, CancellationToken ct)
        {
            lock (_shown) _shown.Remove(user);
            foreach (var id in target.Refs)
            {
                if (c.RemoveFromContinueWatching)
                {
                    await _http.Send("POST", $"/Users/{user}/Items/{id}/HideFromResume?Hide=true", null, ct).ConfigureAwait(false);
                    continue;
                }
                var body = new Dictionary<string, object>
                {
                    ["Played"] = c.Played || (c.Seen && c.PositionMs > 0),
                    ["PlaybackPositionTicks"] = c.Played ? 0L : c.PositionMs * 10000,
                    ["PlayCount"] = c.Played ? Math.Max(1, c.PlayCount) : c.PlayCount,
                };
                if (c.LastPlayed > 0 && (c.Played || c.PositionMs > 0)) body["LastPlayedDate"] = J.Iso(c.LastPlayed);
                await _http.Send("POST", $"/Users/{user}/Items/{id}/UserData", body, ct).ConfigureAwait(false);
            }
        }

        readonly Dictionary<string, object> _series = new Dictionary<string, object>();

        async Task<List<Entry>> ToEntries(List<object> items, CancellationToken ct)
        {
            var need = items.Where(i => J.S(i, "Type") == "Episode").Select(i => J.S(i, "SeriesId"))
                            .Where(s => s.Length > 0 && !_series.ContainsKey(s)).Distinct().ToList();
            for (int i = 0; i < need.Count; i += 100)
            {
                var d = await _http.Get("/Items?Ids=" + string.Join(",", need.Skip(i).Take(100)) + "&Fields=ProviderIds", ct).ConfigureAwait(false);
                foreach (var s in J.A(d, "Items")) _series[J.S(s, "Id")] = s;
            }
            var o = new List<Entry>();
            foreach (var it in items)
            {
                var ud = Json.Get(it, "UserData");
                var p = Json.Get(it, "ProviderIds");
                var e = new Entry
                {
                    Episode = J.S(it, "Type") == "Episode",
                    Played = J.B(ud, "Played") && J.L(ud, "PlaybackPositionTicks") == 0,
                    Seen = J.B(ud, "Played"),
                    PlayCount = (int)J.L(ud, "PlayCount"),
                    PositionMs = J.L(ud, "PlaybackPositionTicks") / 10000,
                    DurationMs = J.L(it, "RunTimeTicks") / 10000,
                    LastPlayed = J.IsoUnix(J.S(ud, "LastPlayedDate")),
                };
                e.Refs.Add(J.S(it, "Id"));
                if (e.Episode) e.Show = J.S(it, "SeriesId");
                e.Ids.Tmdb = Pid(p, "Tmdb"); e.Ids.Imdb = Pid(p, "Imdb"); e.Ids.Tvdb = Pid(p, "Tvdb");
                if (e.Episode)
                {
                    _series.TryGetValue(J.S(it, "SeriesId"), out var s);
                    var sp = Json.Get(s, "ProviderIds");
                    e.Ids.ShowTmdb = Pid(sp, "Tmdb"); e.Ids.ShowImdb = Pid(sp, "Imdb"); e.Ids.ShowTvdb = Pid(sp, "Tvdb");
                    e.Ids.Season = J.I(it, "ParentIndexNumber"); e.Ids.Episode = J.I(it, "IndexNumber");
                    e.Title = Fmt.Ep(J.S(it, "SeriesName"), e.Ids.Season, e.Ids.Episode, J.S(it, "Name"));
                }
                else
                {
                    var y = J.S(it, "ProductionYear");
                    e.Title = y.Length > 0 ? $"{J.S(it, "Name")} ({y})" : J.S(it, "Name");
                }
                e.Ids.Normalize();
                o.Add(e);
            }
            return o;
        }

        static string Pid(object ids, string name)
        {
            if (!(ids is Dictionary<string, object> d)) return null;
            foreach (var kv in d) if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) return Json.S(kv.Value);
            return null;
        }
    }
}
