using System;
using System.Collections.Generic;
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
        private static readonly TimeSpan MinRefreshInterval = TimeSpan.FromMinutes(2);
        private static readonly SemaphoreSlim RefreshLock = new SemaphoreSlim(1, 1);
        private static Dictionary<string, string> _episodeToFeed = new Dictionary<string, string>(StringComparer.Ordinal);
        private static DateTime _lastRefresh = DateTime.MinValue;

        public static int Count => _episodeToFeed.Count;

        public static string FeedOf(string episodeUrl) =>
            episodeUrl != null && _episodeToFeed.TryGetValue(episodeUrl, out var feed) ? feed : null;

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
                var feeds = await client.GetSubscriptionsAsync(ct).ConfigureAwait(false);
                var map = new Dictionary<string, string>(StringComparer.Ordinal);
                var failed = 0;
                foreach (var feed in feeds)
                {
                    try
                    {
                        foreach (var url in await GPodderClient.GetEnclosureUrlsAsync(feed, ct).ConfigureAwait(false))
                        {
                            map[url] = feed;
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        failed++;
                        log.Warn("GPodderSync: could not read feed {0}: {1}", RedactKey(feed), ex.Message);
                    }
                }

                _episodeToFeed = map;
                log.Info("GPodderSync: {0} subscribed feed(s) read, {1} episode(s) known, {2} feed(s) failed.", feeds.Count, map.Count, failed);
            }
            finally
            {
                RefreshLock.Release();
            }
        }

        /// <summary>The audio URL Emby plays for a podcast episode: its path, or else its first media source's path.</summary>
        public static string EpisodeUrlOf(BaseItem item)
        {
            if (IsHttp(item?.Path))
            {
                return item.Path;
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
}
