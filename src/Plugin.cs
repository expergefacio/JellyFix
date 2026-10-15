using System.Runtime.InteropServices;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

[assembly: Guid("7e0b6e7a-01ab-47af-b681-47d879e1e081")]

namespace Jellyfin.Plugin.JellyFix;

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public static Plugin? Instance { get; private set; }
    public Plugin(IApplicationPaths paths, IXmlSerializer serializer) : base(paths,serializer) => Instance=this;
    public override string Name => "JellyFix";
    public override Guid Id => new("7e0b6e7a-01ab-47af-b681-47d879e1e081");
    public override string Description => "Configurable subtitle priorities, video-transcoding limits and browser title override.";
    public IEnumerable<PluginPageInfo> GetPages() => [new PluginPageInfo
    {
        Name="JellyFix", DisplayName="JellyFix",
        EmbeddedResourcePath="Jellyfin.Plugin.JellyFix.Configuration.configPage.html"
    }];
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        if(configuration is not PluginConfiguration settings) throw new ArgumentException("Invalid JellyFix configuration.");
        settings.Validate();
        base.UpdateConfiguration(settings);
    }
}
