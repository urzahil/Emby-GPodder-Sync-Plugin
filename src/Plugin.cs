using System;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Serialization;

namespace Emby.GPodderSync
{
    public class Plugin : BasePlugin<PluginConfiguration>
    {
        public static readonly Guid PluginGuid = new Guid("3b8e6f2a-91c4-4d57-a0e3-7c2f5b19d846");

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
            : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        public static Plugin Instance { get; private set; }

        public override Guid Id => PluginGuid;

        public override string Name => "gPodder Sync";

        public override string Description =>
            "Syncs podcast listening progress between Emby and a gpodder v2 server (such as PodBridge), so Emby and AntennaPod stay in step.";
    }
}
