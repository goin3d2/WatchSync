using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WatchSync.Engine
{
    /// <summary>JSON over HTTP for the remote servers.</summary>
    public sealed class Http
    {
        static readonly HttpClient Client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        readonly string _base;
        readonly Dictionary<string, string> _headers;

        public Http(string baseUrl, Dictionary<string, string> headers)
        {
            _base = (baseUrl ?? "").Trim().TrimEnd('/');
            _headers = headers;
        }

        public static string Q(params (string k, object v)[] p) =>
            string.Join("&", p.Where(x => x.v != null && x.v.ToString() != "")
                              .Select(x => Uri.EscapeDataString(x.k) + "=" + Uri.EscapeDataString(Convert.ToString(x.v, System.Globalization.CultureInfo.InvariantCulture))));

        public async Task<object> Send(string method, string path, object body, CancellationToken ct, Dictionary<string, string> extra = null)
        {
            using (var req = new HttpRequestMessage(new HttpMethod(method), _base + path))
            {
                foreach (var h in _headers) req.Headers.TryAddWithoutValidation(h.Key, h.Value);
                if (extra != null) foreach (var h in extra) req.Headers.TryAddWithoutValidation(h.Key, h.Value);
                req.Headers.TryAddWithoutValidation("Accept", "application/json");
                if (body != null) req.Content = new StringContent(Json.Write(body), Encoding.UTF8, "application/json");
                for (int attempt = 0; ; attempt++)
                {
                    HttpResponseMessage r;
                    try { r = await Client.SendAsync(Clone(req, attempt), ct).ConfigureAwait(false); }
                    catch (HttpRequestException) when (attempt < 2) { await Task.Delay(1500 * (attempt + 1), ct).ConfigureAwait(false); continue; }
                    using (r)
                    {
                        var code = (int)r.StatusCode;
                        if ((code == 429 || code >= 500) && attempt < 2) { await Task.Delay(2000 * (attempt + 1), ct).ConfigureAwait(false); continue; }
                        var text = r.Content == null ? "" : await r.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (code == 401 || code == 403) throw new UnauthorizedAccessException($"HTTP {code}: the key or token was refused");
                        if (code >= 400) throw new HttpRequestException($"HTTP {code} from {method} {Scrub(path)}");
                        if (string.IsNullOrWhiteSpace(text)) return null;
                        var t = text.TrimStart();
                        return t.StartsWith("{") || t.StartsWith("[") ? Json.Parse(text) : (object)text;
                    }
                }
            }
        }

        // HttpRequestMessage can only be sent once; resend a copy on retries.
        static HttpRequestMessage Clone(HttpRequestMessage req, int attempt)
        {
            if (attempt == 0) return req;
            var c = new HttpRequestMessage(req.Method, req.RequestUri);
            foreach (var h in req.Headers) c.Headers.TryAddWithoutValidation(h.Key, h.Value);
            if (req.Content != null)
            {
                var s = req.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                c.Content = new StringContent(s, Encoding.UTF8, "application/json");
            }
            return c;
        }

        static string Scrub(string path)
        {
            var i = path.IndexOf('?');
            return i < 0 ? path : path.Substring(0, i);
        }

        public Task<object> Get(string path, CancellationToken ct) => Send("GET", path, null, ct);
    }

    public static class J
    {
        public static string S(object d, string k) => Json.S(Json.Get(d, k));
        public static long L(object d, string k)
        {
            var v = Json.Get(d, k);
            if (v == null) return 0;
            long.TryParse(Json.S(v), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var n);
            if (n == 0 && double.TryParse(Json.S(v), System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var f)) n = (long)f;
            return n;
        }
        public static int? I(object d, string k) => Json.Get(d, k) == null ? (int?)null : (int)L(d, k);
        public static bool B(object d, string k) => Json.Truthy(Json.Get(d, k));
        public static List<object> A(object d, string k) => Json.Arr(Json.Get(d, k)) ?? new List<object>();
        public static long IsoUnix(string s) =>
            DateTimeOffset.TryParse(s, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var d) && d.Year > 1970 ? d.ToUnixTimeSeconds() : 0;
        public static string Iso(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ");
    }
}
