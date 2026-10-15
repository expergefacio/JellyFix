using System.Reflection;
using System.Text.Json;
using Jellyfin.Plugin.JellyFix;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Streaming;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

public static class TranscodingChecks
{
    public static IMediaEncoder Encoder() => DispatchProxy.Create<IMediaEncoder,DependencyStub>();
    public static EncodingHelper Helper(IMediaEncoder encoder) => new(null!,encoder,null!,new ConfigurationBuilder().Build(),null!,null!);
    public static MediaSourceInfo Fixture() => new()
    {
        Id="fixture", Path="/fixture.mkv", Container="mkv", Protocol=MediaProtocol.File,
        MediaStreams=[
            new MediaStream {Index=0,Type=MediaStreamType.Video,Codec="h264",Width=3840,Height=2160,BitRate=20_000_000,BitDepth=8,Profile="high",RealFrameRate=24},
            new MediaStream {Index=1,Type=MediaStreamType.Audio,Codec="aac",BitRate=384_000,Channels=2,SampleRate=48000},
            new MediaStream {Index=2,Type=MediaStreamType.Subtitle,Codec="pgssub",Language="eng"}
        ]
    };
    public static void RegisterDependencies(IServiceCollection services)
    {
        ConfigurationChecks.Register(services);
        var encoder=Encoder();
        services.AddSingleton(encoder);
        services.AddSingleton(Helper(encoder));
        services.AddSingleton(DispatchProxy.Create<ILibraryManager,DependencyStub>());
        services.AddSingleton(DispatchProxy.Create<ITranscodeManager,DependencyStub>());
        services.AddSingleton(DispatchProxy.Create<IServerConfigurationManager,DependencyStub>());
        services.AddSingleton(DispatchProxy.Create<IUserManager,DependencyStub>());
    }
    public static async Task Run(Action<bool,string> check)
    {
        var encoder=Encoder(); var helper=Helper(encoder);var fixture=Fixture();
        VideoRequestDto Request(string codec="h264") => new() {VideoCodec=codec,AudioCodec="aac",VideoBitRate=30_000_000};
        EncodingDecision Evaluate(VideoRequestDto request,MediaSourceInfo? source=null) => VideoEncodingProbe.EvaluateSource(
            request,"/Videos/test/master.m3u8",null,true,source??fixture,new EncodingOptions(),helper,encoder);
        check(!Evaluate(Request()).EncodesVideo,"Original 4K compatible video is stream-copy, not capped");
        check(Evaluate(Request("hevc")).EncodesVideo,"Codec conversion is detected as video encoding");
        var quality=Request();quality.VideoBitRate=2_000_000;
        check(Evaluate(quality).EncodesVideo,"Existing client quality request can trigger encoding");
        var burn=Request();burn.SubtitleStreamIndex=2;burn.SubtitleMethod=MediaBrowser.Model.Dlna.SubtitleDeliveryMethod.Encode;
        check(Evaluate(burn).EncodesVideo,"Subtitle burn-in is detected as video encoding");
        var copyAudio=Request();copyAudio.AudioCodec="ac3";
        check(!Evaluate(copyAudio).EncodesVideo,"Audio-only conversion still leaves original video unrestricted");
        var encode=Request("hevc");var decision=Evaluate(encode);TranscodeLimits.Apply(encode,decision.HasAudio,decision.AudioCodec);
        check(encode.MaxWidth==1280 && encode.MaxHeight==720,"Video encoding gains 1280x720 bounds");
        check(encode.VideoBitRate==1_072_000 && encode.AudioBitRate==128_000,"Video and audio share 1.2 Mbps budget");
        check(!encode.AllowVideoStreamCopy && !encode.AllowAudioStreamCopy,"Selected video transcode cannot copy over-budget streams");
        var lower=Request("hevc");lower.MaxWidth=640;lower.MaxHeight=360;lower.VideoBitRate=500_000;lower.AudioBitRate=64_000;
        TranscodeLimits.Apply(lower,true,"aac");
        check(lower.MaxWidth==640 && lower.MaxHeight==360 && lower.VideoBitRate==500_000 && lower.AudioBitRate==64_000,"Lower client limits are preserved");
        var fixedSize=Request("hevc");fixedSize.Width=1920;fixedSize.Height=800;TranscodeLimits.Apply(fixedSize,true,"aac");
        check(fixedSize.Width is null && fixedSize.Height is null && fixedSize.MaxWidth==1280 && fixedSize.MaxHeight==720,"Fixed dimensions converted to aspect-preserving bounds");
        var silent=Request("hevc");TranscodeLimits.Apply(silent,false,"");check(silent.VideoBitRate==1_200_000,"No-audio video can use entire budget");

        var encodingState=new EncodingJobInfo(TranscodingJobType.Progressive)
        {
            BaseRequest=encode,IsVideoRequest=true,IsInputVideo=true,VideoStream=fixture.MediaStreams[0],
            AudioStream=fixture.MediaStreams[1],MediaSource=fixture,InputContainer="mkv",OutputContainer="mp4",
            OutputVideoCodec="h264",OutputAudioCodec="aac",OutputVideoBitrate=encode.VideoBitRate,
            OutputAudioBitrate=encode.AudioBitRate,OutputAudioChannels=2
        };
        var encodingOptions=new EncodingOptions();
        var qualityArgs=helper.GetVideoQualityParam(encodingState,"libx264",encodingOptions,MediaBrowser.Model.Entities.EncoderPreset.veryfast);
        check(qualityArgs.Contains("-maxrate 1072000"),"Actual Jellyfin encoder arguments carry capped video maxrate");
        var filterArgs=helper.GetVideoProcessingFilterParam(encodingState,encodingOptions,"libx264");
        check(filterArgs.Contains("1280"),"Actual Jellyfin video filter contains capped scaling width");
        check(encodingState.OutputWidth<=1280 && encodingState.OutputHeight<=720,"Actual output geometry obeys ceiling");
        encodingState.VideoStream=new MediaStream{Index=0,Type=MediaStreamType.Video,Codec="h264",Width=640,Height=360};
        check(encodingState.OutputWidth==640 && encodingState.OutputHeight==360,"Actual output geometry never upscales small input");

        var builder=WebApplication.CreateBuilder(new WebApplicationOptions{ApplicationName=typeof(TranscodingChecks).Assembly.FullName,Args=[]});
        builder.WebHost.UseUrls("http://127.0.0.1:0");builder.Logging.ClearProviders();
        RegisterDependencies(builder.Services);
        var manager=DispatchProxy.Create<IMediaSourceManager,FakeManager>();
        ((FakeManager)(object)manager).Sources=[fixture];
        builder.Services.AddSingleton(manager);
        new ServiceRegistrator().RegisterServices(builder.Services,null!);
        builder.Services.AddControllers().AddApplicationPart(typeof(Jellyfin.Api.Controllers.DynamicHlsController).Assembly).AddApplicationPart(typeof(Plugin).Assembly);
        await using var app=builder.Build();app.MapControllers();
        app.MapGet("/web/index.html",async (Microsoft.AspNetCore.Http.HttpContext c) =>
        {c.Response.ContentType="text/html";await Microsoft.AspNetCore.Http.HttpResponseWritingExtensions.WriteAsync(c.Response,"<html><head><title>Jellyfin</title></head><body>Media</body></html>");});
        await app.StartAsync();
        using var http=new HttpClient {BaseAddress=new Uri(app.Urls.Single())};
        var prefix="/Videos/"+Guid.NewGuid()+"/master.m3u8?testMarker=preserved&videoBitRate=30000000&audioBitRate=384000";
        async Task<JsonElement> Get(string suffix)
        {
            using var response=await http.GetAsync(prefix+suffix);var body=await response.Content.ReadAsStringAsync();
            if(!response.IsSuccessStatusCode)throw new Exception("HTTP integration failed: "+response.StatusCode+" "+body);
            return JsonDocument.Parse(body).RootElement.Clone();
        }
        var direct=await Get("&static=true&videoCodec=h264");
        check(direct.GetProperty("videoBitRate").GetInt32()==30_000_000 && direct.GetProperty("maxHeight").ValueKind==JsonValueKind.Null,"HTTP static direct play remains uncapped");
        var remux=await Get("&videoCodec=h264&audioCodec=ac3");
        check(remux.GetProperty("videoBitRate").GetInt32()==30_000_000 && remux.GetProperty("maxWidth").ValueKind==JsonValueKind.Null,"HTTP 4K video copy/audio conversion remains uncapped");
        var result=await Get("&videoCodec=hevc&audioCodec=aac&subtitleStreamIndex=2");
        check(result.GetProperty("videoBitRate").GetInt32()==1_072_000 && result.GetProperty("audioBitRate").GetInt32()==128_000,"HTTP video encoding receives capped rates");
        check(result.GetProperty("maxWidth").GetInt32()==1280 && result.GetProperty("maxHeight").GetInt32()==720,"HTTP video encoding receives capped resolution");
        check(result.GetProperty("subtitleStreamIndex").GetInt32()==2,"Transcode filter preserves selected subtitles");
        var query=result.GetProperty("query").GetString()!;
        check(query.Contains("videoBitRate=1072000") && query.Contains("maxHeight=720") && query.Contains("testMarker=preserved"),"Child playlist query carries limits and preserves other parameters");
        var copied=await Get("&videoCodec=copy");check(copied.GetProperty("videoBitRate").GetInt32()==30_000_000,"HTTP explicit video copy bypasses filter");
        var live=await Get("&videoCodec=hevc&liveStreamId=external-live");check(live.GetProperty("maxHeight").ValueKind==JsonValueKind.Null,"Live source left outside library-video policy");
        var savedSettings=Plugin.Instance!.Configuration;
        Plugin.Instance.UpdateConfiguration(new PluginConfiguration {MaximumTranscodeHeight=1080,MaximumTranscodeMbps=3.5m});
        var changed=await Get("&videoCodec=hevc&audioCodec=aac");
        check(changed.GetProperty("maxHeight").GetInt32()==1080 && changed.GetProperty("videoBitRate").GetInt32()==3_372_000,"Saved transcoding settings affect new HTTP requests without restart");
        Plugin.Instance.UpdateConfiguration(new PluginConfiguration {MaximumTranscodeHeight=0,MaximumTranscodeMbps=0});
        var unlimited=await Get("&videoCodec=hevc&audioCodec=aac");
        check(unlimited.GetProperty("maxHeight").ValueKind==JsonValueKind.Null && unlimited.GetProperty("videoBitRate").GetInt32()==30_000_000,"Both no-limit settings bypass the transcoding ceiling");
        Plugin.Instance.UpdateConfiguration(new PluginConfiguration {BrowserTitleOverride="Private Media"});
        var titleScript=await http.GetStringAsync("/JellyFix/title.js");
        check(titleScript.Contains("Private Media") && titleScript.Contains("MutationObserver"),"Public title-script endpoint serves saved title without a token");
        var indexResponse=await http.GetStringAsync("/web/index.html");
        check(indexResponse.Contains("<title>Private Media</title>") && indexResponse.Contains("data-jellyfix-title"),"Startup middleware injects title override into real HTTP index response");
        Plugin.Instance.UpdateConfiguration(savedSettings);
        await app.StopAsync();
    }
}

