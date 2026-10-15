using System.Text.Json;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;
using MediaBrowser.Model.Globalization;
using System.Globalization;

namespace Jellyfin.Plugin.JellyFix;

public static class SubtitlePolicy
{
    public static bool Applies(User? user) => user?.SubtitleMode == SubtitlePlaybackMode.Always;

    public static MediaStream? Select(IEnumerable<MediaStream> streams, PluginConfiguration? config = null,
        ILocalizationManager? localization = null)
    {
        config ??= new PluginConfiguration();
        var first=CanonicalLanguage(config.FirstSubtitleLanguage,localization);
        var second=CanonicalLanguage(config.SecondSubtitleLanguage,localization);
        int Rank(MediaStream s)
        {
            var language=CanonicalLanguage(s.Language,localization);
            return Matches(language,first)?0:Matches(language,second)?1:2;
        }
        return streams.Where(s=>s.Type==MediaStreamType.Subtitle)
            .Where(s=>config.UseAnySubtitleFallback || Rank(s)<2)
            .OrderBy(Rank).ThenBy(s=>s.IsForced).ThenByDescending(s=>s.IsDefault)
            .ThenByDescending(s=>s.IsTextSubtitleStream).ThenBy(s=>s.Index).FirstOrDefault();
    }

    private static bool Matches(string language,string preferred) => preferred=="no" ? language is "no" or "nb" or "nn" : language==preferred;

    private static readonly IReadOnlyDictionary<string,string> IsoCodes = CultureInfo.GetCultures(CultureTypes.NeutralCultures)
        .Where(c=>!string.IsNullOrEmpty(c.Name)).GroupBy(c=>c.ThreeLetterISOLanguageName,StringComparer.OrdinalIgnoreCase)
        .ToDictionary(g=>g.Key,g=>g.First().TwoLetterISOLanguageName,StringComparer.OrdinalIgnoreCase);
    public static string CanonicalLanguage(string? language, ILocalizationManager? localization=null)
    {
        var code=(language??"").Trim().Replace('_','-').ToLowerInvariant();
        if(code is "no" or "nor" || code.StartsWith("no-")) return "no";
        if(code is "nb" or "nob" || code.StartsWith("nb-")) return "nb";
        if(code is "nn" or "nno" || code.StartsWith("nn-")) return "nn";
        var info=localization?.FindLanguageInfo(code);
        if(info is not null && info.ThreeLetterISOLanguageNames.Any()) code=info.ThreeLetterISOLanguageNames[0].ToLowerInvariant();
        code=code switch {"fre"=>"fra","ger"=>"deu","chi"=>"zho","dut"=>"nld","cze"=>"ces","gre"=>"ell","rum"=>"ron","slo"=>"slk","wel"=>"cym","baq"=>"eus","per"=>"fas","alb"=>"sqi","arm"=>"hye","bur"=>"mya","geo"=>"kat","ice"=>"isl","mac"=>"mkd","mao"=>"mri","may"=>"msa","tib"=>"bod",_=>code};
        code=code.Split('-')[0];
        return IsoCodes.TryGetValue(code,out var canonical)?canonical:code;
    }

    // Clone before changing per-user defaults: don't mutate a provider's cached source or streams.
    public static MediaSourceInfo ForPlayback(MediaSourceInfo source, User? user, ILogger logger, ILocalizationManager? localization=null)
    {
        if (!Applies(user) || !source.MediaStreams.Any(s => s.Type == MediaStreamType.Video))
            return source;

        var clone = JsonSerializer.Deserialize<MediaSourceInfo>(JsonSerializer.SerializeToUtf8Bytes(source))
            ?? throw new InvalidOperationException("Could not clone media source.");
        // Jellyfin deliberately omits this field from JSON. Preserve audio selection semantics.
        clone.DefaultAudioIndexSource = source.DefaultAudioIndexSource;
        Apply(clone,JellyFixSettings.Current,localization);
        logger.LogDebug("JellyFix: user {UserId}, source {SourceId}, subtitle index {Index}",
            user!.Id, clone.Id, clone.DefaultSubtitleStreamIndex);
        return clone;
    }

    // The void interface method requires an in-place update. Replace streams with private copies first.
    public static void Apply(MediaSourceInfo source, PluginConfiguration? config=null, ILocalizationManager? localization=null)
    {
        var selected = Select(source.MediaStreams,config,localization);
        source.DefaultSubtitleStreamIndex = selected?.Index;
        foreach (var stream in source.MediaStreams.Where(s => s.Type == MediaStreamType.Subtitle))
            stream.Score = stream.Index == selected?.Index ? 1_000_000 : null;
    }
}
