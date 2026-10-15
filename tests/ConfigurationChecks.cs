using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Xml.Serialization;
using Jellyfin.Plugin.JellyFix;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

public static class ConfigurationChecks
{
    public static void Register(IServiceCollection services)
    {
        var paths=DispatchProxy.Create<IApplicationPaths,ConfigPaths>();
        services.AddSingleton(paths);
        services.AddSingleton(DispatchProxy.Create<IXmlSerializer,ConfigXml>());
        services.AddSingleton(DispatchProxy.Create<ILocalizationManager,DependencyStub>());
    }
    public static async Task Run(Action<bool,string> check)
    {
        var plugin=Plugin.Instance!;
        var original=plugin.Configuration;
        var settings=new PluginConfiguration
        {
            FirstSubtitleLanguage="fra",SecondSubtitleLanguage="deu",UseAnySubtitleFallback=false,
            MaximumTranscodeHeight=1080,MaximumTranscodeMbps=3.5m,BrowserTitleOverride="Media <private> & \"quiet\""
        };
        plugin.UpdateConfiguration(settings);
        check(File.Exists(plugin.ConfigurationFilePath),"Settings saved through Jellyfin's XML configuration mechanism");
        var disk=(PluginConfiguration)new XmlSerializer(typeof(PluginConfiguration)).Deserialize(File.OpenRead(plugin.ConfigurationFilePath))!;
        check(disk.FirstSubtitleLanguage=="fra" && disk.SecondSubtitleLanguage=="deu" && !disk.UseAnySubtitleFallback,"Both preferred languages and no-subtitle fallback persist");
        check(disk.MaximumTranscodeHeight==1080 && disk.MaximumTranscodeMbps==3.5m && disk.BrowserTitleOverride==settings.BrowserTitleOverride,"Resolution, fractional bitrate and browser title persist");
        var french=new MediaBrowser.Model.Entities.MediaStream {Type=MediaBrowser.Model.Entities.MediaStreamType.Subtitle,Index=3,Language="fre"};
        var german=new MediaBrowser.Model.Entities.MediaStream {Type=MediaBrowser.Model.Entities.MediaStreamType.Subtitle,Index=4,Language="ger"};
        var english=new MediaBrowser.Model.Entities.MediaStream {Type=MediaBrowser.Model.Entities.MediaStreamType.Subtitle,Index=5,Language="eng"};
        check(SubtitlePolicy.Select([english,german,french],settings)?.Index==3,"Configured first language matches bibliographic language aliases");
        check(SubtitlePolicy.Select([english,german],settings)?.Index==4,"Configured second language is used when first is absent");
        check(SubtitlePolicy.Select([english],settings) is null,"No-subtitle fallback leaves unrelated subtitles off");
        settings.UseAnySubtitleFallback=true;
        check(SubtitlePolicy.Select([english],settings)?.Index==5,"Any-subtitle fallback accepts unrelated subtitles");
        settings.UseAnySubtitleFallback=false;
        var pages=plugin.GetPages().ToArray();
        check(pages.Length==1 && typeof(Plugin).Assembly.GetManifestResourceStream(pages[0].EmbeddedResourcePath) is not null,"Dashboard settings page is embedded and discoverable");
        var defaults=new PluginConfiguration();
        check(defaults.FirstSubtitleLanguage=="eng" && defaults.SecondSubtitleLanguage=="nor" && defaults.UseAnySubtitleFallback && defaults.MaximumTranscodeHeight==720 && defaults.MaximumTranscodeMbps==1.2m,"New configuration preserves existing defaults");
        bool Reject(PluginConfiguration invalid)
        {
            try {plugin.UpdateConfiguration(invalid);return false;}catch(ArgumentException){return true;}
        }
        check(Reject(new(){MaximumTranscodeMbps=-1}) && Reject(new(){MaximumTranscodeMbps=0.1m}) && Reject(new(){MaximumTranscodeMbps=1001}),"Server rejects invalid bitrate limits");
        check(Reject(new(){MaximumTranscodeHeight=999}) && Reject(new(){BrowserTitleOverride=new string('x',201)}),"Server validates resolution and title length");
        check(ReferenceEquals(plugin.Configuration,settings),"Rejected settings do not replace saved configuration");
        using(var serviceProvider=MakeServices())
        {
            var reloaded=new Plugin(serviceProvider.GetRequiredService<IApplicationPaths>(),serviceProvider.GetRequiredService<IXmlSerializer>());
            // Use the same persisted configuration path to simulate a restart/upgrade.
            File.Copy(plugin.ConfigurationFilePath,reloaded.ConfigurationFilePath,true);
            check(reloaded.Configuration.MaximumTranscodeMbps==3.5m,"Configuration reload retains saved values");
            reloaded.UpdateConfiguration(original);
        }
        // Current Plugin.Instance now points at the reloaded plugin with default configuration.
        var dangerous="</script><script>alert('no')</script> & Media";
        var html="<!doctype html><html><head><title>Old title</title><script src=\"app.js\"></script></head><body>ok</body></html>";
        var rewritten=BrowserTitle.Rewrite(html,dangerous,"/jellyfin/JellyFix/title.js");
        check(rewritten.Contains("&lt;/script&gt;") && !rewritten.Contains("<script>alert"),"Title override is HTML escaped");
        check(rewritten.IndexOf("data-jellyfix-title",StringComparison.Ordinal)<rewritten.IndexOf("app.js",StringComparison.Ordinal),"Title enforcement script loads before application scripts");
        check(BrowserTitle.Script(dangerous).Contains("\\u003C") && !BrowserTitle.Script(dangerous).Contains("</script>"),"Title script safely encodes malicious-looking text");
        check(BrowserTitle.Rewrite("plain text","New","/JellyFix/title.js")=="plain text","Non-HTML content is untouched");
        var context=new DefaultHttpContext();context.Request.Method="GET";context.Request.Path="/jellyfin/web/index.html";context.Request.Headers.AcceptEncoding="gzip";
        context.Request.Headers.IfNoneMatch="old-index";context.Response.Body=new MemoryStream();
        var now=Plugin.Instance!;now.UpdateConfiguration(new PluginConfiguration(){BrowserTitleOverride="Private"});
        var middleware=new JellyFixTitleMiddleware(async c=>
        {
            check(!c.Request.Headers.ContainsKey("Accept-Encoding") && !c.Request.Headers.ContainsKey("If-None-Match"),"Index rewrite prevents compressed or stale cached input");
            c.Response.ContentType="text/html; charset=utf-8";
            c.Response.Headers.ETag="old-index";
            await c.Response.WriteAsync(html);
        });
        await middleware.InvokeAsync(context);
        context.Response.Body.Position=0;var output=await new StreamReader(context.Response.Body).ReadToEndAsync();
        check(output.Contains("<title>Private</title>") && output.Contains("/jellyfin/JellyFix/title.js"),"Index response uses custom title and respects server base path");
        check(context.Response.Headers.CacheControl=="no-store" && !context.Response.Headers.ContainsKey("ETag"),"Modified index cannot reuse stale cache validators");
        check(context.Request.Headers.AcceptEncoding=="gzip" && context.Request.Headers.IfNoneMatch=="old-index","Index middleware restores request headers");
        var media=new DefaultHttpContext();media.Request.Path="/Videos/id/stream.mp4";media.Response.Body=new MemoryStream();
        await new JellyFixTitleMiddleware(c=>c.Response.WriteAsync("video-bytes")).InvokeAsync(media);
        media.Response.Body.Position=0;
        check(await new StreamReader(media.Response.Body).ReadToEndAsync()=="video-bytes","Title middleware never buffers or rewrites video responses");
        now.UpdateConfiguration(original);
        // Export actual generated script for the real-browser checks.
        Directory.CreateDirectory("research");
        await File.WriteAllTextAsync("research/title-test.js",BrowserTitle.Script("Private Media"));
    }
    private static ServiceProvider MakeServices(){var s=new ServiceCollection();Register(s);return s.BuildServiceProvider();}
}

public class ConfigPaths : DispatchProxy
{
    private readonly string root=Path.Combine(Path.GetTempPath(),"jellyfix-config-"+Guid.NewGuid().ToString("N"));
    protected override object? Invoke(MethodInfo? m,object?[]? a)
    {
        var path=Path.Combine(root,m!.Name=="get_PluginConfigurationsPath"?"configurations":"plugins");
        Directory.CreateDirectory(path);return path;
    }
}
public class ConfigXml : DispatchProxy
{
    protected override object? Invoke(MethodInfo? m,object?[]? a)
    {
        if(m!.Name=="SerializeToFile")
        {using var f=File.Create((string)a![1]!);new XmlSerializer(a[0]!.GetType()).Serialize(f,a[0]);return null;}
        if(m.Name=="DeserializeFromFile")
        {using var f=File.OpenRead((string)a![1]!);return new XmlSerializer((Type)a[0]!).Deserialize(f);}
        throw new NotSupportedException(m.Name);
    }
}
