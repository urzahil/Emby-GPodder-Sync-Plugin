using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace Emby.GPodderSync
{
    /// <summary>
    /// Runs at server start: makes sure the config file exists (so it can be edited) and sends Emby playback progress of
    /// podcast episodes to the gpodder server.
    /// </summary>
    public class EntryPoint : IServerEntryPoint
    {
        private static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(60);

        private readonly IUserDataManager _userData;
        private readonly ILogger _log;
        private readonly ConcurrentDictionary<string, DateTime> _lastSent = new ConcurrentDictionary<string, DateTime>(StringComparer.Ordinal);

        // Outgoing actions are sent one at a time, in the order the events happened. A newer action for an episode that is
        // still waiting replaces the older one (only the latest position matters).
        private readonly object _queueLock = new object();
        private readonly Queue<string> _order = new Queue<string>();
        private readonly Dictionary<string, PendingSend> _pending = new Dictionary<string, PendingSend>(StringComparer.Ordinal);
        private bool _draining;
        private volatile bool _disposed;

        private sealed class PendingSend
        {
            public PluginConfiguration Cfg;
            public string Name;
            public string Episode;
            public int Position;
            public int Total;
            public DateTime EventTimeUtc;
        }

        public EntryPoint(IUserDataManager userDataManager, ILogManager logManager)
        {
            _userData = userDataManager;
            _log = logManager.GetLogger("GPodderSync");
        }

        public void Run()
        {
            EnsureConfigFile();
            _userData.UserDataSaved += OnUserDataSaved;
        }

        public void Dispose()
        {
            _disposed = true;
            _userData.UserDataSaved -= OnUserDataSaved;
        }

        private void EnsureConfigFile()
        {
            var plugin = Plugin.Instance;
            if (!File.Exists(plugin.ConfigurationFilePath))
            {
                plugin.SaveConfiguration();
            }

            _log.Info("configuration file is {0}", plugin.ConfigurationFilePath);
            if (!GPodderClient.IsConfigured(plugin.Configuration))
            {
                _log.Warn("not configured yet. Stop Emby, fill in ServerUrl, Username and Password in that file, then start Emby.");
            }
        }

        // ------------------------------------------------------------ Emby → gpodder

        private void OnUserDataSaved(object sender, UserDataSaveEventArgs e)
        {
            try
            {
                // Import is what the pull uses when it applies the server's progress: never send that back.
                var reason = e.SaveReason;
                if (reason != UserDataSaveReason.PlaybackFinished && reason != UserDataSaveReason.TogglePlayed
                    && reason != UserDataSaveReason.PlaybackProgress)
                {
                    return;
                }

                var cfg = Plugin.Instance.Configuration;
                if (!GPodderClient.IsConfigured(cfg) || e.Item == null || e.UserData == null || !UserAllowed(e.User, cfg))
                {
                    return;
                }

                var episode = FeedIndex.EpisodeUrlOf(e.Item);
                if (episode == null)
                {
                    return;   // not a podcast episode played from a URL
                }

                var now = DateTime.UtcNow;   // when the event happened; sent as the action's timestamp
                var total = (int)((e.Item.RunTimeTicks ?? 0) / TimeSpan.TicksPerSecond);
                if (total <= 0)
                {
                    // Emby doesn't probe remote audio until it is played: use the feed's duration or the server's length.
                    total = FeedIndex.TotalOf(episode);
                }

                // A resume position wins (also when re-listening to an episode already marked played); a finished episode
                // has none and is sent as played to the end.
                var position = (int)(e.UserData.PlaybackPositionTicks / TimeSpan.TicksPerSecond);
                if (position <= 0 && e.UserData.Played)
                {
                    position = total;
                }
                if (total <= 0 || position <= 0)
                {
                    // Unknown length, or marked unplayed (gpodder clients can't clear progress either): nothing to send.
                    _log.Debug("nothing to send for '{0}' (position {1}s of {2}s)", e.Item.Name, position, total);
                    return;
                }

                if (reason == UserDataSaveReason.PlaybackProgress)
                {
                    if (position < 5 || (_lastSent.TryGetValue(episode, out var last) && now - last < ProgressInterval))
                    {
                        return;
                    }
                }

                _lastSent[episode] = now;
                Enqueue(new PendingSend { Cfg = cfg, Name = e.Item.Name, Episode = episode, Position = position, Total = total, EventTimeUtc = now });
            }
            catch (Exception ex)
            {
                _log.Warn("progress handler failed: {0}", ex.Message);
            }
        }

        private void Enqueue(PendingSend send)
        {
            lock (_queueLock)
            {
                if (!_pending.ContainsKey(send.Episode))
                {
                    _order.Enqueue(send.Episode);
                }

                _pending[send.Episode] = send;
                if (_draining)
                {
                    return;
                }

                _draining = true;
            }

            Task.Run(DrainAsync);
        }

        private async Task DrainAsync()
        {
            while (true)
            {
                PendingSend next;
                lock (_queueLock)
                {
                    if (_order.Count == 0 || _disposed)
                    {
                        _draining = false;
                        return;
                    }

                    var episode = _order.Dequeue();
                    next = _pending[episode];
                    _pending.Remove(episode);
                }

                if (!await SendAsync(next).ConfigureAwait(false)
                    && _lastSent.TryGetValue(next.Episode, out var stamp) && stamp == next.EventTimeUtc)
                {
                    // Not delivered: don't let the 60 s throttle suppress the next progress event's retry.
                    _lastSent.TryRemove(next.Episode, out _);
                }
            }
        }

        /// <summary>Returns false only when the send failed and is worth retrying.</summary>
        private async Task<bool> SendAsync(PendingSend s)
        {
            try
            {
                var client = new GPodderClient(s.Cfg);
                if (FeedIndex.IsKnownUnmatched(s.Episode))
                {
                    return true;   // looked up before and in no subscribed feed; stays so until the feeds are re-read
                }

                var feed = FeedIndex.FeedOf(s.Episode);
                if (feed == null)
                {
                    // New episode, or the index hasn't been built yet since startup.
                    await FeedIndex.RefreshAsync(client, _log, false, CancellationToken.None).ConfigureAwait(false);
                    feed = FeedIndex.FeedOf(s.Episode);
                }

                if (feed == null)
                {
                    FeedIndex.MarkUnmatched(s.Episode);
                    _log.Info("'{0}' is not in any feed subscribed on the gpodder server; not sent ({1}).", s.Name, FeedIndex.RedactKey(s.Episode));
                    return true;
                }

                await client.PostPlayAsync(feed, s.Episode, s.Position, s.Total, s.EventTimeUtc, CancellationToken.None).ConfigureAwait(false);
                _log.Info("sent '{0}' at {1}s of {2}s", s.Name, s.Position, s.Total);
                return true;
            }
            catch (Exception ex)
            {
                _log.Warn("could not send progress for '{0}': {1}", s.Name, ex.Message);
                return false;
            }
        }

        private static bool UserAllowed(User user, PluginConfiguration cfg)
        {
            var allowed = SyncUsers.Parse(cfg.SyncEmbyUsers);
            return allowed.Count == 0 || (user != null && allowed.Contains(user.Name));
        }
    }

    public static class SyncUsers
    {
        public static HashSet<string> Parse(string csv)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var n in (csv ?? string.Empty).Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var t = n.Trim();
                if (t.Length > 0)
                {
                    set.Add(t);
                }
            }

            return set;
        }
    }
}
