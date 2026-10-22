using MediaBrowser.Model.Plugins;

namespace Emby.GPodderSync
{
    /// <summary>
    /// Stored by Emby as XML (plugins/configurations/Emby.GPodderSync.xml). There is no settings page:
    /// edit that file while Emby is stopped, then start Emby. A default file is written on first start.
    /// The password is kept in plain text in that file; the plugin never logs it.
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>Address of the gpodder server, no trailing slash, e.g. http://homeserver:8080.</summary>
        public string ServerUrl { get; set; } = string.Empty;

        /// <summary>gpodder login (for PodBridge: GPODDER_USERNAME).</summary>
        public string Username { get; set; } = string.Empty;

        /// <summary>gpodder password (for PodBridge: GPODDER_PASSWORD).</summary>
        public string Password { get; set; } = string.Empty;

        /// <summary>Device id this Emby server uses on the gpodder server.</summary>
        public string DeviceId { get; set; } = "emby";

        /// <summary>Comma-separated Emby user names whose progress is synced. Empty = all users.</summary>
        public string SyncEmbyUsers { get; set; } = string.Empty;

        /// <summary>An episode counts as finished when this many seconds or less are left...</summary>
        public int FinishedRemainingSeconds { get; set; } = 30;

        /// <summary>...or when at least this fraction of it was played.</summary>
        public double FinishedRatio { get; set; } = 0.95;
    }
}
