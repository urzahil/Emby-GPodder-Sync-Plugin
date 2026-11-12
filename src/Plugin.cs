using System;
using System.IO;
using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;

namespace Emby.GPodderSync
{
    /// <summary>
    /// The plugin. It has a settings page (Dashboard → Plugins → gPodder Sync) that Emby renders from <see cref="PluginOptions"/>.
    /// The settings themselves stay in the XML file plugins/configurations/Emby.GPodderSync.xml: the page reads from and writes
    /// to that file. Every part of the plugin reads <see cref="Configuration"/> when it needs a value, so a saved change is used
    /// straight away, without restarting Emby. Edits made to the XML file by hand while Emby runs are picked up too.
    /// </summary>
    public class Plugin : BasePluginSimpleUI<PluginOptions>, IHasThumbImage
    {
        public static readonly Guid PluginGuid = new Guid("3b8e6f2a-91c4-4d57-a0e3-7c2f5b19d846");

        /// <summary>Same file name Emby used for this plugin's settings before the settings page existed.</summary>
        private const string ConfigurationFileName = "Emby.GPodderSync.xml";

        /// <summary>How often <see cref="Configuration"/> looks at the file's timestamp for edits made outside the settings page.</summary>
        private static readonly TimeSpan FileCheckInterval = TimeSpan.FromSeconds(5);

        private readonly IApplicationHost _host;
        private readonly IXmlSerializer _xml;
        private readonly ILogger _log;
        private readonly object _configLock = new object();

        private volatile PluginConfiguration _config;
        private DateTime _fileStamp;
        private DateTime _lastFileCheck;

        public Plugin(IApplicationHost applicationHost, IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationHost)
        {
            Instance = this;
            _host = applicationHost;
            _xml = xmlSerializer;
            _log = applicationHost.Resolve<ILogManager>().GetLogger("GPodderSync");
            ConfigurationFilePath = Path.Combine(applicationPaths.PluginConfigurationsPath, ConfigurationFileName);
        }

        public static Plugin Instance { get; private set; }

        public override Guid Id => PluginGuid;

        public override string Name => "gPodder Sync";

        public override string Description =>
            "Syncs podcast listening progress between Emby and a gpodder v2 server (such as PodBridge), so Emby and AntennaPod stay in step.";

        public ImageFormat ThumbImageFormat => ImageFormat.Png;

        /// <summary>Icon shown on Emby's Plugins page.</summary>
        public Stream GetThumbImage()
        {
            var type = GetType();
            return type.Assembly.GetManifestResourceStream(type.Namespace + ".thumb.png");
        }

        /// <summary>Full path of the XML settings file.</summary>
        public string ConfigurationFilePath { get; }

        /// <summary>
        /// Current settings. Treat the returned object as read-only: a save replaces it with a new instance, so take it once
        /// per operation (<c>var cfg = Plugin.Instance.Configuration;</c>) for a consistent set of values.
        /// </summary>
        public PluginConfiguration Configuration
        {
            get
            {
                var cfg = _config;
                if (cfg == null || DateTime.UtcNow - _lastFileCheck >= FileCheckInterval)
                {
                    cfg = LoadIfChanged();
                }

                return cfg;
            }
        }

        /// <summary>Writes the settings to the XML file and makes them the current ones.</summary>
        public void SaveConfiguration(PluginConfiguration cfg)
        {
            if (cfg == null)
            {
                throw new ArgumentNullException(nameof(cfg));
            }

            lock (_configLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ConfigurationFilePath));
                _xml.SerializeToFile(cfg, ConfigurationFilePath);
                _fileStamp = File.GetLastWriteTimeUtc(ConfigurationFilePath);
                _lastFileCheck = DateTime.UtcNow;
                _config = cfg;
            }
        }

        public override PluginInfo GetPluginInfo()
        {
            var info = base.GetPluginInfo();
            info.ConfigurationFileName = ConfigurationFilePath;
            return info;
        }

        // ------------------------------------------------------------ settings page

        protected override PluginOptions OnBeforeShowUI(PluginOptions options) => PluginOptions.FromConfiguration(Configuration);

        /// <summary>
        /// Called when Save is pressed on the settings page. Writes the XML file instead of Emby's default JSON options
        /// file (hence the <c>false</c>, which only skips that JSON file; the page still reports success).
        /// </summary>
        protected override bool OnOptionsSaving(PluginOptions options)
        {
            var cfg = options.ToConfiguration();
            SaveConfiguration(cfg);
            _log.Info("settings saved from the settings page to {0}", ConfigurationFilePath);

            if (GPodderClient.IsConfigured(cfg))
            {
                QueueSync();
            }
            else
            {
                _log.Warn("not configured yet. Enter the server URL, username and password on the settings page.");
            }

            return false;
        }

        /// <summary>Runs the sync task now, so changed settings (server, login, users...) are used right away.</summary>
        private void QueueSync()
        {
            try
            {
                _host.Resolve<ITaskManager>().QueueScheduledTask<SyncTask>();
                _log.Info("sync queued to apply the new settings.");
            }
            catch (Exception ex)
            {
                _log.Warn("could not start the sync ({0}); it will run at its next scheduled time.", ex.Message);
            }
        }

        // ------------------------------------------------------------ XML file

        private PluginConfiguration LoadIfChanged()
        {
            lock (_configLock)
            {
                _lastFileCheck = DateTime.UtcNow;
                var exists = File.Exists(ConfigurationFilePath);
                var stamp = exists ? File.GetLastWriteTimeUtc(ConfigurationFilePath) : DateTime.MinValue;
                if (_config != null && stamp == _fileStamp)
                {
                    return _config;
                }

                PluginConfiguration loaded = null;
                if (exists)
                {
                    try
                    {
                        loaded = (PluginConfiguration)_xml.DeserializeFromFile(typeof(PluginConfiguration), ConfigurationFilePath);
                    }
                    catch (Exception ex)
                    {
                        _log.Warn("could not read {0}: {1}", ConfigurationFilePath, ex.Message);
                        if (_config != null)
                        {
                            // Probably caught mid-write by an editor: keep the last good settings and try again later.
                            return _config;
                        }
                    }
                }

                loaded = loaded ?? new PluginConfiguration();
                _fileStamp = stamp;
                if (_config != null)
                {
                    _log.Info("{0} changed on disk; using the new settings.", ConfigurationFilePath);
                }

                if (!exists)
                {
                    // Write the defaults so the file exists.
                    try
                    {
                        SaveConfiguration(loaded);
                    }
                    catch (Exception ex)
                    {
                        _log.Warn("could not write {0}: {1}", ConfigurationFilePath, ex.Message);
                    }
                }

                _config = loaded;
                return loaded;
            }
        }
    }
}
