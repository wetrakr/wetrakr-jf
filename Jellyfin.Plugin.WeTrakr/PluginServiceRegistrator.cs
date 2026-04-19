using Jellyfin.Plugin.WeTrakr.Api;
using Jellyfin.Plugin.WeTrakr.Scrobbling;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.WeTrakr;

/// <summary>
/// Registers plugin services with Jellyfin's DI container.
/// The server picks this up automatically if the type implements
/// IPluginServiceRegistrator and lives in the plugin assembly.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient(HttpClientNames.WeTrakr);

        serviceCollection.AddSingleton<WeTrakrClient>();
        serviceCollection.AddSingleton<DeviceCodeClient>();
        serviceCollection.AddSingleton<PauseStateTracker>();
        serviceCollection.AddSingleton<PayloadBuilder>();

        // ScrobbleManager is a hosted service: Jellyfin starts/stops it with the server.
        serviceCollection.AddHostedService<ScrobbleManager>();
    }
}

public static class HttpClientNames
{
    public const string WeTrakr = "WeTrakr";
}
