using Jellyfin.Plugin.WeTrakr.Api;
using Jellyfin.Plugin.WeTrakr.Scrobbling;
using Jellyfin.Plugin.WeTrakr.Sync;
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
        serviceCollection.AddSingleton<ProgressThrottle>();
        serviceCollection.AddSingleton<PlayedStateTracker>();
        serviceCollection.AddSingleton<ProgressPlayedTracker>();
        serviceCollection.AddSingleton<SessionItemTracker>();
        serviceCollection.AddSingleton<PayloadBuilder>();

        // Library sync (0.2.0+). The scheduled task (Tasks/WeTrakrSyncTask) is
        // found by Jellyfin on its own and gets the engine from here.
        serviceCollection.AddSingleton<SyncApi>();
        serviceCollection.AddSingleton<SyncEngine>();
        serviceCollection.AddSingleton<SyncScheduler>();

        // Hosted services: Jellyfin starts/stops them with the server.
        serviceCollection.AddHostedService<ScrobbleManager>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<SyncScheduler>());
    }
}

public static class HttpClientNames
{
    public const string WeTrakr = "WeTrakr";
}
