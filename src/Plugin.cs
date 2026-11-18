using System;
using System.Collections.Generic;
using System.IO;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using WatchSync.Engine;

namespace WatchSync
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage
    {
        public static Plugin Instance { get; private set; }
        public SyncEngine Engine { get; }

        static readonly Guid PluginId = new Guid("5a5eb58d-11a9-44c6-af4e-c1e48794c63c");

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILogManager logManager,
                      ILibraryManager libraryManager, IUserManager userManager, IUserDataManager userDataManager)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
            var logger = logManager.GetLogger("WatchSync");
            var dataDir = Path.Combine(applicationPaths.DataPath, "watchsync");
            var log = new LogRing(Scrub, (level, message) =>
            {
                // Pass the text as an argument so braces in titles aren't read as format codes.
                if (level == "ERROR") logger.Error("{0}", message);
                else if (level == "WARNING") logger.Warn("{0}", message);
                else if (level == "DEBUG") logger.Debug("{0}", message);
                else logger.Info("{0}", message);
            });
            Engine = new SyncEngine(() => Configuration, (kind, url, key) =>
            {
                switch (kind)
                {
                    case "local": return new LocalEmby(libraryManager, userManager, userDataManager, () => Pct(Configuration.EmbyWatchedPercent));
                    case "emby": return new RemoteEmby(url, key, () => Pct(Configuration.EmbyWatchedPercent));
                    case "plex": return new PlexSide(url, key, dataDir, () => Pct(Configuration.PlexWatchedPercent), log);
                    default: throw new InvalidOperationException("Unknown source type: " + kind);
                }
            }, dataDir, log);

            userDataManager.UserDataSaved += (s, e) =>
            {
                try
                {
                    // Our own writes are saved as Import; anything else is the user playing or marking.
                    if (e.SaveReason == UserDataSaveReason.Import || e.User == null) return;
                    var playing = e.SaveReason == UserDataSaveReason.PlaybackProgress || e.SaveReason == UserDataSaveReason.PlaybackStart;
                    if (e.Item != null) LocalEmby.MarkDirty(e.User.Id.ToString("N"), e.Item.InternalId);
                    Engine.OnLocalUserData(e.User.Id.ToString("N"), playing);
                }
                catch { }
            };
        }

        static double Pct(int p) => Math.Min(99, Math.Max(50, p)) / 100.0;

        /// <summary>Keeps API keys and tokens out of the log.</summary>
        string Scrub(string s)
        {
            var c = Configuration;
            foreach (var k in new[] { c?.SourceAKey, c?.SourceBKey })
                if (!string.IsNullOrEmpty(k) && k.Length >= 6) s = s.Replace(k, "***");
            return s;
        }

        public override void UpdateConfiguration(BasePluginConfiguration configuration)
        {
            base.UpdateConfiguration(configuration);
            Engine.ConfigChanged();
        }

        public override string Name => "WatchSync";
        public override Guid Id => PluginId;
        public override string Description =>
            "Keeps watched status and Continue Watching the same between this Emby server and another " +
            "Emby or Plex server, per user.";

        public IEnumerable<PluginPageInfo> GetPages() => new[]
        {
            new PluginPageInfo
            {
                Name = "watchsync", DisplayName = "WatchSync",
                EmbeddedResourcePath = GetType().Namespace + ".Web.watchsync.html",
                EnableInMainMenu = true, MenuIcon = "sync", IsMainConfigPage = true,
            },
            new PluginPageInfo
            {
                Name = "watchsyncjs",
                EmbeddedResourcePath = GetType().Namespace + ".Web.watchsync.js",
            },
        };

        public Stream GetThumbImage() => GetType().Assembly.GetManifestResourceStream(GetType().Namespace + ".Web.thumb.png");
        public ImageFormat ThumbImageFormat => ImageFormat.Png;
    }
}
