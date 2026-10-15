using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.JellyFix;

public static partial class BrowserTitle
{
    public static string Script(string title)
    {
        if(string.IsNullOrWhiteSpace(title)) return "/* JellyFix title override is disabled. */";
        return "(() => { 'use strict'; const fixed = "+JsonSerializer.Serialize(title)+";"+"""
            const descriptor = Object.getOwnPropertyDescriptor(Document.prototype, 'title');
            const read = descriptor && descriptor.get ? () => descriptor.get.call(document) : () => document.title;
            const write = descriptor && descriptor.set ? () => descriptor.set.call(document, fixed) : () => { document.title = fixed; };
            const enforce = () => { if (read() !== fixed) write(); };
            enforce();
            if (descriptor && descriptor.get && descriptor.set && descriptor.configurable) {
                Object.defineProperty(document, 'title', { configurable: true, get: () => fixed, set: enforce });
            }
            if (document.head) new MutationObserver(enforce).observe(document.head, { childList: true, subtree: true, characterData: true });
            addEventListener('pageshow', enforce);
            addEventListener('popstate', enforce);
            })();
            """;
    }
    [GeneratedRegex(@"<title\b[^>]*>[\s\S]*?</title\s*>",RegexOptions.IgnoreCase)]
    private static partial Regex TitleElement();
    public static string Rewrite(string html,string title,string scriptPath)
    {
        var head=html.IndexOf("<head",StringComparison.OrdinalIgnoreCase);
        if(head<0) return html;
        var tag="<title>"+WebUtility.HtmlEncode(title)+"</title>";
        html=TitleElement().IsMatch(html)?TitleElement().Replace(html,_=>tag,1):html;
        var at=html.IndexOf('>',head);
        if(at<0) return html;
        var injection="<script src=\""+WebUtility.HtmlEncode(scriptPath)+"\" data-jellyfix-title=\"true\"></script>";
        return html.Insert(at+1,injection);
    }
}

[ApiController]
[Route("JellyFix")]
public sealed class JellyFixWebController : ControllerBase
{
    [AllowAnonymous]
    [HttpGet("title.js")]
    public ContentResult TitleScript()
    {
        Response.Headers.CacheControl="no-store";
        return Content(BrowserTitle.Script(JellyFixSettings.Current.BrowserTitleOverride),"application/javascript; charset=utf-8");
    }
}

public sealed class JellyFixWebStartupFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    { app.UseMiddleware<JellyFixTitleMiddleware>();next(app); };
}

public sealed class JellyFixTitleMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var path=context.Request.Path.Value??"";
        var web=path.LastIndexOf("/web",StringComparison.OrdinalIgnoreCase);
        var isIndex=path.EndsWith("/web/index.html",StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("/web/",StringComparison.OrdinalIgnoreCase);
        var title=JellyFixSettings.Current.BrowserTitleOverride;
        if(!isIndex || web<0 || string.IsNullOrWhiteSpace(title) || !HttpMethods.IsGet(context.Request.Method))
        { await next(context);return; }
        var body=context.Response.Body;
        var encoding=context.Request.Headers.AcceptEncoding;
        var etag=context.Request.Headers.IfNoneMatch;
        var modified=context.Request.Headers.IfModifiedSince;
        // Only this small HTML response is buffered. Video, subtitle and script responses pass through.
        await using var buffer=new MemoryStream();
        context.Response.Body=buffer;
        context.Request.Headers.Remove("Accept-Encoding");
        context.Request.Headers.Remove("If-None-Match");
        context.Request.Headers.Remove("If-Modified-Since");
        try
        {
            await next(context);
            context.Response.Body=body;
            if(context.Response.StatusCode==200 && context.Response.ContentType?.StartsWith("text/html",StringComparison.OrdinalIgnoreCase)==true)
            {
                var html=Encoding.UTF8.GetString(buffer.ToArray());
                var script=(context.Request.PathBase.Value??"")+path[..web]+"/JellyFix/title.js";
                var output=Encoding.UTF8.GetBytes(BrowserTitle.Rewrite(html,title,script));
                context.Response.Headers.Remove("ETag");
                context.Response.Headers.Remove("Last-Modified");
                context.Response.Headers.CacheControl="no-store";
                context.Response.ContentLength=output.Length;
                await body.WriteAsync(output,context.RequestAborted);
            }
            else { buffer.Position=0;await buffer.CopyToAsync(body,context.RequestAborted); }
        }
        finally
        {
            context.Response.Body=body;
            context.Request.Headers.AcceptEncoding=encoding;
            context.Request.Headers.IfNoneMatch=etag;
            context.Request.Headers.IfModifiedSince=modified;
        }
    }
}
