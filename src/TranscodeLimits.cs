using MediaBrowser.Controller.Streaming;
namespace Jellyfin.Plugin.JellyFix;

public static class TranscodeLimits
{
    public const int AudioBitrate=128_000;
    public static void Apply(VideoRequestDto request,bool hasAudio,string audioCodec,PluginConfiguration? settings=null)
    {
        settings ??= new PluginConfiguration();
        var height=settings.MaximumTranscodeHeight;
        if(height>0)
        {
            var width=(int)Math.Ceiling(height*16d/9d/2)*2;
            request.MaxWidth=Math.Min(PositiveMinimum(request.MaxWidth,request.Width)??width,width);
            request.MaxHeight=Math.Min(PositiveMinimum(request.MaxHeight,request.Height)??height,height);
            request.Width=null;request.Height=null;
        }
        if(settings.MaximumTranscodeMbps>0)
        {
            var total=(int)(settings.MaximumTranscodeMbps*1_000_000m);
            var audio=hasAudio?Math.Min(Positive(request.AudioBitRate)??AudioBitrate,AudioBitrate):0;
            request.VideoBitRate=Math.Min(Positive(request.VideoBitRate)??total,total-audio);
            if(hasAudio)
            {
                request.AudioBitRate=audio;request.AudioCodec=audioCodec;
                request.AllowAudioStreamCopy=false;request.EnableAudioVbrEncoding=false;
            }
        }
        // This request was already identified as encoding video. Preserve that choice after capping.
        request.AllowVideoStreamCopy=false;
    }
    private static int? Positive(int? n)=>n>0?n:null;
    private static int? PositiveMinimum(int? a,int? b)=>(Positive(a),Positive(b)) switch
    { (int x,int y)=>Math.Min(x,y),(int x,null)=>x,(null,int y)=>y,_=>null };
}
