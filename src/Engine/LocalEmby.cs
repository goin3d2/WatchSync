using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;

namespace WatchSync.Engine
{
    /// <summary>The Emby server the plugin runs in, through Emby's own library and user data.</summary>
    public sealed class LocalEmby : ISide
    {
        readonly ILibraryManager _lib;
        readonly IUserManager _users;
        readonly IUserDataManager _data;
        readonly Func<double> _watchedAt;

        /// <summary>Items whose watch data Emby reported saved (some changes, like hiding a title
        /// from Continue Watching, don't move its "last saved" date), per user.</summary>
        static readonly Dictionary<string, HashSet<long>> Dirty = new Dictionary<string, HashSet<long>>();

        public static void MarkDirty(string user, long item)
        {
            lock (Dirty)
            {
                if (!Dirty.TryGetValue(user, out var s)) Dirty[user] = s = new HashSet<long>();
                s.Add(item);
            }
        }

        public LocalEmby(ILibraryManager lib, IUserManager users, IUserDataManager data, Func<double> watchedAt)
        {
            _lib = lib; _users = users; _data = data; _watchedAt = watchedAt;
        }

        public string Label => "This Emby server";
        public bool StampsNow => false;
        public bool OneEpisodePerShow => true;   // Emby's Continue Watching shows the latest episode of each show
        public double WatchedAt => _watchedAt();

        static readonly string[] Types = { "Movie", "Episode" };

        User U(string id) => _users.GetUserById(id) ?? throw new InvalidOperationException("Emby user not found: " + id);

        public Task<string> Test(CancellationToken ct) =>
            Task.FromResult($"{_users.GetUserList(new UserQuery()).Length} users");

        public Task<List<UserInfo>> Users(CancellationToken ct) =>
            Task.FromResult(_users.GetUserList(new UserQuery())
                .Select(u => new UserInfo { Id = u.Id.ToString("N"), Name = u.Name }).ToList());

        InternalItemsQuery Q(User user) => new InternalItemsQuery
        {
            User = user, IncludeItemTypes = Types, Recursive = true, IsVirtualItem = false,
        };

        public Task<List<Entry>> States(string user, CancellationToken ct)
        {
            var u = U(user);
            var seen = new HashSet<long>();
            var items = new List<BaseItem>();
            foreach (var q in new[] { Q(u).With(x => x.IsPlayed = true), Q(u).With(x => x.IsResumable = true) })
                foreach (var it in _lib.GetItemList(q, ct))
                    if (seen.Add(it.InternalId)) items.Add(it);
            // Hidden from Continue Watching but still part-watched: not "resumable", so ask for recently saved too.
            return Task.FromResult(ToEntries(u, items));
        }

        public Task<List<Entry>> Changed(string user, long since, CancellationToken ct)
        {
            var u = U(user);
            var q = Q(u);
            q.MinDateLastSavedForUser = DateTimeOffset.FromUnixTimeSeconds(Math.Max(0, since));
            var items = _lib.GetItemList(q, ct).ToList();
            HashSet<long> dirty = null;
            lock (Dirty) if (Dirty.TryGetValue(u.Id.ToString("N"), out dirty)) Dirty.Remove(u.Id.ToString("N"));
            if (dirty != null)
            {
                var have = new HashSet<long>(items.Select(i => i.InternalId));
                foreach (var id in dirty)
                    if (!have.Contains(id)) { var it = _lib.GetItemById(id); if (it != null && (it is Episode || it is MediaBrowser.Controller.Entities.Movies.Movie)) items.Add(it); }
            }
            return Task.FromResult(ToEntries(u, items));
        }

        public Task<List<Entry>> ContinueWatching(string user, int limit, CancellationToken ct)
        {
            var u = U(user);
            var q = Q(u).With(x => x.IsResumable = true);
            q.OrderBy = new[] { ValueTuple.Create("DatePlayed", SortOrder.Descending) };
            q.Limit = limit * 3 + 50;
            // Match what Emby shows: nothing removed by the user, and the latest episode of each show.
            var shows = new HashSet<string>();
            var list = ToEntries(u, _lib.GetItemList(q, ct))
                .Where(e => !e.Hidden && (!e.Episode || e.Show == null || shows.Add(e.Show)))
                .Take(limit).ToList();
            return Task.FromResult(list);
        }

        public Task<Entry> Find(string user, Entry other, CancellationToken ct)
        {
            var u = U(user);
            var ids = other.Ids;
            var found = new List<BaseItem>();
            if (!other.Episode)
            {
                var pids = Pids(ids.Tmdb, ids.Imdb, ids.Tvdb);
                if (pids.Count > 0)
                    found.AddRange(_lib.GetItemList(new InternalItemsQuery
                    { IncludeItemTypes = new[] { "Movie" }, Recursive = true, IsVirtualItem = false, AnyProviderIdEquals = pids }, ct));
            }
            else
            {
                if (ids.Season != null && ids.Episode != null)
                {
                    var spids = Pids(ids.ShowTmdb, ids.ShowImdb, ids.ShowTvdb);
                    if (spids.Count > 0)
                    {
                        var series = _lib.GetItemList(new InternalItemsQuery
                        { IncludeItemTypes = new[] { "Series" }, Recursive = true, AnyProviderIdEquals = spids }, ct);
                        if (series.Length > 0)
                            found.AddRange(_lib.GetItemList(new InternalItemsQuery
                            {
                                IncludeItemTypes = new[] { "Episode" }, Recursive = true, IsVirtualItem = false,
                                SeriesIds = series.Select(s => s.InternalId).ToArray(),
                                ParentIndexNumber = ids.Season, IndexNumber = ids.Episode,
                            }, ct));
                    }
                }
                if (found.Count == 0)
                {
                    var epids = Pids(ids.Tmdb, ids.Imdb, ids.Tvdb);
                    if (epids.Count > 0)
                        found.AddRange(_lib.GetItemList(new InternalItemsQuery
                        { IncludeItemTypes = new[] { "Episode" }, Recursive = true, IsVirtualItem = false, AnyProviderIdEquals = epids }, ct));
                }
            }
            if (found.Count == 0) return Task.FromResult<Entry>(null);
            var entries = ToEntries(u, found);
            return Task.FromResult(Merge(entries));
        }

