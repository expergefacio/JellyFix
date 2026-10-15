using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Streaming;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Dto;

namespace Jellyfin.Plugin.JellyFix;

public sealed record EncodingDecision(bool EncodesVideo, bool HasAudio, string AudioCodec);

public interface IVideoEncodingProbe
{
    Task<EncodingDecision> Evaluate(VideoRequestDto request, string path, User? user, bool hls, CancellationToken token);
}

public sealed class VideoEncodingProbe(IMediaSourceManager sources, ILibraryManager library,
    ITranscodeManager jobs, IServerConfigurationManager configuration,
    EncodingHelper helper, IMediaEncoder encoder) : IVideoEncodingProbe
{
    public async Task<EncodingDecision> Evaluate(VideoRequestDto request, string path, User? user, bool hls, CancellationToken token)
    {
        var item = library.GetItemById<BaseItem>(request.Id);
        if (item is null) return new(false,false,"");
        var current = string.IsNullOrWhiteSpace(request.PlaySessionId) ? null : jobs.GetTranscodingJob(request.PlaySessionId);
        MediaSourceInfo? source = current?.MediaSource;
        if (source is null)
        {
            var available = await sources.GetPlaybackMediaSources(item, null!, false, false, token).ConfigureAwait(false);
            source = string.IsNullOrWhiteSpace(request.MediaSourceId) ? available.FirstOrDefault()
                : available.FirstOrDefault(s => string.Equals(s.Id,request.MediaSourceId,StringComparison.Ordinal));
        }
        // Live/open sources have separate lifecycle and delivery decisions. Do not touch those here.
        if (source is null || source.RequiresOpening || source.RequiresClosing || source.IsInfiniteStream)
            return new(false,false,"");
        return EvaluateSource(request, path, user, hls, source, configuration.GetEncodingOptions(), helper, encoder);
    }

    public static EncodingDecision EvaluateSource(VideoRequestDto request, string path, User? user, bool hls,
        MediaSourceInfo source, EncodingOptions options, EncodingHelper helper, IMediaEncoder encoder)
    {
        // Mirror Jellyfin 12.2's public streaming-state setup and TryStreamCopy decision.
        var state = new EncodingJobInfo(hls ? TranscodingJobType.Hls : TranscodingJobType.Progressive)
        {
            BaseRequest = request, IsVideoRequest = true, IsInputVideo = true, User = user!,
            SupportedVideoCodecs = Split(request.VideoCodec), SupportedAudioCodecs = Split(request.AudioCodec)
        };
        if (state.SupportedVideoCodecs.Length > 0) request.VideoCodec = state.SupportedVideoCodecs[0];
        if (state.SupportedAudioCodecs.Length > 0)
            request.AudioCodec = state.SupportedAudioCodecs.FirstOrDefault(encoder.CanEncodeToAudioCodec) ?? state.SupportedAudioCodecs[0];
        if (string.IsNullOrEmpty(request.AudioCodec)) request.AudioCodec = helper.InferAudioCodec(Path.GetExtension(path).TrimStart('.'));
        helper.AttachMediaSourceInfo(state, options, source, path);
        state.OutputAudioCodec = request.AudioCodec;
        state.OutputAudioChannels = helper.GetNumAudioChannelsParam(state,state.AudioStream,state.OutputAudioCodec);
        state.OutputAudioBitrate = helper.GetAudioBitrateParam(request.AudioBitRate,request.AudioCodec,state.AudioStream,state.OutputAudioChannels) ?? 0;
        state.OutputVideoCodec = request.VideoCodec;
        state.OutputVideoBitrate = helper.GetVideoBitrateParamValue(request,state.VideoStream,state.OutputVideoCodec);
        helper.TryStreamCopy(state,options);
        if (state.VideoStream is null || EncodingHelper.IsCopyCodec(state.OutputVideoCodec))
            return new(false,state.AudioStream is not null,"");
        var codec = state.SupportedAudioCodecs.FirstOrDefault(c => !EncodingHelper.IsCopyCodec(c)
            && !EncodingHelper.LosslessAudioCodecs.Contains(c) && encoder.CanEncodeToAudioCodec(c));
        codec ??= hls ? "aac" : helper.InferAudioCodec(request.Container ?? Path.GetExtension(path).TrimStart('.'));
        if (EncodingHelper.LosslessAudioCodecs.Contains(codec)) return new(false,state.AudioStream is not null,"");
        return new(true,state.AudioStream is not null,codec);
    }

    private static string[] Split(string? value) => value?.Split(',',StringSplitOptions.RemoveEmptyEntries) ?? [];
}
