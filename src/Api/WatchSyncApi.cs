using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using WatchSync.Engine;

namespace WatchSync.Api
{
    [Route("/watchsync/status", "GET", Summary = "Sync status per user pair")]
    [Authenticated(Roles = "admin")]
    public class GetStatus : IReturn<object> { }

    [Route("/watchsync/log", "GET", Summary = "Recent log lines")]
    [Authenticated(Roles = "admin")]
    public class GetLog : IReturn<object> { public long After { get; set; } }

    [Route("/watchsync/test", "POST", Summary = "Test a source's connection and list its users")]
    [Authenticated(Roles = "admin")]
    public class TestSource : IReturn<object>
    {
        public string Side { get; set; }
        public string Kind { get; set; }
        public string Url { get; set; }
        public string Key { get; set; }
    }

    [Route("/watchsync/run", "POST", Summary = "Run a sync pass now")]
    [Authenticated(Roles = "admin")]
    public class RunNow : IReturn<object>
    {
        public bool Full { get; set; }
        public bool Apply { get; set; }
        public int Pair { get; set; } = -1;
    }

    [Route("/watchsync/compare", "GET", Summary = "Both Continue Watching rows for a user pair")]
    [Authenticated(Roles = "admin")]
    public class GetCompare : IReturn<object> { public int Pair { get; set; } }

    [Route("/watchsync/reset", "POST", Summary = "Forget what was synced so everything is compared again")]
    [Authenticated(Roles = "admin")]
    public class ResetState : IReturn<object> { }

    public class WatchSyncApi : IService, IRequiresRequest
    {
        readonly IHttpResultFactory _results;
        public IRequest Request { get; set; }

        public WatchSyncApi(IHttpResultFactory resultFactory) { _results = resultFactory; }

        static SyncEngine Engine => Plugin.Instance.Engine;

        object JsonResult(object value) =>
            _results.GetResult(Request, Json.Write(value).AsSpan(), "application/json", new Dictionary<string, string>());

        public object Get(GetStatus r) => JsonResult(Engine.Status());

        public object Get(GetLog r)
        {
            var (lines, last) = Engine.Log.Since(r.After);
            return JsonResult(new Dictionary<string, object> { ["lines"] = lines, ["last"] = last });
        }

        public async Task<object> Post(TestSource r)
        {
            try
            {
                // Test what's typed on the page (it may not be saved yet), or the saved source.
                ISide side;
                if (!string.IsNullOrEmpty(r.Kind))
                {
                    var c = Plugin.Instance.Configuration;
                    var key = string.IsNullOrEmpty(r.Key) ? (r.Side == "A" ? c.SourceAKey : c.SourceBKey) : r.Key;
                    side = Make(r.Kind, r.Url, key);
                }
                else side = Engine.Side(r.Side == "A" ? "A" : "B");
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45)))
                {
                    var msg = await side.Test(cts.Token).ConfigureAwait(false);
                    var users = await side.Users(cts.Token).ConfigureAwait(false);
                    return JsonResult(new Dictionary<string, object>
                    {
                        ["ok"] = true, ["message"] = msg,
                        ["users"] = users.Select(u => (object)new Dictionary<string, object> { ["id"] = u.Id, ["name"] = u.Name }).ToList(),
                    });
                }
            }
            catch (Exception e)
            {
                return JsonResult(new Dictionary<string, object> { ["ok"] = false, ["message"] = e.Message });
            }
        }

        static ISide Make(string kind, string url, string key)
        {
            var pct = 0.9;
            var dir = System.IO.Path.Combine(Plugin.Instance.DataFolderPath, "test");
            switch (kind)
            {
                case "local": return Plugin.Instance.Engine.Side(Plugin.Instance.Configuration.SourceAKind == "local" ? "A" : "B");
                case "emby": return new RemoteEmby(url, key, () => pct);
                case "plex": return new PlexSide(url, key, dir, () => pct, Engine.Log);
                default: throw new ArgumentException("Choose a source type");
            }
        }

        public object Post(RunNow r)
        {
            if (Engine.Running) return JsonResult(new Dictionary<string, object> { ["ok"] = false, ["message"] = "A sync pass is already running" });
            var c = Plugin.Instance.Configuration;
            var pairs = r.Pair >= 0 && r.Pair < c.Pairs.Count ? new List<UserPair> { c.Pairs[r.Pair] } : c.Pairs.Where(p => p.Enabled).ToList();
            _ = Task.Run(() => Engine.RunPairs(pairs, r.Full, r.Apply, CancellationToken.None, manual: true));
            return JsonResult(new Dictionary<string, object> { ["ok"] = true });
        }

        public async Task<object> Get(GetCompare r)
        {
            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
                    return JsonResult(await Engine.Compare(r.Pair, cts.Token).ConfigureAwait(false));
            }
            catch (Exception e)
            {
                return JsonResult(new Dictionary<string, object> { ["error"] = e.Message });
            }
        }

        public object Post(ResetState r)
        {
            Engine.Reset();
            return JsonResult(new Dictionary<string, object> { ["ok"] = true });
        }
    }
}
