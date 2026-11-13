using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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
            _userData.UserDataSaved -= OnUserDataSaved;
        }

        private void EnsureConfigFile()
        {
            // Reading the settings creates the file (with defaults) if it does not exist yet.
            var plugin = Plugin.Instance;
            var cfg = plugin.Configuration;

            _log.Info("configuration file is {0}", plugin.ConfigurationFilePath);
            if (!GPodderClient.IsConfigured(cfg))
            {
                _log.Warn("not configured yet. Enter the server URL, username and password in Dashboard → Plugins → gPodder Sync (no restart needed).");
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

                var total = (int)((e.Item.RunTimeTicks ?? 0) / TimeSpan.TicksPerSecond);
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

                var now = DateTime.UtcNow;
                if (reason == UserDataSaveReason.PlaybackProgress)
                {
                    if (position < 5 || (_lastSent.TryGetValue(episode, out var last) && now - last < ProgressInterval))
                    {
                        return;
                    }
                }

                _lastSent[episode] = now;
                var name = e.Item.Name;
                Task.Run(() => SendAsync(cfg, name, episode, position, total));
            }
            catch (Exception ex)
            {
                _log.Warn("progress handler failed: {0}", ex.Message);
            }
        }

        private async Task SendAsync(PluginConfiguration cfg, string name, string episode, int position, int total)
        {
            try
            {
                var client = new GPodderClient(cfg);
                var feed = FeedIndex.FeedOf(episode);
                if (feed == null)
                {
                    // New episode, or the index hasn't been built yet since startup.
                    await FeedIndex.RefreshAsync(client, _log, false, CancellationToken.None).ConfigureAwait(false);
                    feed = FeedIndex.FeedOf(episode);
                }

                if (feed == null)
                {
                    _log.Info("'{0}' is not in any feed subscribed on the gpodder server; not sent ({1}).", name, FeedIndex.RedactKey(episode));
                    return;
                }

                await client.PostPlayAsync(feed, episode, position, total, CancellationToken.None).ConfigureAwait(false);
                _log.Info("sent '{0}' at {1}s of {2}s", name, position, total);
            }
            catch (Exception ex)
            {
                _log.Warn("could not send progress for '{0}': {1}", name, ex.Message);
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
