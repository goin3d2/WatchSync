using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace WatchSync.Engine
{
    /// <summary>
    /// A Plex server. The server owner uses the token given in the settings; users the server is
    /// shared with get their own token from plex.tv (the owner's token can list them). Titles are
    /// found through an index of the library by TMDB/IMDb/TVDB id, kept on disk and topped up
    /// with newly added titles, since Plex can't look up by external id.
    /// </summary>
    public sealed class PlexSide : ISide
    {
        const string Ident = "com.plexapp.plugins.library";
        const string Exclude = "&excludeElements=Media,Genre,Country,Director,Writer,Role,Collection,Label,Image,UltraBlurColors,Rating,Marker,Chapter" +
                               "&excludeFields=summary,tagline,art,thumb,studio,contentRating,originalTitle,titleSort,chapterSource,audienceRatingImage,ratingImage";
        readonly string _url, _token, _dataDir;
        readonly Func<double> _watchedAt;
        readonly LogRing _log;
        readonly Http _owner;
        readonly SemaphoreSlim _indexLock = new SemaphoreSlim(1, 1);
        readonly Dictionary<string, Http> _userHttp = new Dictionary<string, Http>();
        Dictionary<string, string> _sharedTokens;   // plex.tv user id -> server access token
        long _sharedAt;
        string _machine;
        List<(string key, string type)> _sections;
        Index _index;
        readonly Dictionary<string, Ids> _showIds = new Dictionary<string, Ids>();
        readonly Dictionary<string, (long at, List<object> eps)> _episodes = new Dictionary<string, (long, List<object>)>();

        public PlexSide(string url, string token, string dataDir, Func<double> watchedAt, LogRing log)
        {
            _url = (url ?? "").Trim().TrimEnd('/');
            _token = (token ?? "").Trim();
            _dataDir = dataDir; _watchedAt = watchedAt; _log = log;
            _owner = Client(_token);
            Label = "Plex at " + _url;
        }

        public string Label { get; }
        public bool StampsNow => true;
        public bool OneEpisodePerShow => true;
        public double WatchedAt => _watchedAt();

        Http Client(string token) => new Http(_url, new Dictionary<string, string>
        {
            ["X-Plex-Token"] = token,
            ["X-Plex-Client-Identifier"] = "watchsync-emby-plugin",
            ["X-Plex-Product"] = "WatchSync",
        });

        static object MC(object d) => Json.Get(d, "MediaContainer");

        async Task<string> Machine(CancellationToken ct) =>
            _machine ?? (_machine = J.S(MC(await _owner.Get("/identity", ct).ConfigureAwait(false)), "machineIdentifier"));

        public async Task<string> Test(CancellationToken ct)
        {
            var id = MC(await _owner.Get("/", ct).ConfigureAwait(false));
            var users = await Users(ct).ConfigureAwait(false);
            return $"{J.S(id, "friendlyName")} (Plex {J.S(id, "version")}), {users.Count} users";
        }

        public async Task<List<UserInfo>> Users(CancellationToken ct)
        {
            var a = J.A(MC(await _owner.Get("/accounts", ct).ConfigureAwait(false)), "Account");
            var shared = await SharedTokens(ct).ConfigureAwait(false);
            var o = new List<UserInfo>();
            foreach (var x in a)
            {
                var id = J.S(x, "id");
                if (id == "0" || id.Length == 0) continue;
                var name = J.S(x, "name");
                if (id == "1") name += " (owner)";
                else if (!shared.ContainsKey(id)) name += " (no access token)";
                o.Add(new UserInfo { Id = id, Name = name });
            }
            return o;
        }

        /// <summary>Server tokens for users the server is shared with, from plex.tv (cached for an hour).</summary>
        async Task<Dictionary<string, string>> SharedTokens(CancellationToken ct)
        {
            if (_sharedTokens != null && Store.Now() - _sharedAt < 3600) return _sharedTokens;
            var map = new Dictionary<string, string>();
            try
            {
                var plex = new Http("https://plex.tv", new Dictionary<string, string> { ["X-Plex-Token"] = _token, ["X-Plex-Client-Identifier"] = "watchsync-emby-plugin" });
                var xml = await plex.Send("GET", $"/api/servers/{await Machine(ct).ConfigureAwait(false)}/shared_servers", null, ct,
                                          new Dictionary<string, string> { ["Accept"] = "application/xml" }).ConfigureAwait(false) as string;
                if (!string.IsNullOrEmpty(xml))
                    foreach (var s in XDocument.Parse(xml).Descendants("SharedServer"))
                    {
                        var uid = (string)s.Attribute("userID");
                        var tok = (string)s.Attribute("accessToken");
                        if (!string.IsNullOrEmpty(uid) && !string.IsNullOrEmpty(tok)) map[uid] = tok;
                    }
            }
            catch (Exception e) { _log.Warn("Plex: couldn't get shared users' tokens from plex.tv: " + e.Message); }
            _sharedTokens = map; _sharedAt = Store.Now();
            return map;
        }

        async Task<Http> For(string user, CancellationToken ct)
        {
            if (user == "1" || string.IsNullOrEmpty(user)) return _owner;
            lock (_userHttp) if (_userHttp.TryGetValue(user, out var h)) return h;
            var shared = await SharedTokens(ct).ConfigureAwait(false);
            if (!shared.TryGetValue(user, out var tok))
                throw new InvalidOperationException("No Plex access token for this user (only the owner and users the server is shared with can be synced)");
            var c = Client(tok);
            lock (_userHttp) _userHttp[user] = c;
            return c;
        }

        async Task<List<(string key, string type)>> Sections(CancellationToken ct)
        {
            if (_sections != null) return _sections;
            var d = J.A(MC(await _owner.Get("/library/sections", ct).ConfigureAwait(false)), "Directory");
            return _sections = d.Where(s => J.S(s, "type") == "movie" || J.S(s, "type") == "show")
                                .Select(s => (J.S(s, "key"), J.S(s, "type"))).ToList();
        }

        async Task<List<object>> Paged(Http h, string path, CancellationToken ct, Func<List<object>, bool> stop = null, int page = 500)
        {
            var all = new List<object>();
            for (int start = 0; ; start += page)
            {
                var sep = path.Contains("?") ? "&" : "?";
                var d = MC(await h.Get($"{path}{sep}X-Plex-Container-Start={start}&X-Plex-Container-Size={page}", ct).ConfigureAwait(false));
                var items = J.A(d, "Metadata");
                all.AddRange(items);
                if (items.Count < page || (stop != null && stop(items))) break;
                var total = J.L(d, "totalSize");
                if (total > 0 && all.Count >= total) break;
            }
            return all;
        }

        public async Task<List<Entry>> States(string user, CancellationToken ct)
        {
            var h = await For(user, ct).ConfigureAwait(false);
            var items = new List<object>();
            foreach (var (key, type) in await Sections(ct).ConfigureAwait(false))
            {
                var t = type == "movie" ? 1 : 4;
                items.AddRange(await Paged(h, $"/library/sections/{key}/all?type={t}&unwatched=0&includeGuids=1{Exclude}", ct).ConfigureAwait(false));
                items.AddRange(await Paged(h, $"/library/sections/{key}/all?type={t}&inProgress=1&includeGuids=1{Exclude}", ct).ConfigureAwait(false));
            }
            var seen = new HashSet<string>();
            return MarkHidden(await ToEntries(items.Where(i => seen.Add(J.S(i, "ratingKey"))).ToList(), ct).ConfigureAwait(false),
                              await Shown(h, ct).ConfigureAwait(false));
        }

        /// <summary>What Plex's Continue Watching shows: titles, and shows (for their episodes).</summary>
        readonly Dictionary<Http, (DateTime at, (HashSet<string>, HashSet<string>) v)> _shown = new Dictionary<Http, (DateTime, (HashSet<string>, HashSet<string>))>();

        async Task<(HashSet<string> keys, HashSet<string> shows)> Shown(Http h, CancellationToken ct)
        {
            lock (_shown) if (_shown.TryGetValue(h, out var c) && DateTime.UtcNow - c.at < TimeSpan.FromSeconds(20)) return c.v;
            var v = await FetchShown(h, ct).ConfigureAwait(false);
            lock (_shown) _shown[h] = (DateTime.UtcNow, v);
            return v;
        }

        /// <summary>Forget the cached Continue Watching row after writing, so the next read sees the change.</summary>
        void ForgetShown(Http h) { lock (_shown) _shown.Remove(h); }

        async Task<(HashSet<string> keys, HashSet<string> shows)> FetchShown(Http h, CancellationToken ct)
        {
            var d = MC(await h.Get("/hubs/continueWatching/items?X-Plex-Container-Size=500", ct).ConfigureAwait(false));
            var keys = new HashSet<string>(); var shows = new HashSet<string>();
            foreach (var m in J.A(d, "Metadata"))
            {
                keys.Add(J.S(m, "ratingKey"));
                var show = J.S(m, "grandparentRatingKey");
                if (show.Length > 0) shows.Add(show);
            }
            return (keys, shows);
        }

        /// <summary>Plex keeps the position of a title removed from Continue Watching, so a title
        /// in progress that Continue Watching doesn't list counts as removed. Other episodes of a
        /// show it does list are just Plex showing one episode per show.</summary>
        List<Entry> MarkHidden(List<Entry> es, (HashSet<string> keys, HashSet<string> shows) shown)
        {
            foreach (var e in es)
                if (!e.Played && e.PositionMs > 0 && !shown.keys.Contains(e.Refs[0]) && !(e.Episode && e.Show != null && shown.shows.Contains(e.Show)))
                    e.Hidden = true;
            return es;
        }

        public async Task<List<Entry>> Changed(string user, long since, CancellationToken ct)
        {
            var h = await For(user, ct).ConfigureAwait(false);
            var items = new List<object>();
            foreach (var (key, type) in await Sections(ct).ConfigureAwait(false))
            {
                var t = type == "movie" ? 1 : 4;
                var got = await Paged(h, $"/library/sections/{key}/all?type={t}&sort=lastViewedAt:desc&includeGuids=1{Exclude}", ct,
                                      page => J.L(page[page.Count - 1], "lastViewedAt") < since, 100).ConfigureAwait(false);
                items.AddRange(got.Where(m => J.L(m, "lastViewedAt") >= since));
            }
            return MarkHidden(await ToEntries(items, ct).ConfigureAwait(false), await Shown(h, ct).ConfigureAwait(false));
        }

        public async Task<List<Entry>> ContinueWatching(string user, int limit, CancellationToken ct)
        {
            var h = await For(user, ct).ConfigureAwait(false);
            var d = MC(await h.Get($"/hubs/continueWatching/items?X-Plex-Container-Size={limit}&includeGuids=1", ct).ConfigureAwait(false));
            return await ToEntries(J.A(d, "Metadata").Where(m => J.S(m, "type") == "movie" || J.S(m, "type") == "episode").ToList(), ct).ConfigureAwait(false);
        }

        public async Task<Entry> Find(string user, Entry other, CancellationToken ct)
        {
            var idx = await GetIndex(ct).ConfigureAwait(false);
            var keys = new List<string>();
            if (!other.Episode)
            {
                foreach (var k in other.Keys) if (idx.Movies.TryGetValue(k, out var rks)) { keys.AddRange(rks); break; }
            }
            else
            {
                var ids = other.Ids;
                var showKeys = new[] { ids.ShowTvdb != null ? "tvdb:" + ids.ShowTvdb : null, ids.ShowTmdb != null ? "tmdb:" + ids.ShowTmdb : null, ids.ShowImdb != null ? "imdb:" + ids.ShowImdb : null };
                string show = null;
                foreach (var k in showKeys) if (k != null && idx.Shows.TryGetValue(k, out var rk)) { show = rk.FirstOrDefault(); break; }
                if (show != null)
                {
                    var eps = await Episodes(show, ct).ConfigureAwait(false);
                    var hit = eps.FirstOrDefault(m => ids.Season != null && J.I(m, "parentIndex") == ids.Season && J.I(m, "index") == ids.Episode)
                           ?? eps.FirstOrDefault(m => Guids(m).Overlaps(new[] { ids.Tvdb != null ? "tvdb://" + ids.Tvdb : "-", ids.Tmdb != null ? "tmdb://" + ids.Tmdb : "-", ids.Imdb != null ? "imdb://" + ids.Imdb : "-" }));
                    if (hit != null) keys.Add(J.S(hit, "ratingKey"));
                }
            }
            if (keys.Count == 0) return null;
            var h = await For(user, ct).ConfigureAwait(false);
            var d = MC(await h.Get($"/library/metadata/{string.Join(",", keys.Distinct())}?includeGuids=1", ct).ConfigureAwait(false));
            var es = MarkHidden(await ToEntries(J.A(d, "Metadata"), ct).ConfigureAwait(false), await Shown(h, ct).ConfigureAwait(false));
            return es.Count == 0 ? null : LocalEmby.Merge(es);
        }

        public async Task<Entry> Get(string user, List<string> refs, CancellationToken ct)
        {
            var h = await For(user, ct).ConfigureAwait(false);
            object d;
            try { d = MC(await h.Get($"/library/metadata/{string.Join(",", refs)}?includeGuids=1", ct).ConfigureAwait(false)); }
            catch (HttpRequestException) { return null; }   // 404: removed from Plex
            var es = MarkHidden(await ToEntries(J.A(d, "Metadata"), ct).ConfigureAwait(false), await Shown(h, ct).ConfigureAwait(false));
            return es.Count == 0 ? null : LocalEmby.Merge(es);
        }

        async Task<List<object>> Episodes(string show, CancellationToken ct)
        {
            lock (_episodes) if (_episodes.TryGetValue(show, out var c) && Store.Now() - c.at < 600) return c.eps;
            var d = MC(await _owner.Get($"/library/metadata/{show}/allLeaves?includeGuids=1{Exclude}", ct).ConfigureAwait(false));
            var eps = J.A(d, "Metadata");
            lock (_episodes) _episodes[show] = (Store.Now(), eps);
            return eps;
        }

        public async Task Apply(string user, Entry target, Change c, CancellationToken ct)
        {
            var h = await For(user, ct).ConfigureAwait(false);
            ForgetShown(h);
            foreach (var rk in target.Refs)
            {
                if (c.RemoveFromContinueWatching)
                    await h.Send("PUT", $"/actions/removeFromContinueWatching?ratingKey={rk}", null, ct).ConfigureAwait(false);
                else if (c.Played)
                    await h.Get($"/:/scrobble?key={rk}&identifier={Ident}", ct).ConfigureAwait(false);
                else if (c.PositionMs > 0)
                {
                    // A re-watch keeps the watched mark; a fresh resume point clears it.
                    if (target.Played && !c.Seen) await h.Get($"/:/unscrobble?key={rk}&identifier={Ident}", ct).ConfigureAwait(false);
                    // Plex ignores a write that doesn't move the position (and then keeps the old
                    // "last viewed" time), so nudge it by a second first when it's the same.
                    if (Math.Abs(target.PositionMs - c.PositionMs) < 1000)
                        await h.Get($"/:/progress?key={rk}&identifier={Ident}&time={c.PositionMs + 1000}&state=stopped", ct).ConfigureAwait(false);
                    await h.Get($"/:/progress?key={rk}&identifier={Ident}&time={c.PositionMs}&state=stopped", ct).ConfigureAwait(false);
                }
                else
                    await h.Get($"/:/unscrobble?key={rk}&identifier={Ident}", ct).ConfigureAwait(false);
            }
        }

        static HashSet<string> Guids(object m) =>
            new HashSet<string>(J.A(m, "Guid").Select(g => J.S(g, "id").ToLowerInvariant()));

        static void FromGuids(object m, out string tmdb, out string imdb, out string tvdb)
        {
            tmdb = imdb = tvdb = null;
            foreach (var g in Guids(m))
            {
                if (g.StartsWith("tmdb://")) tmdb = g.Substring(7);
                else if (g.StartsWith("imdb://")) imdb = g.Substring(7);
                else if (g.StartsWith("tvdb://")) tvdb = g.Substring(7);
            }
        }

        async Task<List<Entry>> ToEntries(List<object> items, CancellationToken ct)
        {
            var need = items.Where(m => J.S(m, "type") == "episode").Select(m => J.S(m, "grandparentRatingKey"))
                            .Where(k => k.Length > 0 && !_showIds.ContainsKey(k)).Distinct().ToList();
            for (int i = 0; i < need.Count; i += 50)
            {
                var d = MC(await _owner.Get($"/library/metadata/{string.Join(",", need.Skip(i).Take(50))}?includeGuids=1{Exclude}", ct).ConfigureAwait(false));
                foreach (var s in J.A(d, "Metadata"))
                {
                    FromGuids(s, out var t, out var im, out var tv);
                    _showIds[J.S(s, "ratingKey")] = new Ids { ShowTmdb = t, ShowImdb = im, ShowTvdb = tv };
                }
            }
            var o = new List<Entry>();
            foreach (var m in items)
            {
                var type = J.S(m, "type");
                if (type != "movie" && type != "episode") continue;
                var offset = J.L(m, "viewOffset");
                var count = (int)J.L(m, "viewCount");
                var e = new Entry
                {
                    Episode = type == "episode",
                    PlayCount = count,
                    PositionMs = offset,
                    Seen = count > 0,
                    Played = count > 0 && offset == 0,   // a re-watch in progress counts as in progress
                    DurationMs = J.L(m, "duration"),
                    LastPlayed = J.L(m, "lastViewedAt"),
                };
                e.Refs.Add(J.S(m, "ratingKey"));
                if (type == "episode") e.Show = J.S(m, "grandparentRatingKey");
                FromGuids(m, out e.Ids.Tmdb, out e.Ids.Imdb, out e.Ids.Tvdb);
                if (e.Episode)
                {
                    if (_showIds.TryGetValue(J.S(m, "grandparentRatingKey"), out var s))
                    { e.Ids.ShowTmdb = s.ShowTmdb; e.Ids.ShowImdb = s.ShowImdb; e.Ids.ShowTvdb = s.ShowTvdb; }
                    e.Ids.Season = J.I(m, "parentIndex"); e.Ids.Episode = J.I(m, "index");
                    e.Title = Fmt.Ep(J.S(m, "grandparentTitle"), e.Ids.Season, e.Ids.Episode, J.S(m, "title"));
                }
                else
                {
                    var y = J.S(m, "year");
                    e.Title = y.Length > 0 ? $"{J.S(m, "title")} ({y})" : J.S(m, "title");
                }
                e.Ids.Normalize();
                o.Add(e);
            }
            return o;
        }

        // ---- index of the library by external id -------------------------------------------

        sealed class Index
        {
            public Dictionary<string, List<string>> Movies = new Dictionary<string, List<string>>();  // "m:tmdb:1" -> ratingKeys
            public Dictionary<string, List<string>> Shows = new Dictionary<string, List<string>>();   // "tvdb:1" -> ratingKeys
            public Dictionary<string, long> Newest = new Dictionary<string, long>();                    // section -> newest addedAt
            public long Checked;
        }

        string IndexPath => Path.Combine(_dataDir, "plex-index-" + (_machine ?? "server") + ".json");

        async Task<Index> GetIndex(CancellationToken ct)
        {
            if (_index != null && Store.Now() - _index.Checked < 900) return _index;
            await _indexLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_index != null && Store.Now() - _index.Checked < 900) return _index;
                await Machine(ct).ConfigureAwait(false);
                var idx = _index ?? Load();
                var sections = await Sections(ct).ConfigureAwait(false);
                var started = DateTime.UtcNow;
                int added = 0;
                foreach (var (key, type) in sections)
                {
                    idx.Newest.TryGetValue(key, out var newest);
                    var t = type == "movie" ? 1 : 2;
                    var path = $"/library/sections/{key}/all?type={t}&sort=addedAt:desc&includeGuids=1{Exclude}";
                    var items = await Paged(_owner, path, ct, page => J.L(page[page.Count - 1], "addedAt") < newest, newest == 0 ? 1000 : 200).ConfigureAwait(false);
                    foreach (var m in items)
                    {
                        if (J.L(m, "addedAt") < newest) continue;
                        FromGuids(m, out var tm, out var im, out var tv);
                        var rk = J.S(m, "ratingKey");
                        var map = type == "movie" ? idx.Movies : idx.Shows;
                        var prefix = type == "movie" ? "m:" : "";
                        foreach (var k in new[] { tm != null ? "tmdb:" + tm : null, im != null ? "imdb:" + im : null, tv != null ? "tvdb:" + tv : null })
                        {
                            if (k == null) continue;
                            if (!map.TryGetValue(prefix + k, out var l)) map[prefix + k] = l = new List<string>();
                            if (!l.Contains(rk)) l.Add(rk);
                        }
                        added++;
                    }
                    if (items.Count > 0) idx.Newest[key] = Math.Max(newest, items.Max(m => J.L(m, "addedAt")));
                }
                idx.Checked = Store.Now();
                if (added > 0)
                {
                    _log.Info($"Plex index: {added} titles added ({idx.Movies.Count} movie ids, {idx.Shows.Count} show ids) in {(DateTime.UtcNow - started).TotalSeconds:0}s");
                    Save(idx);
                }
                return _index = idx;
            }
            finally { _indexLock.Release(); }
        }

        Index Load()
        {
            try
            {
                var d = Store.ReadJson(IndexPath) as Dictionary<string, object>;
                if (d == null) return new Index();
                var i = new Index();
                foreach (var kv in Json.Obj(d["m"]) ?? new Dictionary<string, object>()) i.Movies[kv.Key] = (Json.Arr(kv.Value) ?? new List<object>()).Select(Json.S).ToList();
                foreach (var kv in Json.Obj(d["s"]) ?? new Dictionary<string, object>()) i.Shows[kv.Key] = (Json.Arr(kv.Value) ?? new List<object>()).Select(Json.S).ToList();
                foreach (var kv in Json.Obj(d["n"]) ?? new Dictionary<string, object>()) i.Newest[kv.Key] = J.L(d["n"], kv.Key);
                return i;
            }
            catch { return new Index(); }
        }

        void Save(Index i)
        {
            Store.WriteJson(IndexPath, new Dictionary<string, object>
            {
                ["m"] = i.Movies.ToDictionary(k => k.Key, k => (object)k.Value.Cast<object>().ToList()),
                ["s"] = i.Shows.ToDictionary(k => k.Key, k => (object)k.Value.Cast<object>().ToList()),
                ["n"] = i.Newest.ToDictionary(k => k.Key, k => (object)k.Value),
            });
        }

        /// <summary>Forget the index so it's rebuilt from scratch (after titles were removed from Plex).</summary>
        public void ResetIndex() { _index = new Index(); try { File.Delete(IndexPath); } catch { } }
    }
}
