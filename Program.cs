using System.Reflection;
var asm = Assembly.LoadFrom("/Users/albertoaznar/.nuget/packages/jellyfin.model/10.11.8/lib/net9.0/MediaBrowser.Model.dll");
foreach (var t in asm.GetExportedTypes())
    if (t.Name == "UserDataSaveReason" || t.Name == "UserDataSaveEventArgs")
        System.Console.WriteLine($"{t.FullName} ({t.Assembly.GetName().Name})");
var asm2 = Assembly.LoadFrom("/Users/albertoaznar/.nuget/packages/jellyfin.controller/10.11.8/lib/net9.0/MediaBrowser.Controller.dll");
foreach (var t in asm2.GetExportedTypes())
    if (t.Name == "UserDataSaveReason" || t.Name == "UserDataSaveEventArgs")
        System.Console.WriteLine($"{t.FullName} ({t.Assembly.GetName().Name})");
