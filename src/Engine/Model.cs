using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace WatchSync.Engine
{
    /// <summary>The external ids a title is matched by on the other server.</summary>
    public sealed class Ids
    {
        public string Tmdb, Imdb, Tvdb;                // the item itself (movie, or episode)
        public string ShowTmdb, ShowImdb, ShowTvdb;    // the episode's series
        public int? Season, Episode;
        public string Title, Year;

        static string Clean(string v)
        {
            v = (v ?? "").Trim();
            return v.Length == 0 || v == "0" ? null : v;
        }

        public Ids Normalize()
        {
            Tmdb = Clean(Tmdb); Imdb = Clean(Imdb)?.ToLowerInvariant(); Tvdb = Clean(Tvdb);
            ShowTmdb = Clean(ShowTmdb); ShowImdb = Clean(ShowImdb)?.ToLowerInvariant(); ShowTvdb = Clean(ShowTvdb);
            return this;
        }

        /// <summary>Keys in order of preference. Movies: "m:tmdb:1"; episodes: their own ids
        /// ("e:tvdb:9") and then show id + season/episode ("s:tvdb:7:1:2").</summary>
        public List<string> Keys(bool episode)
        {
            var k = new List<string>();
            if (!episode)
            {
                if (Tmdb != null) k.Add("m:tmdb:" + Tmdb);
                if (Imdb != null) k.Add("m:imdb:" + Imdb);
                if (Tvdb != null) k.Add("m:tvdb:" + Tvdb);
                return k;
            }
            if (Season != null && Episode != null)
            {
                var se = ":" + Season + ":" + Episode;
                if (ShowTvdb != null) k.Add("s:tvdb:" + ShowTvdb + se);
                if (ShowTmdb != null) k.Add("s:tmdb:" + ShowTmdb + se);
                if (ShowImdb != null) k.Add("s:imdb:" + ShowImdb + se);
            }
            if (Tvdb != null) k.Add("e:tvdb:" + Tvdb);
            if (Tmdb != null) k.Add("e:tmdb:" + Tmdb);
            if (Imdb != null) k.Add("e:imdb:" + Imdb);
            return k;
        }
    }

    /// <summary>One title's watch state for one user on one server.</summary>
    public sealed class Entry
    {
        public List<string> Refs = new List<string>(); // server's item id(s); Emby can have several versions of a movie
        public bool Episode;
        public Ids Ids = new Ids();
        public string Title;            // "Movie (2020)" or "Show S01E02"
        public bool Played;             // watched and not part-way through a re-watch
        public bool Seen;               // watched at least once (a re-watch in progress is Seen, not Played)
        public int PlayCount;
        public long PositionMs;
        public long DurationMs;
        public long LastPlayed;         // unix seconds, 0 = never
        public bool Hidden;             // removed from Continue Watching by the user
        public string Show;             // the server's id for the episode's show (Plex)

        public bool InProgress => !Played && PositionMs > 0 && !Hidden;
        public List<string> Keys => Ids.Keys(Episode);
        public List<string> ShowKeys => new[] { Ids.ShowTvdb != null ? "tvdb:" + Ids.ShowTvdb : null, Ids.ShowTmdb != null ? "tmdb:" + Ids.ShowTmdb : null, Ids.ShowImdb != null ? "imdb:" + Ids.ShowImdb : null }.Where(x => x != null).ToList();

        public Entry Clone()
        {
            var e = (Entry)MemberwiseClone();
            e.Refs = new List<string>(Refs);
            return e;
        }

        public override string ToString() =>
            Played ? "watched" : PositionMs > 0 ? (Hidden ? "hidden at " : "at ") + Fmt.Clock(PositionMs) : "unwatched";
    }

    /// <summary>What to write on the other side.</summary>
    public sealed class Change
    {
        public bool Played;
        public bool Seen;             // with a position: a re-watch, keep the watched mark
        public int PlayCount;
        public long PositionMs;       // 0 with !Played: clear progress
        public long LastPlayed;       // when the source played it (unix seconds)
        public bool RemoveFromContinueWatching;
        public string Why;
    }

    public sealed class UserInfo
    {
        public string Id, Name;
    }

    /// <summary>A server one side of the sync talks to.</summary>
    public interface ISide
    {
        string Label { get; }
        /// <summary>Writing a position stamps "last played" with the time of the write (Plex), so
        /// order in Continue Watching has to be kept by writing in order.</summary>
        bool StampsNow { get; }
        /// <summary>Continue Watching shows only the latest episode of each show (Plex).</summary>
        bool OneEpisodePerShow { get; }
        /// <summary>How far into a title counts as watched there (0..1).</summary>
        double WatchedAt { get; }
        Task<List<UserInfo>> Users(CancellationToken ct);
        /// <summary>Every title the user has watched or has in progress.</summary>
        Task<List<Entry>> States(string user, CancellationToken ct);
        /// <summary>Titles whose watch state may have changed since a time (unix seconds).</summary>
        Task<List<Entry>> Changed(string user, long since, CancellationToken ct);
        /// <summary>Find a title by its ids (any of them), with the user's current state, or null.</summary>
        Task<Entry> Find(string user, Entry other, CancellationToken ct);
        /// <summary>The current state of titles already matched, by this server's item ids, or null if gone.</summary>
        Task<Entry> Get(string user, List<string> refs, CancellationToken ct);
        Task Apply(string user, Entry target, Change change, CancellationToken ct);
        /// <summary>The user's Continue Watching row, in the order the server shows it.</summary>
        Task<List<Entry>> ContinueWatching(string user, int limit, CancellationToken ct);
        Task<string> Test(CancellationToken ct);
    }

    public static class Fmt
    {
        public static string Clock(long ms)
        {
            var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
            return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
        }

        public static string Ep(string show, int? s, int? e, string name)
        {
            var se = s != null && e != null ? $" S{s:00}E{e:00}" : "";
            return (show ?? name ?? "?") + se;
        }

        public static long Unix(DateTimeOffset? d) => d == null || d.Value.Year < 1971 ? 0 : d.Value.ToUnixTimeSeconds();
        public static long Unix(DateTime? d) => d == null || d.Value.Year < 1971 ? 0 : new DateTimeOffset(DateTime.SpecifyKind(d.Value, DateTimeKind.Utc)).ToUnixTimeSeconds();
    }
}
