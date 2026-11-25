using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Logging;

namespace Emby.GPodderSync
{
    /// <summary>
    /// Maps an episode's audio URL to the feed it comes from. gpodder clients such as AntennaPod ignore actions without
    /// a feed URL, and Emby doesn't keep the feed's address on the item, so the plugin reads the subscribed feeds itself
    /// (the subscription list on the gpodder server is the same set of feeds Emby uses).
    /// </summary>
    public static class FeedIndex
    {
        private const int MaxParallelFeeds = 6;
        private static readonly TimeSpan MinRefreshInterval = TimeSpan.FromMinutes(2);
        private static readonly SemaphoreSlim RefreshLock = new SemaphoreSlim(1, 1);

        // Last successfully read content of every feed; only touched while holding RefreshLock. A feed that fails to load
        // keeps its last known episodes instead of vanishing from the index.
        private static readonly Dictionary<string, List<FeedEnclosure>> FeedCache = new Dictionary<string, List<FeedEnclosure>>(StringComparer.Ordinal);

        // Swapped as a whole by the refresh and read without a lock.
        private static volatile Dictionary<string, string> _episodeToFeed = new Dictionary<string, string>(StringComparer.Ordinal);
        private static volatile Dictionary<string, int> _feedDurations = new Dictionary<string, int>(StringComparer.Ordinal);

        // Episode lengths reported by the gpodder server's play actions (for episodes whose feed has no duration).
        private static readonly ConcurrentDictionary<string, int> ServerTotals = new ConcurrentDictionary<string, int>(StringComparer.Ordinal);

        // URLs that were looked up and are in none of the feeds (radio and IPTV streams, other http items). Cleared each
        // time the feeds are re-read, so a new episode is found again after the next refresh.
        private static readonly ConcurrentDictionary<string, bool> Unmatched = new ConcurrentDictionary<string, bool>(StringComparer.Ordinal);

        private static DateTime _lastRefresh = DateTime.MinValue;

        public static int Count => _episodeToFeed.Count;

        public static string FeedOf(string episodeUrl) =>
            episodeUrl != null && _episodeToFeed.TryGetValue(episodeUrl, out var feed) ? feed : null;

        /// <summary>Episode length in seconds from the feed's itunes:duration or an earlier play action; 0 when unknown.</summary>
        public static int TotalOf(string episodeUrl)
        {
            if (episodeUrl == null)
            {
                return 0;
            }

            if (_feedDurations.TryGetValue(episodeUrl, out var seconds) && seconds > 0)
            {
                return seconds;
            }

            return ServerTotals.TryGetValue(episodeUrl, out seconds) ? seconds : 0;
        }

        public static void RememberTotal(string episodeUrl, int seconds)
        {
            if (!string.IsNullOrEmpty(episodeUrl) && seconds > 0)
            {
                ServerTotals[episodeUrl] = seconds;
            }
        }

        public static bool IsKnownUnmatched(string episodeUrl) => episodeUrl != null && Unmatched.ContainsKey(episodeUrl);

        public static void MarkUnmatched(string episodeUrl)
        {
            if (episodeUrl != null)
            {
                Unmatched[episodeUrl] = true;
            }
        }

        /// <summary>Re-reads every subscribed feed. Skipped if the last refresh was under two minutes ago, unless forced.</summary>
        public static async Task RefreshAsync(GPodderClient client, ILogger log, bool force, CancellationToken ct)
        {
            await RefreshLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (!force && DateTime.UtcNow - _lastRefresh < MinRefreshInterval)
                {
                    return;
                }

                _lastRefresh = DateTime.UtcNow;
                var feeds = (await client.GetSubscriptionsAsync(ct).ConfigureAwait(false)).Distinct(StringComparer.Ordinal).ToList();

                var results = new List<FeedEnclosure>[feeds.Count];
                using (var gate = new SemaphoreSlim(MaxParallelFeeds))
                {
                    var tasks = feeds.Select(async (feed, i) =>
                    {
                        await gate.WaitAsync(ct).ConfigureAwait(false);
                        try
                        {
                            results[i] = await GPodderClient.GetEnclosuresAsync(feed, ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            log.Warn("could not read feed {0}: {1}", RedactKey(feed), ex.Message);
                        }
                        finally
                        {
                            gate.Release();
                        }
                    }).ToList();

                    await Task.WhenAll(tasks).ConfigureAwait(false);
                }

                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                var durations = new Dictionary<string, int>(StringComparer.Ordinal);
                var failed = 0;
                var kept = 0;
                var current = new HashSet<string>(feeds, StringComparer.Ordinal);
                foreach (var stale in FeedCache.Keys.Where(k => !current.Contains(k)).ToList())
                {
                    FeedCache.Remove(stale);   // unsubscribed
                }

                for (var i = 0; i < feeds.Count; i++)
                {
                    if (results[i] != null)
                    {
                        FeedCache[feeds[i]] = results[i];
                    }
                    else
                    {
                        failed++;
                        if (FeedCache.ContainsKey(feeds[i]))
                        {
                            kept++;
                        }
                    }

                    if (!FeedCache.TryGetValue(feeds[i], out var enclosures))
                    {
                        continue;
                    }

                    foreach (var e in enclosures)
                    {
                        map[e.Url] = feeds[i];
                        if (e.DurationSeconds > 0)
                        {
                            durations[e.Url] = e.DurationSeconds;
                        }
                    }
                }

                _episodeToFeed = map;
                _feedDurations = durations;
                Unmatched.Clear();
                log.Info(
                    "{0} subscribed feed(s) read, {1} episode(s) known, {2} feed(s) failed ({3} of them kept from the last successful read).",
                    feeds.Count, map.Count, failed, kept);
            }
            finally
            {
                RefreshLock.Release();
            }
        }

        /// <summary>The audio URL Emby plays for a podcast episode: its path, or else its first media source's path.</summary>
        public static string EpisodeUrlOf(BaseItem item)
        {
            var path = item?.Path;
            if (IsHttp(path))
            {
                return path;
            }

            // A real file in the library (movie, TV episode, song) is never a podcast episode: skip the media source lookup
            // that every playback tick would otherwise trigger. .strm files and virtual items still go through it.
            if (!string.IsNullOrEmpty(path) && System.IO.Path.IsPathRooted(path)
                && !path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            try
            {
                var sources = item?.GetMediaSources(false, false, null);
                if (sources != null)
                {
                    foreach (var s in sources)
                    {
                        if (IsHttp(s.Path))
                        {
                            return s.Path;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Some item types can't produce media sources outside playback; they are simply not podcast episodes.
            }

            return null;
        }

        public static bool IsHttp(string s) =>
            s != null && (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

        /// <summary>Feed URLs from PodBridge carry the secret ?key=...; keep it out of the log.</summary>
        public static string RedactKey(string url)
        {
            var i = url?.IndexOf('?') ?? -1;
            return i < 0 ? url : url.Substring(0, i) + "?…";
        }
    }

    /// <summary>One enclosure of a feed: the audio URL and, if the feed states it, the episode length.</summary>
    public class FeedEnclosure
    {
        public string Url { get; set; }
        public int DurationSeconds { get; set; }
    }
}
