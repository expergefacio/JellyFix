using System.Reflection;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.JellyFix;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dlna;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

int checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    checks++;
    Console.WriteLine("PASS " + name);
}
MediaStream Sub(int index, string? language, bool forced = false, bool hi = false, bool external = false) => new()
{
    Index = index, Language = language!, Type = MediaStreamType.Subtitle,
    Codec = "subrip", IsForced = forced, IsHearingImpaired = hi, IsExternal = external
};
MediaSourceInfo Source(params MediaStream[] subtitles) => new()
{
    Id = "fixture", DefaultAudioStreamIndex = 1, DefaultAudioIndexSource = AudioIndexSource.User,
    MediaStreams = new[] {
        new MediaStream { Index = 0, Type = MediaStreamType.Video, Codec = "h264" },
        new MediaStream { Index = 1, Type = MediaStreamType.Audio, Codec = "aac", Language = "nor" }
    }.Concat(subtitles).ToArray()
};
// Reproduce the real loader/dashboard access sequence that was absent from the first tests.
var initialServices=new ServiceCollection();
ConfigurationChecks.Register(initialServices);
using (var pluginServices = initialServices.BuildServiceProvider())
{
    var plugin = ActivatorUtilities.CreateInstance<Jellyfin.Plugin.JellyFix.Plugin>(pluginServices);
    Check(plugin.Version.ToString() == "0.3.0.0", "PluginManager can read plugin version immediately after construction");
    Check(File.Exists(plugin.AssemblyFilePath), "Plugin assembly path is initialised");
    Check(!string.IsNullOrWhiteSpace(plugin.DataFolderPath) && Path.IsPathFullyQualified(plugin.DataFolderPath), "Plugin data-folder path is initialised");
    Check(plugin.GetPluginInfo().Name == "JellyFix", "BasePlugin.GetPluginInfo does not crash");
    var installed = new MediaBrowser.Common.Plugins.LocalPlugin(
        Path.GetDirectoryName(plugin.AssemblyFilePath)!, true,
        new MediaBrowser.Common.Plugins.PluginManifest
        {
            Id = plugin.Id, Name = plugin.Name, Version = plugin.Version.ToString(),
            Status = MediaBrowser.Model.Plugins.PluginStatus.Active
        }) { Instance = plugin };
    var info = installed.GetPluginInfo();
    Check(info.Name == "JellyFix", "Actual LocalPlugin.GetPluginInfo dashboard path succeeds");
    Check(System.Text.Json.JsonSerializer.Serialize(new[] { info }).Contains("JellyFix"),
        "Dashboard plugin response serialises successfully");
}
var user = new User("test", "test", "test") { SubtitleMode = SubtitlePlaybackMode.Always };
var log = NullLogger.Instance;
Check(SubtitlePolicy.Select([Sub(2,"nor"),Sub(3,"eng")])?.Index == 3,"English beats Norwegian");
Check(SubtitlePolicy.Select([Sub(2,"eng",hi:true),Sub(3,"nor")])?.Index == 2,"English HI beats Norwegian full");
Check(SubtitlePolicy.Select([Sub(2,"en",forced:true),Sub(3,"eng",hi:true)])?.Index == 3,"Full English beats forced English");
Check(SubtitlePolicy.Select([Sub(2,"eng",forced:true),Sub(3,"nor")])?.Index == 2,"Language priority precedes forced tie-break");
foreach (var code in new[] {"no","nor","nb","nob","nn","nno","NB-no"})
    Check(SubtitlePolicy.Select([Sub(2,"fra"),Sub(3,code)])?.Index == 3,"Norwegian alias " + code);
