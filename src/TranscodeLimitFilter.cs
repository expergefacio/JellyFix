using System.Reflection;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Streaming;
using MediaBrowser.Model.Dlna;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Jellyfin.Plugin.JellyFix;

public sealed class TranscodeLimitFilter(IVideoEncodingProbe probe, IUserManager users,
    ILogger<TranscodeLimitFilter> logger) : IAsyncActionFilter
{
    private static readonly Dictionary<string,PropertyInfo> Properties = typeof(VideoRequestDto).GetProperties()
        .Where(p => p.CanWrite).ToDictionary(p => p.Name,StringComparer.OrdinalIgnoreCase);
    private static readonly string[] Limits = ["MaxWidth","MaxHeight","Width","Height","VideoBitRate",
        "AudioBitRate","AudioCodec","AllowVideoStreamCopy","AllowAudioStreamCopy","EnableAudioVbrEncoding"];

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var controller = (context.ActionDescriptor as ControllerActionDescriptor)?.ControllerTypeInfo.FullName;
        var hls = controller == "Jellyfin.Api.Controllers.DynamicHlsController";
        if ((!hls && controller != "Jellyfin.Api.Controllers.VideosController")
            || !context.ModelState.IsValid || !context.ActionArguments.ContainsKey("videoBitRate")
            || !context.ActionArguments.TryGetValue("itemId",out var id) || id is not Guid itemId)
        { await next(); return; }

        var settings=JellyFixSettings.Current;
        if(settings.MaximumTranscodeHeight==0 && settings.MaximumTranscodeMbps==0) {await next();return;}
        var request = Bind(context.ActionArguments,itemId,hls);
        request.StreamOptions = context.HttpContext.Request.Query
            .Where(p => p.Key.Length > 0 && char.IsLower(p.Key[0]))
            .ToDictionary(p => p.Key,p => (string?)p.Value.ToString());
        // Static playback and explicit copy never acquire our limits. Packed legacy Params and live
        // streams need their own negotiation adapter; leave those unchanged rather than misclassify.
        if (request.Static || string.Equals(request.VideoCodec,"copy",StringComparison.OrdinalIgnoreCase)
            || !string.IsNullOrWhiteSpace(request.Params) || !string.IsNullOrWhiteSpace(request.LiveStreamId))
        { await next(); return; }
        User? user = null;
        if (Guid.TryParse(context.HttpContext.User.FindFirst("Jellyfin-UserId")?.Value,out var userId))
            user = users.GetUserById(userId);
        EncodingDecision decision;
        try
        {
            decision = await probe.Evaluate(request,context.HttpContext.Request.Path.Value ?? "",user,hls,
                context.HttpContext.RequestAborted).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Preserve the controller's own validation/errors; never log URL/query strings containing tokens.
            logger.LogWarning(ex,"JellyFix could not evaluate the video-encoding request; leaving it unchanged.");
            await next(); return;
        }
        if (decision.EncodesVideo)
        {
            TranscodeLimits.Apply(request,decision.HasAudio,decision.AudioCodec,settings);
            ApplyToContext(context,request);
            logger.LogInformation("JellyFix transcode ceiling: item {ItemId}, max {Width}x{Height}, video {VideoBitrate}, audio {AudioBitrate} bps",
                itemId,request.MaxWidth,request.MaxHeight,request.VideoBitRate,decision.HasAudio?request.AudioBitRate:0);
        }
        await next();
    }

    public static VideoRequestDto Bind(IDictionary<string,object?> arguments, Guid itemId, bool hls)
    {
        var result = new VideoRequestDto {Id=itemId, SubtitleMethod=hls?SubtitleDeliveryMethod.External:SubtitleDeliveryMethod.Encode};
        foreach(var (name,value) in arguments)
            if(value is not null && Properties.TryGetValue(name,out var property)) property.SetValue(result,value);
        // Codec-specific constraints are in the query in the real request. The filter supplies those below.
        return result;
    }

    public static void ApplyToContext(ActionExecutingContext context,VideoRequestDto request)
    {
        var query = new Dictionary<string,StringValues>(context.HttpContext.Request.Query,StringComparer.OrdinalIgnoreCase);
        foreach(var propertyName in Limits)
        {
            var argument = context.ActionDescriptor.Parameters
                .FirstOrDefault(p => string.Equals(p.Name,propertyName,StringComparison.OrdinalIgnoreCase))?.Name;
            if(argument is null) continue;
            var value = Properties[propertyName].GetValue(request);
            context.ActionArguments[argument]=value;
            query.Remove(argument);
            if(value is not null) query[argument]=value is bool b?b.ToString().ToLowerInvariant():Convert.ToString(value,System.Globalization.CultureInfo.InvariantCulture);
        }
        // HLS playlists copy the incoming query into child URLs. Carry the same limits into them.
        context.HttpContext.Request.QueryString=QueryString.Create(query);
    }
}
