using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WatchSync.Engine
{
    /// <summary>
    /// Keeps watched state and resume positions the same on two servers, per user pair.
    ///
    /// Each pass reads what changed on both sides (everything, on a full pass), pairs the titles up
    /// by TMDB/IMDb/TVDB id, and compares each side with what was last synced. A side that changed
    /// since then wins; when both did, the more recently played one wins. The winner's state is
    /// written to the other side, converting positions past that server's "watched" point into
    /// watched. Writes go oldest-first, so a server that stamps "last played" with the time of the
    /// write still ends up with the same Continue Watching order.
    /// </summary>
    public sealed class SyncEngine
    {
        public readonly LogRing Log;
        readonly Func<PluginConfiguration> _cfg;
        readonly Func<string, string, string, ISide> _makeSide;  // kind, url, key
        readonly string _dir;
        readonly SemaphoreSlim _run = new SemaphoreSlim(1, 1);
        readonly object _lock = new object();
        readonly Dictionary<string, PairState> _state = new Dictionary<string, PairState>();
        readonly Dictionary<string, PairStatus> _status = new Dictionary<string, PairStatus>();
        readonly Dictionary<string, ISide> _sides = new Dictionary<string, ISide>();
        Timer _poll;
        readonly Dictionary<string, Timer> _soon = new Dictionary<string, Timer>();
        string _phase = "idle";
        public bool Running => _run.CurrentCount == 0;

        public SyncEngine(Func<PluginConfiguration> cfg, Func<string, string, string, ISide> makeSide, string dataDir, LogRing log)
        {
            _cfg = cfg; _makeSide = makeSide; _dir = dataDir; Log = log;
            Directory.CreateDirectory(_dir);
            Load();
        }

        public void Start()
        {
            var minutes = Math.Max(1, _cfg().PollMinutes);
            _poll?.Dispose();
            _poll = new Timer(_ => { _ = RunAll(false, false, CancellationToken.None); }, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(minutes));
        }

        public void ConfigChanged()
        {
            lock (_sides) _sides.Clear();
            Start();
        }

        // ---- sides ---------------------------------------------------------------------------

        public ISide Side(string which)
        {
            var c = _cfg();
            var (kind, url, key) = which == "A" ? (c.SourceAKind, c.SourceAUrl, c.SourceAKey) : (c.SourceBKind, c.SourceBUrl, c.SourceBKey);
            var id = $"{kind}|{url}|{key}";
            lock (_sides)
            {
                if (!_sides.TryGetValue(id, out var s)) _sides[id] = s = _makeSide(kind, url, key);
                return s;
            }
        }

        string Signature(PluginConfiguration c, UserPair p) =>
            $"{c.SourceAKind}|{c.SourceAUrl}|{p.UserA}>{c.SourceBKind}|{c.SourceBUrl}|{p.UserB}";

        static string PairId(string sig)
        {
            using (var sha = System.Security.Cryptography.SHA1.Create())
                return BitConverter.ToString(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(sig))).Replace("-", "").Substring(0, 12).ToLowerInvariant();
        }

        // ---- state ---------------------------------------------------------------------------

        sealed class St
        {
            public bool P; public long Pos; public bool H; public long Lp;
            public static St Of(Entry e) => new St { P = e.Played, Pos = e.Played ? 0 : e.PositionMs, H = e.Hidden, Lp = e.LastPlayed };
            public bool Active => P || (Pos > 0 && !H);
            public static bool Same(St x, St y) => x.P == y.P && x.H == y.H && (x.P || Math.Abs(x.Pos - y.Pos) < 30000);
            public List<object> ToJson() => new List<object> { P, Pos, H, Lp };
            public static St FromJson(object o)
            {
                var l = Json.Arr(o);
                if (l == null || l.Count < 4) return null;
                return new St { P = Json.Truthy(l[0]), Pos = J.L(new Dictionary<string, object> { ["v"] = l[1] }, "v"), H = Json.Truthy(l[2]), Lp = J.L(new Dictionary<string, object> { ["v"] = l[3] }, "v") };
            }
        }

        sealed class Snap
        {
            public List<string> Keys = new List<string>();
            public List<string> ARefs = new List<string>(), BRefs = new List<string>();
            public St A, B;
            public string Title;
        }

        sealed class PairState
        {
            public long SinceA, SinceB, FullAt;
            public Dictionary<string, Snap> Items = new Dictionary<string, Snap>();
            public Dictionary<string, string> Alias = new Dictionary<string, string>();
            public void Index(string id, Snap s) { foreach (var k in s.Keys) Alias[k] = id; }
        }

        sealed class PairStatus
        {
            public string Id, NameA, NameB, Error, Kind;
            public long LastRun, Seconds;
            public int Failures;      // passes in a row that failed
            public long RetryAt;      // automatic passes wait until then after a failure
            public int Checked, Matched, Applied, InSync;
            public List<object> Recent = new List<object>();
            public List<object> Unmatched = new List<object>();
            public List<object> Plan = new List<object>();
        }

        string StatePath => Path.Combine(_dir, "state.json");

        void Load()
        {
            try
            {
                var d = Store.ReadJson(StatePath) as Dictionary<string, object>;
                if (d == null) return;
                foreach (var kv in d)
                {
                    var ps = new PairState { SinceA = J.L(kv.Value, "sa"), SinceB = J.L(kv.Value, "sb"), FullAt = J.L(kv.Value, "f") };
                    foreach (var it in Json.Obj(Json.Get(kv.Value, "i")) ?? new Dictionary<string, object>())
                    {
                        var s = new Snap
                        {
                            Keys = J.A(it.Value, "k").Select(Json.S).ToList(),
                            ARefs = J.A(it.Value, "ar").Select(Json.S).ToList(),
                            BRefs = J.A(it.Value, "br").Select(Json.S).ToList(),
                            A = St.FromJson(Json.Get(it.Value, "a")),
                            B = St.FromJson(Json.Get(it.Value, "b")),
                            Title = J.S(it.Value, "t"),
                        };
                        ps.Items[it.Key] = s;
                        ps.Index(it.Key, s);
                    }
                    _state[kv.Key] = ps;
                }
            }
            catch (Exception e) { Log.Warn("Couldn't read the sync state (starting fresh): " + e.Message); }
        }

        void Save()
        {
            Dictionary<string, object> d;
            lock (_lock)
                d = _state.ToDictionary(kv => kv.Key, kv => (object)new Dictionary<string, object>
                {
                    ["sa"] = kv.Value.SinceA, ["sb"] = kv.Value.SinceB, ["f"] = kv.Value.FullAt,
                    ["i"] = kv.Value.Items.ToDictionary(i => i.Key, i => (object)new Dictionary<string, object>
                    {
                        ["k"] = i.Value.Keys.Cast<object>().ToList(),
                        ["ar"] = i.Value.ARefs.Cast<object>().ToList(),
                        ["br"] = i.Value.BRefs.Cast<object>().ToList(),
                        ["a"] = i.Value.A?.ToJson(), ["b"] = i.Value.B?.ToJson(), ["t"] = i.Value.Title,
                    }),
                });
            try { Store.WriteJson(StatePath, d); } catch (Exception e) { Log.Error("Couldn't save the sync state: " + e.Message); }
        }

        public void Reset()
        {
            lock (_lock) { _state.Clear(); _status.Clear(); }
            Save();
            Log.Info("Sync state cleared; the next pass compares everything from scratch");
        }

        // ---- running -------------------------------------------------------------------------

        /// <summary>Emby saved a user's watch data: sync that user's pairs shortly (soon after a
        /// play stops or a title is marked, within a minute while playing).</summary>
        public void OnLocalUserData(string userId, bool playing)
        {
            var c = _cfg();
            foreach (var p in c.Pairs.Where(p => p.Enabled))
            {
                var local = (c.SourceAKind == "local" && Same(p.UserA, userId)) || (c.SourceBKind == "local" && Same(p.UserB, userId));
                if (!local) continue;
                var id = PairId(Signature(c, p));
                lock (_soon)
                {
                    var due = TimeSpan.FromSeconds(playing ? 60 : 10);
                    if (_soon.TryGetValue(id, out var existing))
                    {
                        // Playback stopped or the user marked something: bring a waiting run forward.
                        if (!playing) existing.Change(due, Timeout.InfiniteTimeSpan);
                        continue;
                    }
                    var pair = p;
                    _soon[id] = new Timer(_ =>
                    {
                        // Another pass is running: try again shortly instead of dropping the change.
                        if (_run.CurrentCount == 0) { lock (_soon) _soon[id]?.Change(TimeSpan.FromSeconds(15), Timeout.InfiniteTimeSpan); return; }
                        lock (_soon) { _soon[id]?.Dispose(); _soon.Remove(id); }
                        _ = RunPairs(new[] { pair }, false, false, CancellationToken.None);
                    }, null, due, Timeout.InfiniteTimeSpan);
                }
            }
        }

        static bool Same(string a, string b) => string.Equals((a ?? "").Replace("-", ""), (b ?? "").Replace("-", ""), StringComparison.OrdinalIgnoreCase);

        public Task RunAll(bool full, bool apply, CancellationToken ct, bool manual = false) =>
            RunPairs(_cfg().Pairs.Where(p => p.Enabled && p.UserA.Length > 0 && p.UserB.Length > 0).ToList(), full, apply, ct, manual);

        /// <summary><paramref name="manual"/>: asked for from the page or the scheduled task, so it runs even
        /// while automatic passes are backing off after a failure.</summary>
        public async Task RunPairs(IEnumerable<UserPair> pairs, bool full, bool apply, CancellationToken ct, bool manual = false)
        {
            if (!await _run.WaitAsync(0, ct).ConfigureAwait(false)) return;
            try
            {
                foreach (var p in pairs.ToList())
                {
                    ct.ThrowIfCancellationRequested();
                    PairStatus st;
                    lock (_lock) st = StatusOf(_cfg(), p);
                    // After a failure (a server down or too busy to answer), automatic passes wait longer
                    // each time instead of loading everything again every few minutes.
                    if (!manual && st.RetryAt > Store.Now()) continue;
                    try
                    {
                        await Cycle(p, full, apply, ct).ConfigureAwait(false);
                        lock (_lock) { st.Failures = 0; st.RetryAt = 0; }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception e)
                    {
                        int wait;
                        lock (_lock)
                        {
                            st.Error = e.Message;
                            st.Failures++;
                            wait = Math.Min(60, 5 << Math.Min(4, st.Failures - 1));
                            st.RetryAt = Store.Now() + wait * 60L;
                        }
                        Log.Error($"{p.NameA} ↔ {p.NameB}: {e.Message} (trying again in {wait} min)");
                    }
                }
            }
            finally { _phase = "idle"; _run.Release(); }
        }

        PairStatus StatusOf(PluginConfiguration c, UserPair p)
        {
            var id = PairId(Signature(c, p));
            if (!_status.TryGetValue(id, out var s)) _status[id] = s = new PairStatus { Id = id };
            s.NameA = p.NameA; s.NameB = p.NameB;
            return s;
        }

        sealed class Push
        {
            public ISide Side; public string User; public Entry Target; public Change Change;
            public long When; public string Id, Title, Dir, What;
            public St NewA, NewB;
            public Snap Snap;
            public bool Reorder;   // only re-written to keep Continue Watching in order
        }

        async Task Cycle(UserPair p, bool full, bool apply, CancellationToken ct)
        {
            var c = _cfg();
            var A = Side("A");
            var B = Side("B");
            var id = PairId(Signature(c, p));
            PairState ps;
            lock (_lock) if (!_state.TryGetValue(id, out ps)) _state[id] = ps = new PairState();
            var started = Store.Now();
            var t0 = DateTime.UtcNow;
            var dry = c.DryRun && !apply;
            var fullDue = ps.FullAt == 0 || started - ps.FullAt > Math.Max(1, c.FullSyncHours) * 3600L;
            if (dry && !full)
            {
                // Preview only plans a full pass. Polls and playback don't redo it; it's refreshed on the
                // full-sync schedule or with the Full sync button.
                PairStatus prev;
                lock (_lock) prev = StatusOf(c, p);
                if (prev.Kind == "preview" && started - prev.LastRun < Math.Max(1, c.FullSyncHours) * 3600L) return;
            }
            full = full || dry || fullDue;
            var label = $"{p.NameA} ↔ {p.NameB}";

            _phase = $"{label}: reading";
            var ea = full ? await A.States(p.UserA, ct).ConfigureAwait(false) : await A.Changed(p.UserA, ps.SinceA - 120, ct).ConfigureAwait(false);
            var eb = full ? await B.States(p.UserB, ct).ConfigureAwait(false) : await B.Changed(p.UserB, ps.SinceB - 120, ct).ConfigureAwait(false);
            Log.Debug($"{label}: read {ea.Count} on {A.Label}, {eb.Count} on {B.Label} ({(full ? "full" : "changes")})");

            // Group each side's titles by the pair's id for them (versions of a movie share one).
            var groups = new Dictionary<string, (List<Entry> a, List<Entry> b)>();
            var unmatched = new List<object>();
            string GroupId(Entry e)
            {
                foreach (var k in e.Keys) if (ps.Alias.TryGetValue(k, out var g)) return g;
                foreach (var k in e.Keys) if (groups.ContainsKey(k)) return k;
                return e.Keys.FirstOrDefault();
            }
            void Add(Entry e, bool isA)
            {
                var g = GroupId(e);
                if (g == null) { unmatched.Add(Miss(e, isA ? A : B, "no TMDB/IMDb/TVDB id")); return; }
                if (!groups.TryGetValue(g, out var l)) groups[g] = l = (new List<Entry>(), new List<Entry>());
                (isA ? l.a : l.b).Add(e);
            }
            foreach (var e in ea) Add(e, true);
            foreach (var e in eb) Add(e, false);
            if (full)
            {
                // Titles synced before that no longer show as watched or in progress: re-check them,
                // so an "unwatched" or a cleared position carries over.
                foreach (var kv in ps.Items)
                    if (!groups.ContainsKey(kv.Key) && ((kv.Value.A?.Active ?? false) || (kv.Value.B?.Active ?? false)))
                        groups[kv.Key] = (new List<Entry>(), new List<Entry>());
            }

            var pushes = new List<Push>();
            int matched = 0, inSync = 0, n = 0;
            // Titles in progress on each side after this pass, with when they were really played.
            var progA = new List<(Entry e, long lp, string id, Snap snap)>();
            var progB = new List<(Entry e, long lp, string id, Snap snap)>();
            // Keep the time a title was really played: a server that stamps writes with "now"
            // would otherwise make every synced title look just played.
            St True(St cur, St prev) => prev != null && St.Same(cur, prev) ? prev : cur;
            foreach (var g in groups)
            {
                ct.ThrowIfCancellationRequested();
                if (++n % 50 == 0) _phase = $"{label}: comparing {n} of {groups.Count}";
                ps.Items.TryGetValue(g.Key, out var snap);
                var a = g.Value.a.Count > 0 ? LocalEmby.Merge(g.Value.a) : null;
                var b = g.Value.b.Count > 0 ? LocalEmby.Merge(g.Value.b) : null;
                if (a == null) a = snap?.ARefs.Count > 0 ? await A.Get(p.UserA, snap.ARefs, ct).ConfigureAwait(false) : (b != null ? await A.Find(p.UserA, b, ct).ConfigureAwait(false) : null);
                if (b == null) b = snap?.BRefs.Count > 0 ? await B.Get(p.UserB, snap.BRefs, ct).ConfigureAwait(false) : (a != null ? await B.Find(p.UserB, a, ct).ConfigureAwait(false) : null);
                if (a == null || b == null)
                {
                    var e = a ?? b;
                    if (e != null && (St.Of(e).Active)) unmatched.Add(Miss(e, a == null ? B : A, "not found on " + (a == null ? A.Label : B.Label)));
                    continue;
                }
                matched++;
                if (snap == null) snap = new Snap();
                snap.Keys = snap.Keys.Union(a.Keys).Union(b.Keys).Distinct().ToList();
                snap.ARefs = a.Refs; snap.BRefs = b.Refs;
                snap.Title = a.Title ?? b.Title;

                var sa = St.Of(a); var sb = St.Of(b);
                var aCh = snap.A == null || !St.Same(sa, snap.A);
                var bCh = snap.B == null || !St.Same(sb, snap.B);
                var ta = True(sa, snap.A); var tb = True(sb, snap.B);
                if ((!aCh && !bCh) || Equivalent(a, b, A, B, c))
                {
                    inSync++;
                    // Same state on both: the side that stamps "now" takes the other side's time.
                    if (bCh && B.StampsNow && !A.StampsNow) tb = new St { P = sb.P, Pos = sb.Pos, H = sb.H, Lp = ta.Lp };
                    if (aCh && A.StampsNow && !B.StampsNow) ta = new St { P = sa.P, Pos = sa.Pos, H = sa.H, Lp = tb.Lp };
                    if (a.InProgress) progA.Add((a, ta.Lp, g.Key, snap));
                    if (b.InProgress) progB.Add((b, tb.Lp, g.Key, snap));
                    if (!dry) { snap.A = ta; snap.B = tb; Keep(ps, g.Key, snap); }
                    continue;
                }
                bool aWins;
                if (aCh && !bCh) aWins = true;
                else if (bCh && !aCh) aWins = false;
                // First time this title is compared: a side with nothing on it (an old "last played"
                // left behind after marking unwatched, say) doesn't win over one that has progress.
                else if (snap.A == null && snap.B == null && Rank(a) != Rank(b) && (Rank(a) == 0 || Rank(b) == 0)) aWins = Rank(a) > Rank(b);
                else aWins = Newer(a, b);
                if ((aWins && c.Direction == "btoa") || (!aWins && c.Direction == "atob"))
                {
                    if (a.InProgress) progA.Add((a, ta.Lp, g.Key, snap));
                    if (b.InProgress) progB.Add((b, tb.Lp, g.Key, snap));
                    if (!dry) { snap.A = ta; snap.B = tb; Keep(ps, g.Key, snap); }
                    continue;
                }
                var w = aWins ? a : b;
                var l = aWins ? b : a;
                var loseSide = aWins ? B : A;
                var change = MakeChange(w, l, aWins ? snap.A : snap.B, loseSide, c);
                if (change == null)
                {
                    if (a.InProgress) progA.Add((a, ta.Lp, g.Key, snap));
                    if (b.InProgress) progB.Add((b, tb.Lp, g.Key, snap));
                    if (!dry) { snap.A = ta; snap.B = tb; Keep(ps, g.Key, snap); }
                    continue;
                }
                var expected = Expected(change, l);
                if (w.InProgress) (aWins ? progA : progB).Add((w, w.LastPlayed, g.Key, snap));
                pushes.Add(new Push
                {
                    Side = loseSide, User = aWins ? p.UserB : p.UserA, Target = l, Change = change,
                    When = w.LastPlayed, Id = g.Key, Title = snap.Title, Dir = aWins ? "A→B" : "B→A",
                    What = $"{change.Why} (was {l})",
                    NewA = aWins ? sa : expected, NewB = aWins ? expected : sb, Snap = snap,
                });
            }

            // A server that stamps "now" puts whatever was written last at the front of Continue
            // Watching. When older titles are written there, re-write the ones played after them
            // (same position) so the row ends up in the true order.
            foreach (var (side, prog, user) in new[] { (A, progA, p.UserA), (B, progB, p.UserB) })
            {
                if (!side.StampsNow) continue;
                var written = pushes.Where(x => x.Side == side && !x.Change.Played && x.Change.PositionMs > 0).ToList();
                if (written.Count == 0) continue;
                var oldest = written.Min(x => x.When);
                var done = new HashSet<string>(pushes.Where(x => x.Side == side).Select(x => x.Id));
                foreach (var t in prog.Where(t => t.lp > oldest && !done.Contains(t.id)))
                    pushes.Add(new Push
                    {
                        Side = side, User = user, Target = t.e, When = t.lp, Id = t.id, Title = t.e.Title, Dir = "order",
                        Change = new Change { PositionMs = t.e.PositionMs, Seen = t.e.Seen, PlayCount = t.e.PlayCount, LastPlayed = t.lp, Why = "re-ordered" },
                        What = "re-written to keep Continue Watching in order", Reorder = true,
                        NewA = t.snap.A ?? St.Of(t.e), NewB = t.snap.B ?? St.Of(t.e), Snap = t.snap,
                    });
            }

            // Oldest first, so the newest titles end up at the front of Continue Watching.
            pushes = pushes.OrderBy(x => x.When).ThenBy(x => x.Reorder ? 1 : 0).ToList();
            int applied = 0;
            var recent = new List<object>();
            var lastStamp = new Dictionary<ISide, DateTime>();
            if (!dry)
            {
                foreach (var x in pushes)
                {
                    ct.ThrowIfCancellationRequested();
                    _phase = $"{label}: writing {applied + 1} of {pushes.Count}";
                    try
                    {
                        // A server that stamps writes keeps "last viewed" to the second: space
                        // position writes out so each one gets its own second, in order.
                        if (x.Side.StampsNow && !x.Change.Played && x.Change.PositionMs > 0)
                        {
                            if (lastStamp.TryGetValue(x.Side, out var prev))
                            {
                                var wait = prev.AddMilliseconds(1100) - DateTime.UtcNow;
                                if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
                            }
                            lastStamp[x.Side] = DateTime.UtcNow;
                        }
                        await x.Side.Apply(x.User, x.Target, x.Change, ct).ConfigureAwait(false);
                        if (x.Reorder) { Log.Debug($"{label}: {x.Title}: {x.What}"); continue; }
                        applied++;
                        x.Snap.A = x.NewA; x.Snap.B = x.NewB;
                        Keep(ps, x.Id, x.Snap);
                        recent.Add((object)Row(x, started));
                        Log.Info($"{label}: {x.Title}: {x.What} [{x.Dir}]");
                    }
                    catch (Exception e) when (!ct.IsCancellationRequested)
                    {
                        Log.Warn($"{label}: {x.Title}: couldn't write to {x.Side.Label}: {e.Message}");
                    }
                }
                ps.SinceA = ps.SinceB = started;
                if (full) ps.FullAt = started;
                Save();
            }

            lock (_lock)
            {
                var st = StatusOf(c, p);
                st.Error = null; st.LastRun = started; st.Seconds = (long)(DateTime.UtcNow - t0).TotalSeconds;
                st.Kind = dry ? "preview" : full ? "full" : "changes";
                st.Checked = groups.Count; st.Matched = matched; st.InSync = inSync; st.Applied = applied;
                st.Plan = dry ? pushes.Where(x => !x.Reorder).Take(500).Select(x => (object)Row(x, started)).ToList() : new List<object>();
                st.Recent = recent.Concat(st.Recent).Take(200).ToList();
                if (full) st.Unmatched = unmatched.Take(300).ToList();
            }
            Log.Debug($"{label}: pass done in {(DateTime.UtcNow - t0).TotalSeconds:0}s");
            if (dry) Log.Info($"{label}: preview: {pushes.Count(x => !x.Reorder)} changes to make, {matched} titles matched, {unmatched.Count} not matched (nothing written)");
            else if (full || applied > 0) Log.Info($"{label}: {(full ? "full pass" : "changes")}: {applied} written, {matched} matched, {inSync} already the same{(full ? $", {unmatched.Count} not matched" : "")}");
        }

        void Keep(PairState ps, string id, Snap s)
        {
            lock (_lock) { ps.Items[id] = s; ps.Index(id, s); }
        }

        static Dictionary<string, object> Miss(Entry e, ISide side, string why) =>
            new Dictionary<string, object> { ["title"] = e.Title, ["state"] = e.ToString(), ["why"] = why };

        static Dictionary<string, object> Row(Push x, long t) => new Dictionary<string, object>
        {
            ["t"] = t, ["title"] = x.Title, ["dir"] = x.Dir, ["what"] = x.What, ["when"] = x.When,
        };

        /// <summary>Same as far as Continue Watching and watched marks go.</summary>
        static bool Equivalent(Entry a, Entry b, ISide A, ISide B, PluginConfiguration c)
        {
            bool Done(Entry e, ISide other) => e.Played || (e.DurationMs > 0 && e.PositionMs >= e.DurationMs * other.WatchedAt);
            if (a.Played && b.Played) return true;
            var minPos = c.MinProgressSeconds * 1000L;
            bool None(Entry e) => !e.Played && (e.PositionMs < minPos || e.Hidden);
            if (None(a) && None(b)) return true;
            if (!a.Played && !b.Played && Math.Abs(a.PositionMs - b.PositionMs) < 30000 && a.Hidden == b.Hidden) return true;
            if ((a.Played && Done(b, A)) || (b.Played && Done(a, B))) return true;
            return false;
        }

        static int Rank(Entry e) => e.Played ? 2 : e.PositionMs > 0 && !e.Hidden ? 1 : 0;

        /// <summary>A wins when it was played more recently (ties: further along).</summary>
        static bool Newer(Entry a, Entry b) =>
            a.LastPlayed != b.LastPlayed ? a.LastPlayed > b.LastPlayed : Rank(a) >= Rank(b);

        static Change MakeChange(Entry w, Entry l, St prevW, ISide loseSide, PluginConfiguration c)
        {
            var minPos = c.MinProgressSeconds * 1000L;
            if (w.Played)
            {
                if (!c.SyncWatched) return null;
                return new Change { Played = true, PlayCount = Math.Max(w.PlayCount, l.PlayCount), LastPlayed = w.LastPlayed, Why = "watched" };
            }
            if (w.PositionMs >= minPos && !w.Hidden)
            {
                var dur = w.DurationMs > 0 ? w.DurationMs : l.DurationMs;
                if (dur > 0 && w.PositionMs >= dur * loseSide.WatchedAt)
                    return c.SyncWatched ? new Change { Played = true, PlayCount = Math.Max(1, l.PlayCount), LastPlayed = w.LastPlayed, Why = "watched (past " + (int)(loseSide.WatchedAt * 100) + "%)" } : null;
                if (!c.SyncProgress) return null;
                return new Change { PositionMs = w.PositionMs, Seen = w.Seen || l.Seen, LastPlayed = w.LastPlayed, PlayCount = l.PlayCount, Why = (w.Seen ? "re-watching, resume at " : "resume at ") + Fmt.Clock(w.PositionMs) };
            }
            var cleared = w.Hidden || (prevW != null && !prevW.P && prevW.Pos > 0 && w.PositionMs == 0);
            if (cleared)
            {
                if (!c.SyncRemovals || !(l.PositionMs > 0 && !l.Played && !l.Hidden)) return null;
                return new Change { RemoveFromContinueWatching = true, Why = "removed from Continue Watching" };
            }
            if (prevW != null && prevW.P && !w.Played && w.PositionMs == 0)
            {
                if (!c.SyncWatched || !l.Played) return null;
                return new Change { Played = false, PositionMs = 0, Why = "unwatched" };
            }
            return null;
        }

        static St Expected(Change c, Entry l)
        {
            if (c.RemoveFromContinueWatching) return new St { P = false, Pos = l.PositionMs, H = true, Lp = l.LastPlayed };
            if (c.Played) return new St { P = true, Lp = c.LastPlayed };
            return new St { P = false, Pos = c.PositionMs, Lp = c.LastPlayed };
        }

        // ---- page ----------------------------------------------------------------------------

        public object Status()
        {
            var c = _cfg();
            lock (_lock)
                return new Dictionary<string, object>
                {
                    ["now"] = Store.Now(),
                    ["running"] = Running,
                    ["phase"] = _phase,
                    ["dry_run"] = c.DryRun,
                    ["pairs"] = c.Pairs.Select((p, i) =>
                    {
                        _status.TryGetValue(PairId(Signature(c, p)), out var s);
                        _state.TryGetValue(PairId(Signature(c, p)), out var ps);
                        return (object)new Dictionary<string, object>
                        {
                            ["index"] = i, ["a"] = p.NameA, ["b"] = p.NameB, ["enabled"] = p.Enabled,
                            ["last"] = s?.LastRun ?? 0, ["kind"] = s?.Kind, ["seconds"] = s?.Seconds ?? 0, ["error"] = s?.Error, ["retry_at"] = s?.RetryAt ?? 0,
                            ["checked"] = s?.Checked ?? 0, ["matched"] = s?.Matched ?? 0, ["in_sync"] = s?.InSync ?? 0, ["applied"] = s?.Applied ?? 0,
                            ["tracked"] = ps?.Items.Count ?? 0, ["full_at"] = ps?.FullAt ?? 0,
                            ["plan"] = s?.Plan ?? new List<object>(), ["recent"] = (s?.Recent ?? new List<object>()).Take(50).ToList(),
                            ["unmatched"] = s?.Unmatched ?? new List<object>(),
                        };
                    }).ToList(),
                };
        }

        /// <summary>Both Continue Watching rows for a pair, side by side, with differences marked.</summary>
        public async Task<object> Compare(int index, CancellationToken ct)
        {
            var c = _cfg();
            if (index < 0 || index >= c.Pairs.Count) throw new ArgumentException("No such user pair");
            var p = c.Pairs[index];
            var A = Side("A"); var B = Side("B");
            var la = await A.ContinueWatching(p.UserA, 60, ct).ConfigureAwait(false);
            var lb = await B.ContinueWatching(p.UserB, 60, ct).ConfigureAwait(false);
            var minPos = c.MinProgressSeconds * 1000L;
            // Plex lists next episodes in Continue Watching; Emby keeps them in Next Up.
            var nextB = lb.Where(e => e.PositionMs == 0).ToList();
            var nextA = la.Where(e => e.PositionMs == 0).ToList();
            la = la.Where(e => e.PositionMs > 0).ToList();
            lb = lb.Where(e => e.PositionMs > 0).ToList();
            bool Match(Entry x, Entry y) => x.Keys.Intersect(y.Keys).Any();
            bool SameShow(Entry x, Entry y) => x.Episode && y.Episode && x.ShowKeys.Intersect(y.ShowKeys).Any();
            object Rows(List<Entry> mine, List<Entry> theirs, ISide otherSide, string otherUser)
            {
                // Order is compared among the titles both rows have.
                var common = mine.Where(e => theirs.Any(o => Match(e, o))).ToList();
                var theirCommon = theirs.Where(o => mine.Any(e => Match(e, o))).ToList();
                return mine.Select((e, i) =>
                {
                    var o = theirs.FirstOrDefault(x => Match(e, x));
                    string issue = null, note = null;
                    if (o == null)
                    {
                        var other = otherSide.OneEpisodePerShow ? theirs.FirstOrDefault(x => SameShow(e, x)) : null;
                        var newestOfShow = mine.First(x => x == e || SameShow(e, x)) == e;
                        if (other != null && !newestOfShow) note = "the other server shows one episode per show";
                        else if (other != null) issue = $"the other server shows {other.Title} for this show";
                        else if (otherSide.OneEpisodePerShow && !newestOfShow && e.Episode) note = "the other server shows one episode per show";
                        else issue = "missing on the other side";
                    }
                    else
                    {
                        var mi = common.IndexOf(e);
                        var oi = theirCommon.IndexOf(o);
                        if (Math.Abs(o.PositionMs - e.PositionMs) >= 30000) issue = $"other side at {Fmt.Clock(o.PositionMs)}";
                        else if (mi != oi) issue = $"in a different place on the other side ({oi + 1} of {theirCommon.Count} shared)";
                    }
                    return (object)new Dictionary<string, object>
                    {
                        ["title"] = e.Title, ["pos"] = Fmt.Clock(e.PositionMs), ["dur"] = e.DurationMs > 0 ? Fmt.Clock(e.DurationMs) : "",
                        ["pct"] = e.DurationMs > 0 ? (int)(100 * e.PositionMs / e.DurationMs) : 0, ["last"] = e.LastPlayed,
                        ["issue"] = issue, ["note"] = note,
                    };
                }).ToList();
            }
            return new Dictionary<string, object>
            {
                ["a"] = Rows(la, lb, B, p.UserB), ["b"] = Rows(lb, la, A, p.UserA),
                ["a_label"] = $"{A.Label}: {p.NameA}", ["b_label"] = $"{B.Label}: {p.NameB}",
                ["a_next"] = nextA.Select(e => (object)e.Title).Take(40).ToList(),
                ["b_next"] = nextB.Select(e => (object)e.Title).Take(40).ToList(),
            };
        }
    }
}
