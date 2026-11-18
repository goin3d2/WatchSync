using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Tasks;

namespace WatchSync.Tasks
{
    /// <summary>"Sync watch history (WatchSync)": a full pass for every user pair.</summary>
    public class SyncTask : IScheduledTask
    {
        public string Name => "Sync watch history (WatchSync)";
        public string Key => "WatchSyncFull";
        public string Description => "Compares every watched and in-progress title for each user pair and makes both servers match. Changes are also picked up every few minutes on their own.";
        public string Category => "WatchSync";

        public Task Execute(CancellationToken cancellationToken, IProgress<double> progress) =>
            Plugin.Instance.Engine.RunAll(true, false, cancellationToken);

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => Array.Empty<TaskTriggerInfo>();
    }

    /// <summary>Starts the polling timer once the server is up.</summary>
    public class StartHook : IServerEntryPoint
    {
        public void Run() => Plugin.Instance?.Engine.Start();
        public void Dispose() { }
    }
}
