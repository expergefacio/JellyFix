using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.JellyFix;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public string FirstSubtitleLanguage { get; set; } = "eng";
    public string SecondSubtitleLanguage { get; set; } = "nor";
    public bool UseAnySubtitleFallback { get; set; } = true;
    public int MaximumTranscodeHeight { get; set; } = 720;
    public decimal MaximumTranscodeMbps { get; set; } = 1.2m;
    public string BrowserTitleOverride { get; set; } = "";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(FirstSubtitleLanguage) || string.IsNullOrWhiteSpace(SecondSubtitleLanguage))
            throw new ArgumentException("Choose both subtitle languages.");
        if (!new[] {0,240,360,480,720,1080,1440,2160}.Contains(MaximumTranscodeHeight))
            throw new ArgumentException("Choose a supported resolution limit.");
        if (MaximumTranscodeMbps is < 0 or > 1000 || (MaximumTranscodeMbps > 0 && MaximumTranscodeMbps < 0.2m))
            throw new ArgumentException("Bitrate must be 0 (unlimited), or between 0.2 and 1000 Mbps.");
        if ((BrowserTitleOverride?.Length ?? 0)>200) throw new ArgumentException("Browser title must be at most 200 characters.");
    }
}

public static class JellyFixSettings
{
    public static PluginConfiguration Current => Plugin.Instance?.Configuration ?? new PluginConfiguration();
}
