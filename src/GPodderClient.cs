using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Emby.GPodderSync
{
    /// <summary>One "play" episode action as the gpodder v2 API sends it.</summary>
    public class PlayAction
    {
        public string Podcast { get; set; }
        public string Episode { get; set; }
        public string Device { get; set; }
        public DateTime? Timestamp { get; set; }   // UTC
        public int Position { get; set; }           // seconds
        public int Total { get; set; }              // seconds
    }

    /// <summary>
    /// The few gpodder v2 calls the plugin needs (https://gpoddernet.readthedocs.io/en/latest/api/reference/).
    /// Every call sends HTTP Basic auth, which gpodder servers accept without a login session.
    /// </summary>
    public class GPodderClient
    {
        private const string TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss";

        // One HttpClient for the lifetime of the server, as recommended. Used for RSS feeds, which may redirect.
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        // gpodder API calls don't follow redirects: HttpClient would turn a redirected POST into a GET and drop the
        // login, so the server answers 405 or 401 and the real problem (a wrong ServerUrl) stays hidden.
        private static readonly HttpClient Api = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };

        private readonly string _base;
        private readonly string _user;
        private readonly string _deviceId;
        private readonly string _device;
        private readonly AuthenticationHeaderValue _auth;

        public GPodderClient(PluginConfiguration cfg)
        {
            _base = ServerUrlOf(cfg);
            _user = Uri.EscapeDataString((cfg.Username ?? string.Empty).Trim());
            _deviceId = DeviceIdOf(cfg);
            _device = Uri.EscapeDataString(_deviceId);
            _auth = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes((cfg.Username ?? string.Empty).Trim() + ":" + (cfg.Password ?? string.Empty))));
        }

        public static bool IsConfigured(PluginConfiguration cfg) =>
            !string.IsNullOrWhiteSpace(cfg.ServerUrl) && !string.IsNullOrWhiteSpace(cfg.Username) && !string.IsNullOrEmpty(cfg.Password);

        /// <summary>ServerUrl without a trailing slash; "host:8080" is taken as "http://host:8080".</summary>
        public static string ServerUrlOf(PluginConfiguration cfg)
        {
            var url = (cfg.ServerUrl ?? string.Empty).Trim().TrimEnd('/');
            return url.Length == 0 || FeedIndex.IsHttp(url) ? url : "http://" + url;
        }

        public static string DeviceIdOf(PluginConfiguration cfg) =>
            string.IsNullOrWhiteSpace(cfg.DeviceId) ? "emby" : cfg.DeviceId.Trim();

        /// <summary>POST /api/2/devices/{user}/{device}.json — makes this Emby server show up as a device.</summary>
        public Task RegisterDeviceAsync(CancellationToken ct) =>
            SendAsync(HttpMethod.Post, $"/api/2/devices/{_user}/{_device}.json", "{\"caption\":\"Emby\",\"type\":\"server\"}", ct);

        /// <summary>GET /api/2/subscriptions/{user}.json — every feed URL subscribed on any device.</summary>
        public async Task<List<string>> GetSubscriptionsAsync(CancellationToken ct)
        {
            var body = await SendAsync(HttpMethod.Get, $"/api/2/subscriptions/{_user}.json", null, ct).ConfigureAwait(false);
            using (var doc = JsonDocument.Parse(body))
            {
                return doc.RootElement.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()).ToList();
            }
        }

        /// <summary>POST /api/2/episodes/{user}.json with one "play" action.</summary>
        public Task PostPlayAsync(string podcast, string episode, int position, int total, CancellationToken ct)
        {
            var action = new Dictionary<string, object>
            {
                ["podcast"] = podcast,
                ["episode"] = episode,
                ["device"] = _deviceId,
                ["action"] = "play",
                ["timestamp"] = DateTime.UtcNow.ToString(TimestampFormat, CultureInfo.InvariantCulture),
                ["started"] = 0,
                ["position"] = position,
                ["total"] = total,
            };
            return SendAsync(HttpMethod.Post, $"/api/2/episodes/{_user}.json", JsonSerializer.Serialize(new[] { action }), ct);
        }

        /// <summary>
        /// GET /api/2/episodes/{user}.json?since=0 — the whole action history, reduced to the latest "play" action with a
        /// position per episode. Reading from 0 every time (not from a saved cursor) means an episode Emby only learns about
        /// later still gets its progress, and a later "download" action can't hide the last play position.
        /// </summary>
        public async Task<List<PlayAction>> GetLatestPlaysAsync(CancellationToken ct)
        {
            var body = await SendAsync(HttpMethod.Get, $"/api/2/episodes/{_user}.json?since=0", null, ct).ConfigureAwait(false);
            var latest = new Dictionary<string, PlayAction>(StringComparer.Ordinal);
            using (var doc = JsonDocument.Parse(body))
            {
                if (!doc.RootElement.TryGetProperty("actions", out var actions) || actions.ValueKind != JsonValueKind.Array)
                {
                    return new List<PlayAction>();
                }

                foreach (var a in actions.EnumerateArray())
                {
                    if (!string.Equals(Str(a, "action"), "play", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var episode = Str(a, "episode");
                    var position = Int(a, "position");
                    var total = Int(a, "total");
                    if (string.IsNullOrEmpty(episode) || position == null || total == null)
                    {
                        continue;
                    }

                    var play = new PlayAction
                    {
                        Podcast = Str(a, "podcast"),
                        Episode = episode,
                        Device = Str(a, "device") ?? string.Empty,
                        Timestamp = ParseTimestamp(Str(a, "timestamp")),
                        Position = position.Value,
                        Total = total.Value,
                    };

                    // Actions arrive oldest first; an equal or missing timestamp still lets the later one win.
                    if (!latest.TryGetValue(episode, out var known) || !known.Timestamp.HasValue || !play.Timestamp.HasValue
                        || play.Timestamp.Value >= known.Timestamp.Value)
                    {
                        latest[episode] = play;
                    }
                }
            }

            return latest.Values.ToList();
        }

        /// <summary>Downloads an RSS feed (without the gpodder login: feeds may live on other hosts) and returns its enclosure URLs.</summary>
        public static async Task<List<string>> GetEnclosureUrlsAsync(string feedUrl, CancellationToken ct)
        {
            using (var response = await Http.GetAsync(feedUrl, ct).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var xml = XDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));
                return xml.Descendants("enclosure")
                    .Select(e => (string)e.Attribute("url"))
                    .Where(u => !string.IsNullOrEmpty(u))
                    .ToList();
            }
        }

        private async Task<string> SendAsync(HttpMethod method, string path, string json, CancellationToken ct)
        {
            using (var request = new HttpRequestMessage(method, _base + path))
            {
                request.Headers.Authorization = _auth;
                if (json != null)
                {
                    request.Content = new StringContent(json, Encoding.UTF8, "application/json");
                }

                using (var response = await Api.SendAsync(request, ct).ConfigureAwait(false))
                {
                    var status = (int)response.StatusCode;
                    if (status >= 300 && status < 400)
                    {
                        var target = response.Headers.Location == null
                            ? "(no Location header)"
                            : new Uri(request.RequestUri, response.Headers.Location).ToString();
                        throw new HttpRequestException(
                            $"{method} {path} was redirected (HTTP {status}) to {target}. Set ServerUrl to the address the server redirects to.");
                    }

                    var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new HttpRequestException($"{method} {path} returned HTTP {status}");
                    }

                    return body;
                }
            }
        }

        private static string Str(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        private static int? Int(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : (int?)null;

        private static DateTime? ParseTimestamp(string s)
        {
            // gpodder timestamps are UTC, normally without a zone ("2026-10-22T10:00:00").
            return DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var t)
                ? t
                : (DateTime?)null;
        }
    }
}
