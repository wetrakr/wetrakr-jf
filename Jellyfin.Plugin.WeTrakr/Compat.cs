// Diferencias de API entre las lineas de Jellyfin que compila el plugin
// (net8 = 10.10, net9 = 10.11, net10 = 12.x). Ver el .csproj.
#if JF1010
global using JfUser = Jellyfin.Data.Entities.User;
#else
global using JfUser = Jellyfin.Database.Implementations.Entities.User;
#endif