Check(SubtitlePolicy.Select([Sub(2,"fra"),Sub(3,"EN-us")])?.Index == 3,"English locale and case");
Check(SubtitlePolicy.Select([Sub(2,"fra",external:true)])?.Index == 2,"Any external subtitle fallback");
Check(SubtitlePolicy.Select([Sub(2,"deu")])?.Index == 2,"Any embedded subtitle fallback");
Check(SubtitlePolicy.Select([Sub(2,null)])?.Index == 2,"Unknown language fallback");
Check(SubtitlePolicy.Select([Sub(2,"deu",forced:true)])?.Index == 2,"Forced-only fallback");
Check(SubtitlePolicy.Select([Sub(2,"deu",forced:true),Sub(3,"fra")])?.Index == 3,"Full track wins within any-language fallback");
Check(SubtitlePolicy.Select([]) is null,"No subtitle stays off");
Check(SubtitlePolicy.Select([new MediaStream{Index=2,Type=MediaStreamType.Audio,Language="eng"}]) is null,"Never selects audio as subtitle");
var source = Source(Sub(2,"nor"),Sub(3,"eng"));
source.MediaStreams[2].Score = 999;
var selected = SubtitlePolicy.ForPlayback(source,user,log);
Check(selected.DefaultSubtitleStreamIndex == 3,"Default selected before playback");
Check(source.DefaultSubtitleStreamIndex is null && source.MediaStreams[2].Score == 999,"Provider-owned source remains unchanged");
Check(!ReferenceEquals(source.MediaStreams[2],selected.MediaStreams[2]),"Per-request streams are isolated");
Check(selected.DefaultAudioStreamIndex == 1 && selected.DefaultAudioIndexSource == AudioIndexSource.User,"Audio defaults and internal selection flags preserved");
Check(selected.MediaStreams.Single(s => s.Index == 3).Score == 1_000_000 && selected.MediaStreams.Single(s => s.Index == 2).Score is null,"Old server scores cannot override selection");
var resolve = typeof(StreamBuilder).GetMethod("GetDefaultSubtitleStreamIndex",BindingFlags.NonPublic|BindingFlags.Static)!;
Check((int?)resolve.Invoke(null,[selected,Array.Empty<SubtitleProfile>()]) == 3,"Real Jellyfin StreamBuilder keeps chosen track");
// Exercise actual Jellyfin negotiation, including explicit client choices.
selected.Container = "mkv";
selected.Path = "/fixture.mkv";
var builder = new StreamBuilder(DispatchProxy.Create<ITranscoderSupport, TranscoderStub>(),log);
var options = new MediaOptions
{
    ItemId = Guid.NewGuid(), DeviceId = "test-device", MediaSources = [selected],
    Profile = new DeviceProfile
    {
        DirectPlayProfiles = [new DirectPlayProfile {Container="mkv",VideoCodec="h264",AudioCodec="aac",Type=DlnaProfileType.Video}],
        SubtitleProfiles = [new SubtitleProfile {Format="subrip",Method=SubtitleDeliveryMethod.External}]
    }
};
var negotiated=builder.GetOptimalVideoStream(options);
Check(negotiated?.SubtitleStreamIndex==3,"Actual playback negotiation selects English before playback");
options.MediaSourceId=selected.Id;
options.SubtitleStreamIndex=2;
Check(builder.GetOptimalVideoStream(options)?.SubtitleStreamIndex==2,"Manual client subtitle selection is honoured");
options.SubtitleStreamIndex=-1;
Check(builder.GetOptimalVideoStream(options)?.SubtitleStreamIndex==-1,"Manual client subtitles-off is honoured");
Check(SubtitlePolicy.ForPlayback(Source(),user,log).DefaultSubtitleStreamIndex is null,"Video without subtitles stays off");
foreach (var mode in new[]{SubtitlePlaybackMode.None,SubtitlePlaybackMode.Default,SubtitlePlaybackMode.Smart,SubtitlePlaybackMode.OnlyForced})
{
    user.SubtitleMode=mode;
    Check(ReferenceEquals(SubtitlePolicy.ForPlayback(source,user,log),source),"Other user mode untouched: "+mode);
}
user.SubtitleMode=SubtitlePlaybackMode.Always;
Check(ReferenceEquals(SubtitlePolicy.ForPlayback(source,null,log),source),"Anonymous/internal source queries untouched");
var inner = DispatchProxy.Create<IMediaSourceManager, FakeManager>();
var fake = (FakeManager)(object)inner;
fake.Sources=[source,Source(Sub(5,"fra"),Sub(8,"nob"))];
var services = new ServiceCollection();
services.AddLogging();
TranscodingChecks.RegisterDependencies(services);
services.AddSingleton<IMediaSourceManager>(_=>inner);
new ServiceRegistrator().RegisterServices(services,null!);
using(var provider=services.BuildServiceProvider(new ServiceProviderOptions{ValidateOnBuild=true,ValidateScopes=true}))
{
    var decorated=provider.GetRequiredService<IMediaSourceManager>();
    Check(decorated is PriorityMediaSourceManager,"Plugin replaces interface registration with decorator");
    Check(ReferenceEquals(decorated,provider.GetRequiredService<IMediaSourceManager>()),"Singleton lifetime preserved");
    var results=await decorated.GetPlaybackMediaSources(null!,user,false,true,CancellationToken.None);
    Check(results[0].DefaultSubtitleStreamIndex==3 && results[1].DefaultSubtitleStreamIndex==8,"Independent selections for multiple media versions");
    Check(source.DefaultSubtitleStreamIndex is null,"Decorator leaves originals unchanged");
    var staticSources=decorated.GetStaticMediaSources(null!,true,user);
    Check(staticSources[1].DefaultSubtitleStreamIndex==8,"Static sources expose consistent defaults");
    var reset=Source(Sub(2,"nor"));
    decorated.SetDefaultAudioAndSubtitleStreamIndices(null!,reset,user);
    Check(reset.DefaultSubtitleStreamIndex==2,"Explicit default recalculation also applies policy");
    Check(decorated.GetPathProtocol("https://test")==MediaBrowser.Model.MediaInfo.MediaProtocol.Http,"Unrelated service calls forwarded");
}
Check(fake.DisposeCount==1,"Original service disposed exactly once");
await TranscodingChecks.Run(Check);
await ConfigurationChecks.Run(Check);
Console.WriteLine($"All {checks} checks passed.");

public class FakeManager : DispatchProxy, IDisposable
{
    public IReadOnlyList<MediaSourceInfo> Sources = [];
    public int DisposeCount;
    public void Dispose()=>DisposeCount++;
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
    {
        "GetPlaybackMediaSources" => Task.FromResult(Sources),
        "GetStaticMediaSources" => Sources,
        "SetDefaultAudioAndSubtitleStreamIndices" => null,
        "GetPathProtocol" => MediaBrowser.Model.MediaInfo.MediaProtocol.Http,
        _ => throw new NotSupportedException(method.Name)
    };
}

public class TranscoderStub : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.ReturnType==typeof(bool)
        ? true : throw new NotSupportedException(method.Name);
}
