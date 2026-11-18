using System.Collections.Generic;
using MediaBrowser.Model.Plugins;

namespace WatchSync
{
    public class UserPair
    {
        public string UserA { get; set; } = "";
        public string UserB { get; set; } = "";
        public string NameA { get; set; } = "";
        public string NameB { get; set; } = "";
        public bool Enabled { get; set; } = true;
    }

    public class PluginConfiguration : BasePluginConfiguration
    {
        // Source A and B: "local" (this Emby server), "emby" (another Emby server) or "plex".
        public string SourceAKind { get; set; } = "local";
        public string SourceAUrl { get; set; } = "";
        public string SourceAKey { get; set; } = "";
        public string SourceBKind { get; set; } = "plex";
        public string SourceBUrl { get; set; } = "";
        public string SourceBKey { get; set; } = "";

        public List<UserPair> Pairs { get; set; } = new List<UserPair>();

        // "both", "atob" or "btoa".
        public string Direction { get; set; } = "both";
        public bool SyncWatched { get; set; } = true;
        public bool SyncProgress { get; set; } = true;
        public bool SyncRemovals { get; set; } = true;
        // Preview: work out the changes but don't write anything until they're applied from the page.
        public bool DryRun { get; set; } = true;

        public int PollMinutes { get; set; } = 5;
        public int FullSyncHours { get; set; } = 6;
        // How far into a title counts as watched on each kind of server (percent).
        public int EmbyWatchedPercent { get; set; } = 90;
        public int PlexWatchedPercent { get; set; } = 90;
        // Ignore positions shorter than this (a few seconds of a trailer or an accidental start).
        public int MinProgressSeconds { get; set; } = 60;
    }
}
