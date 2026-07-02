namespace SmtpGateway.E2ETests;

/// <summary>
/// Minimal <c>.env</c> reader: walks up from the test binary's location to the repo root looking
/// for a <c>.env</c> file, then parses its <c>KEY=VALUE</c> lines (blank lines and <c>#</c>
/// comments ignored). Never logs or echoes any value.
/// </summary>
internal static class EnvFile
{
    public static IReadOnlyDictionary<string, string> TryLoad() => TryLoad(AppContext.BaseDirectory);

    /// <summary>
    /// Testable seam: walks up from <paramref name="startDirectory"/> (instead of the live test
    /// binary location) looking for a <c>.env</c> file. Lets unit tests point at a scoped temp
    /// directory so the parser is exercised without the developer's real <c>.env</c>.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> TryLoad(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, ".env");
            if (File.Exists(candidate))
            {
                return Parse(candidate);
            }

            directory = directory.Parent;
        }

        return new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private static Dictionary<string, string> Parse(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            var separatorIndex = line.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim();
            result[key] = value;
        }

        return result;
    }
}
