using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Jellyfin.Plugin.WeTrakr.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.WeTrakr;

public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Guards UserConnections mutations + SaveConfiguration across all threads.</summary>
    public static readonly object ConfigLock = new();

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    public static Plugin? Instance { get; private set; }

    /// <summary>Reads an embedded resource under Configuration/ as a string.</summary>
    public static string ReadEmbedded(string fileName)
    {
        var name = $"{typeof(Plugin).Namespace}.Configuration.{fileName}";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new FileNotFoundException(name);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public override Guid Id => new("eaa1f0a3-7e4c-4c6f-9b80-0a2c1c5e2f01");

    public override string Name => "WeTrakr";

    public override string Description => "Automatic scrobbling to your WeTrakr profile. Tracks what you play, pause, resume and finish in Jellyfin.";

    public IEnumerable<PluginPageInfo> GetPages() => new[]
    {
        new PluginPageInfo
        {
            Name = Name,
            EmbeddedResourcePath = $"{GetType().Namespace}.Configuration.configPage.html"
        }
    };
}
