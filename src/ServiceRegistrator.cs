using MediaBrowser.Controller;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Mvc;
using MediaBrowser.Model.Globalization;
using Microsoft.AspNetCore.Hosting;

namespace Jellyfin.Plugin.JellyFix;

public sealed class ServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection services, IServerApplicationHost applicationHost)
    {
        services.AddSingleton<IStartupFilter,JellyFixWebStartupFilter>();
        services.AddScoped<IVideoEncodingProbe,VideoEncodingProbe>();
        services.AddScoped<TranscodeLimitFilter>();
        services.Configure<MvcOptions>(options => options.Filters.AddService<TranscodeLimitFilter>());
        // Jellyfin 12.2 registers this singleton before calling plugin service registrators.
        var original = services.LastOrDefault(s => s.ServiceType == typeof(IMediaSourceManager));
        if (original is null || original.Lifetime != ServiceLifetime.Singleton || original.IsKeyedService)
            throw new InvalidOperationException("JellyFix requires Jellyfin 12.2's singleton IMediaSourceManager registration.");

        services.AddSingleton(sp => new OriginalMediaSourceManager(original, sp));
        services.Remove(original);
        services.AddSingleton<IMediaSourceManager>(sp => new PriorityMediaSourceManager(
            sp.GetRequiredService<OriginalMediaSourceManager>().Value,
            sp.GetRequiredService<ILogger<PriorityMediaSourceManager>>(),
            sp.GetRequiredService<ILocalizationManager>()));
    }
}

// Own the original singleton and dispose it exactly once, as the original DI registration would.
public sealed class OriginalMediaSourceManager : IDisposable
{
    private readonly bool _ownsInstance;
    public IMediaSourceManager Value { get; }

    public OriginalMediaSourceManager(ServiceDescriptor descriptor, IServiceProvider provider)
    {
        _ownsInstance = descriptor.ImplementationInstance is null;
        Value = (IMediaSourceManager)(descriptor.ImplementationInstance
            ?? descriptor.ImplementationFactory?.Invoke(provider)
            ?? ActivatorUtilities.CreateInstance(provider, descriptor.ImplementationType
                ?? throw new InvalidOperationException("Unsupported media-source service registration.")));
    }

    public void Dispose()
    {
        if (_ownsInstance && Value is IDisposable disposable) disposable.Dispose();
    }
}
