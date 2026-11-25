using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Tasks;

namespace Emby.GPodderSync
{
    /// <summary>
    /// Scheduled task (Dashboard → Scheduled Tasks → gPodder Sync): reads the latest play position of every episode from
    /// the gpodder server and applies it to the matching Emby items when it is newer than Emby's own state.
    /// </summary>
    public class SyncTask : IScheduledTask
    {
        private readonly ILibraryManager _library;
        private readonly IUserManager _users;
        private readonly IUserDataManager _userData;
        private readonly ILogger _log;

        // What this task last imported per (user, episode): the play action's timestamp and when the import happened.
        // Emby may stamp its own state with the import time, which would otherwise look like fresh listening in Emby and
        // make a later, delayed upload from another device (offline phone) lose against our own import.
        private static readonly ConcurrentDictionary<string, ImportRecord> Imports = new ConcurrentDictionary<string, ImportRecord>(StringComparer.Ordinal);
        private static readonly TimeSpan ImportStampSlack = TimeSpan.FromSeconds(10);
        private static bool _warnedPlainHttp;
        private static string _warnedFinishedValues;

        private sealed class ImportRecord
        {
            public DateTime PlayTimestamp;
            public DateTime ImportedAtUtc;
        }

        public SyncTask(ILibraryManager library, IUserManager users, IUserDataManager userData, ILogManager logManager)
        {
            _library = library;
            _users = users;
            _userData = userData;
            _log = logManager.GetLogger("GPodderSync");
        }

        public string Name => "Sync podcast progress";

        public string Key => "GPodderSyncPull";

        public string Description => "Copies listening progress from the gpodder server (AntennaPod and other devices) to Emby's podcast episodes.";

        public string Category => "gPodder Sync";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            return new[]
            {
                new TaskTriggerInfo { Type = "StartupTrigger" },
                new TaskTriggerInfo { Type = "IntervalTrigger", IntervalTicks = TimeSpan.FromMinutes(5).Ticks }
            };
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var cfg = Plugin.Instance.Configuration;
            if (!GPodderClient.IsConfigured(cfg))
            {
                _log.Warn("sync skipped: ServerUrl, Username or Password missing in the configuration file.");
                return;
            }

            WarnAboutConfiguration(cfg);
            var client = new GPodderClient(cfg);
            await client.RegisterDeviceAsync(cancellationToken).ConfigureAwait(false);
            progress.Report(10);

            // Keeps the episode → feed map fresh for the push side (new episodes appear in the feeds over time).
            await FeedIndex.RefreshAsync(client, _log, true, cancellationToken).ConfigureAwait(false);
            progress.Report(40);

            var wanted = SyncUsers.Parse(cfg.SyncEmbyUsers);
            var users = _users.GetUserList(new UserQuery()).Where(u => wanted.Count == 0 || wanted.Contains(u.Name)).ToList();
            if (users.Count == 0)
            {
                _log.Warn("no Emby user matches SyncEmbyUsers '{0}'.", cfg.SyncEmbyUsers);
                return;
            }

            var ownDevice = GPodderClient.DeviceIdOf(cfg);
            var plays = await client.GetLatestPlaysAsync(cancellationToken).ConfigureAwait(false);
            progress.Report(60);

            var items = PodcastItemsByUrl();
            var applied = 0;
            var notInEmby = 0;
            foreach (var play in plays)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FeedIndex.RememberTotal(play.Episode, play.Total);
                if (string.Equals(play.Device, ownDevice, StringComparison.OrdinalIgnoreCase))
                {
                    continue;   // the latest word on this episode came from Emby itself
                }

                if (!items.TryGetValue(play.Episode, out var item))
                {
                    notInEmby++;
                    continue;
                }

                foreach (var user in users)
                {
                    if (Apply(user, item, play, cfg))
                    {
                        applied++;
                    }
                }
            }

            _log.Info(
                "progress pull: {0} episode(s) with a play position on the server, {1} not found among Emby's {2} podcast episode(s), {3} Emby state(s) updated for {4} user(s).",
                plays.Count, notInEmby, items.Count, applied, users.Count);
            if (plays.Count > 0 && items.Count == 0)
            {
                _log.Warn("Emby has no audio or video items whose path is an http(s) URL, so nothing can be matched. Check how your podcast plugin stores episodes.");
            }

