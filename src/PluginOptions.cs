using System;
using System.ComponentModel;
using Emby.Web.GenericEdit;
using Emby.Web.GenericEdit.Validation;
using MediaBrowser.Model.Attributes;

namespace Emby.GPodderSync
{
    /// <summary>
    /// What the settings page (Dashboard → Plugins → gPodder Sync) shows. Emby builds the page from these properties and
    /// their attributes. It is only a view: values are copied from and to <see cref="PluginConfiguration"/>, which is what
    /// is stored (as XML) and what the rest of the plugin reads.
    /// </summary>
    public class PluginOptions : EditableOptionsBase
    {
        public override string EditorTitle => "gPodder Sync";

        public override string EditorDescription =>
            "Settings are stored in plugins/configurations/Emby.GPodderSync.xml and take effect as soon as you save; "
            + "a sync then starts automatically.";

        [DisplayName("gpodder server")]
        public ServerOptions Server { get; set; } = new ServerOptions();

        [DisplayName("Sync")]
        public SyncOptions Sync { get; set; } = new SyncOptions();

        public static PluginOptions FromConfiguration(PluginConfiguration c)
        {
            return new PluginOptions
            {
                Server = new ServerOptions
                {
                    ServerUrl = c.ServerUrl,
                    Username = c.Username,
                    Password = c.Password,
                    DeviceId = c.DeviceId
                },
                Sync = new SyncOptions
                {
                    SyncEmbyUsers = c.SyncEmbyUsers,
                    FinishedRemainingSeconds = c.FinishedRemainingSeconds,
                    FinishedPercent = (int)Math.Round(c.FinishedRatio * 100)
                }
            };
        }

        /// <summary>A new configuration with the page's values.</summary>
        public PluginConfiguration ToConfiguration()
        {
            var s = Server ?? new ServerOptions();
            var y = Sync ?? new SyncOptions();
            return new PluginConfiguration
            {
                ServerUrl = Trim(s.ServerUrl).TrimEnd('/'),
                Username = Trim(s.Username),
                Password = s.Password ?? string.Empty,
                DeviceId = string.IsNullOrWhiteSpace(s.DeviceId) ? "emby" : s.DeviceId.Trim(),
                SyncEmbyUsers = Trim(y.SyncEmbyUsers),
                FinishedRemainingSeconds = y.FinishedRemainingSeconds,
                FinishedRatio = y.FinishedPercent / 100.0
            };
        }

        protected override void Validate(ValidationContext context)
        {
            base.Validate(context);

            var url = Server?.ServerUrl?.Trim();
            if (!string.IsNullOrEmpty(url))
            {
                // "host:8080" is accepted and taken as http://host:8080, as in the XML file.
                var full = FeedIndex.IsHttp(url) ? url : "http://" + url;
                if (!Uri.TryCreate(full, UriKind.Absolute, out var uri)
                    || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                {
                    context.AddValidationError("Server URL must be an http:// or https:// address, e.g. http://homeserver:8080.");
                }
            }
        }

        private static string Trim(string s) => (s ?? string.Empty).Trim();

        // ------------------------------------------------------------ groups

        public class ServerOptions : EditableObjectBase
        {
            public override string EditorTitle => "gpodder server";

            public override string EditorDescription =>
                "The gpodder v2 server AntennaPod syncs with (for example PodBridge). The password is stored in plain text in "
                + "the XML file; it is never logged.";

            [DisplayName("Server URL")]
            [Description("Address of the gpodder server as Emby reaches it, e.g. http://homeserver:8080. Use the exact address the "
                + "server answers on: redirects (say from http:// to https://) are not followed.")]
            public string ServerUrl { get; set; } = string.Empty;

            [DisplayName("Username")]
            [Description("gpodder login (for PodBridge: GPODDER_USERNAME).")]
            public string Username { get; set; } = string.Empty;

            [DisplayName("Password")]
            [Description("gpodder password (for PodBridge: GPODDER_PASSWORD).")]
            [IsPassword]
            public string Password { get; set; } = string.Empty;

            [DisplayName("Device id")]
            [Description("Device name this Emby server uses on the gpodder server. Empty = emby.")]
            [IsAdvanced]
            public string DeviceId { get; set; } = "emby";
        }

        public class SyncOptions : EditableObjectBase
        {
            public override string EditorTitle => "Sync";

            [DisplayName("Emby users")]
            [Description("Comma-separated Emby user names whose progress is synced. Empty = all users. All of them share the one "
                + "gpodder account, so set your own user name if others share this Emby server.")]
            public string SyncEmbyUsers { get; set; } = string.Empty;

            [DisplayName("Finished when this many seconds are left")]
            [Description("An episode from the gpodder server is marked played when this many seconds or less are left...")]
            [MinValue(0)]
            [MaxValue(3600)]
            [IsAdvanced]
            public int FinishedRemainingSeconds { get; set; } = 30;

            [DisplayName("...or when this percentage was played")]
            [MinValue(50)]
            [MaxValue(100)]
            [IsAdvanced]
            public int FinishedPercent { get; set; } = 95;
        }
    }
}
