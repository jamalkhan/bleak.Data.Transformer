namespace bleak.Data.Transformer;

/// <summary>Filesystem path helpers shared across the engine.</summary>
public static class PathUtil
{
    /// <summary>
    /// Expands a leading <c>~</c> to the current user's home directory. .NET does not do this — it would
    /// otherwise treat <c>~/Desktop</c> as a literal relative folder named "~". Other paths pass through.
    /// </summary>
    public static string ExpandHome(string path)
    {
        if (string.IsNullOrEmpty(path) || path[0] != '~')
        {
            return path;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home))
        {
            return path;
        }

        if (path.Length == 1) // just "~"
        {
            return home;
        }
        if (path[1] == '/' || path[1] == '\\') // "~/..." or "~\..."
        {
            return Path.Combine(home, path[2..]);
        }
        return path; // e.g. "~user" — not supported, leave as-is
    }

    /// <summary>Expands <c>~</c>, then resolves a relative path against <paramref name="baseDirectory"/>.</summary>
    public static string Resolve(string path, string baseDirectory)
    {
        var expanded = ExpandHome(path);
        return Path.IsPathRooted(expanded) ? expanded : Path.GetFullPath(Path.Combine(baseDirectory, expanded));
    }
}