        public Task<Entry> Get(string user, List<string> refs, CancellationToken ct)
        {
            var u = U(user);
            var items = refs.Select(r => long.TryParse(r, out var id) ? _lib.GetItemById(id) : null).Where(i => i != null).ToList();
            return Task.FromResult(items.Count == 0 ? null : Merge(ToEntries(u, items)));
        }

        /// <summary>Several versions of one title: one entry with all their ids, showing the most-watched state.</summary>
        public static Entry Merge(List<Entry> entries)
        {
            var best = entries.OrderByDescending(e => e.Played).ThenByDescending(e => e.LastPlayed).ThenByDescending(e => e.PositionMs).First().Clone();
            best.Refs = entries.SelectMany(e => e.Refs).Distinct().ToList();
            best.PlayCount = entries.Max(e => e.PlayCount);
            return best;
        }

        public Task Apply(string user, Entry target, Change c, CancellationToken ct)
        {
            var u = U(user);
            foreach (var r in target.Refs)
            {
                var item = _lib.GetItemById(long.Parse(r));
                if (item == null) continue;
                var ud = _data.GetUserData(u, item);
                if (c.RemoveFromContinueWatching)
                {
                    ud.HideFromResume = true;
                }
                else if (c.Played)
                {
                    ud.Played = true;
                    ud.PlaybackPositionTicks = 0;
                    ud.HideFromResume = false;
                    ud.PlayCount = Math.Max(Math.Max(ud.PlayCount, c.PlayCount), 1);
                    if (c.LastPlayed > 0) ud.LastPlayedDate = DateTimeOffset.FromUnixTimeSeconds(c.LastPlayed);
                }
                else if (c.PositionMs > 0)
                {
                    ud.Played = c.Seen;
                    ud.PlaybackPositionTicks = c.PositionMs * 10000;
                    ud.HideFromResume = false;
                    if (c.LastPlayed > 0) ud.LastPlayedDate = DateTimeOffset.FromUnixTimeSeconds(c.LastPlayed);
                }
                else
                {
                    ud.Played = false;
                    ud.PlaybackPositionTicks = 0;
                }
                _data.SaveUserData(u, item, ud, UserDataSaveReason.Import, ct);
            }
            return Task.CompletedTask;
        }

        static List<KeyValuePair<string, string>> Pids(string tmdb, string imdb, string tvdb)
        {
            var l = new List<KeyValuePair<string, string>>();
            if (tmdb != null) l.Add(new KeyValuePair<string, string>("Tmdb", tmdb));
            if (imdb != null) l.Add(new KeyValuePair<string, string>("Imdb", imdb));
            if (tvdb != null) l.Add(new KeyValuePair<string, string>("Tvdb", tvdb));
            return l;
        }

        static string Pid(BaseItem item, string name) => item?.ProviderIds != null && item.ProviderIds.TryGetValue(name, out var v) ? v : null;

        List<Entry> ToEntries(User u, IEnumerable<BaseItem> items)
        {
            var series = new Dictionary<long, BaseItem>();
            var o = new List<Entry>();
            foreach (var it in items)
            {
                var ud = _data.GetUserData(u, it);
                var e = new Entry
                {
                    Episode = it is Episode,
                    Played = ud.Played && ud.PlaybackPositionTicks == 0,
                    Seen = ud.Played,
                    PlayCount = ud.PlayCount,
                    PositionMs = ud.PlaybackPositionTicks / 10000,
                    DurationMs = (it.RunTimeTicks ?? 0) / 10000,
                    LastPlayed = Fmt.Unix(ud.LastPlayedDate),
                    Hidden = ud.HideFromResume,
                };
                e.Refs.Add(it.InternalId.ToString());
                e.Ids.Tmdb = Pid(it, "Tmdb"); e.Ids.Imdb = Pid(it, "Imdb"); e.Ids.Tvdb = Pid(it, "Tvdb");
                if (e.Episode)
                {
                    e.Show = it.SeriesId.ToString();
                    if (!series.TryGetValue(it.SeriesId, out var s)) series[it.SeriesId] = s = it.SeriesId != 0 ? _lib.GetItemById(it.SeriesId) : null;
                    e.Ids.ShowTmdb = Pid(s, "Tmdb"); e.Ids.ShowImdb = Pid(s, "Imdb"); e.Ids.ShowTvdb = Pid(s, "Tvdb");
                    e.Ids.Season = it.ParentIndexNumber; e.Ids.Episode = it.IndexNumber;
                    e.Title = Fmt.Ep(s?.Name, it.ParentIndexNumber, it.IndexNumber, it.Name);
                }
                else e.Title = it.ProductionYear != null ? $"{it.Name} ({it.ProductionYear})" : it.Name;
                e.Ids.Normalize();
                o.Add(e);
            }
            return o;
        }
    }

    static class QueryExt
    {
        public static InternalItemsQuery With(this InternalItemsQuery q, Action<InternalItemsQuery> a) { a(q); return q; }
    }
}