public class DependencyStub : DispatchProxy
{
    protected override object? Invoke(MethodInfo? method,object?[]? args)
    {
        if(method!.Name=="GetItemById")return new Video {Id=(Guid)args![0]!,Path="/fixture.mkv"};
        if(method.Name=="GetConfiguration")return new EncodingOptions();
        if(method.ReturnType==typeof(bool))return true;
        return null;
    }
}

namespace Jellyfin.Api.Controllers
{
    [ApiController]
    public sealed class DynamicHlsController : ControllerBase
    {
        [HttpGet("Videos/{itemId}/master.m3u8")]
        public object Stream(Guid itemId,string? videoCodec=null,string? audioCodec=null,int? videoBitRate=null,
            int? audioBitRate=null,int? maxWidth=null,int? maxHeight=null,int? width=null,int? height=null,
            bool? @static=null,bool? allowVideoStreamCopy=null,bool? allowAudioStreamCopy=null,
            bool enableAudioVbrEncoding=false,int? subtitleStreamIndex=null,string? liveStreamId=null)
            => new {videoCodec,audioCodec,videoBitRate,audioBitRate,maxWidth,maxHeight,width,height,
                allowVideoStreamCopy,allowAudioStreamCopy,subtitleStreamIndex,query=Request.QueryString.Value};
    }
}