            progress.Report(100);
        }

        private void WarnAboutConfiguration(PluginConfiguration cfg)
        {
            var problems = new List<string>();
            if (cfg.FinishedRatio <= 0 || cfg.FinishedRatio > 100)
            {
                problems.Add("FinishedRatio " + cfg.FinishedRatio + " is not a fraction between 0 and 1; using 0.95.");
            }
            else if (cfg.FinishedRatio > 1)
            {
                problems.Add("FinishedRatio " + cfg.FinishedRatio + " looks like a percentage; reading it as " + cfg.FinishedRatio / 100 + ".");
            }

            if (cfg.FinishedRemainingSeconds < 0)
            {
                problems.Add("FinishedRemainingSeconds " + cfg.FinishedRemainingSeconds + " is negative; using 0.");
            }

            var report = string.Join(" ", problems);
            if (report != (_warnedFinishedValues ?? string.Empty))
            {
                _warnedFinishedValues = report;
                if (report.Length > 0)
                {
                    _log.Warn("{0}", report);
                }
            }

            if (!_warnedPlainHttp)
            {
                var url = GPodderClient.ServerUrlOf(cfg);
                if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(url, UriKind.Absolute, out var uri) && !uri.IsLoopback)
                {
                    _warnedPlainHttp = true;
                    _log.Warn("ServerUrl uses plain http:// to a non-local-host address: the gpodder password is sent unencrypted with every request.");
                }
            }
        }

        /// <summary>Emby items played straight from a URL (what a podcast plugin creates from an RSS feed), by that URL.</summary>
        private Dictionary<string, BaseItem> PodcastItemsByUrl()
        {
            var query = new InternalItemsQuery
            {
                Recursive = true,
                MediaTypes = new[] { "Audio", "Video" },   // video podcasts too
                PathStartsWithAny = new[] { "http://", "https://" },
            };

            var map = new Dictionary<string, BaseItem>(StringComparer.Ordinal);
            foreach (var item in _library.GetItemList(query))
            {
                var url = FeedIndex.EpisodeUrlOf(item);
                if (url != null)
                {
                    map[url] = item;
                }
            }

            return map;
        }

        /// <summary>Applies the server's play position to an Emby user's item state when the server's is newer. Returns true if changed.</summary>
        private bool Apply(User user, BaseItem item, PlayAction play, PluginConfiguration cfg)
        {
            var data = _userData.GetUserData(user, item);
            var key = user.Name + "\n" + play.Episode;
            var embyChanged = data.PlaystateLastModified ?? data.LastPlayedDate;
            if (embyChanged.HasValue && Imports.TryGetValue(key, out var imported)
                && embyChanged.Value.UtcDateTime <= imported.ImportedAtUtc + ImportStampSlack)
            {
                // The state was last touched by our own import: its real age is the play action's timestamp.
                embyChanged = new DateTimeOffset(imported.PlayTimestamp, TimeSpan.Zero);
            }

            // Emby only "wins" if the user really listened there. A fresh item's state timestamp (set when the item was
            // created) must not outrank older listening on another device.
            var embyHasActivity = data.Played || data.PlayCount > 0 || data.PlaybackPositionTicks > 0;
            if (embyHasActivity && embyChanged.HasValue && play.Timestamp.HasValue && embyChanged.Value.UtcDateTime >= play.Timestamp.Value)
            {
                return false;
            }

            var finished = play.Total > 0
                && (play.Position >= play.Total - GPodderClient.FinishedRemainingSecondsOf(cfg)
                    || (double)play.Position / play.Total >= GPodderClient.FinishedRatioOf(cfg));
            if (finished)
            {
                if (data.Played && data.PlaybackPositionTicks == 0)
                {
                    return false;
                }

                data.Played = true;
                data.PlaybackPositionTicks = 0;
                data.PlayCount = Math.Max(1, data.PlayCount);
            }
            else if (play.Position > 0)
            {
                var ticks = play.Position * TimeSpan.TicksPerSecond;
                if (!data.Played && Math.Abs(ticks - data.PlaybackPositionTicks) < 5 * TimeSpan.TicksPerSecond)
                {
                    return false;
                }

                data.Played = false;
                data.PlaybackPositionTicks = ticks;
            }
            else
            {
                return false;
            }

            data.LastPlayedDate = play.Timestamp.HasValue ? new DateTimeOffset(play.Timestamp.Value, TimeSpan.Zero) : DateTimeOffset.UtcNow;
            var importedAt = DateTime.UtcNow;
            _userData.SaveUserData(user, item, data, UserDataSaveReason.Import, CancellationToken.None);
            Imports[key] = new ImportRecord { PlayTimestamp = (play.Timestamp ?? importedAt), ImportedAtUtc = importedAt };
            _log.Info("'{0}' for {1}: {2} (from {3})", item.Name, user.Name, finished ? "played" : play.Position + "s", play.Device);
            return true;
        }
    }
}
