using System.IO;

namespace ReactionStudio.Services;

/// <summary>
/// Single source of truth for where the app keeps everything. Everything lives
/// under one visible folder next to the project so data isn't split across drives.
/// </summary>
public static class AppPaths
{
    public static readonly string Base = @"D:\Users\M4tth3w\Downloads\ReactionStudio";

    public static string Clips      => Ensure(Path.Combine(Base, "clips"));
    public static string Output     => Ensure(Path.Combine(Base, "output"));
    public static string Thumbnails => Ensure(Path.Combine(Base, "thumbnails"));

    // Auth: the OAuth secret you download from Google, and the cached login token.
    public static string Secret   => Path.Combine(Base, "client_secret.json");
    public static string AuthData => Ensure(Path.Combine(Base, "app-data"));

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
