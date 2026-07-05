using System.Text;

namespace Wimux.Core.Terminal;

public static class ShellWorkingDirectoryResolver
{
    public static bool TryResolveCdCommand(string command, string? currentDirectory, out string newDirectory)
    {
        newDirectory = string.Empty;
        var trimmed = command.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
            return false;

        var current = string.IsNullOrWhiteSpace(currentDirectory)
            ? Environment.CurrentDirectory
            : currentDirectory;

        if (trimmed.Equals("cd..", StringComparison.OrdinalIgnoreCase))
            return TryResolveTarget("..", current, out newDirectory);
        if (trimmed.Equals("cd\\", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("cd /", StringComparison.OrdinalIgnoreCase))
            return TryResolveTarget(Path.GetPathRoot(current) ?? current, current, out newDirectory);
        if (trimmed.Equals("cd", StringComparison.OrdinalIgnoreCase) || trimmed.Equals("pwd", StringComparison.OrdinalIgnoreCase))
            return false;
        if (trimmed.Equals("popd", StringComparison.OrdinalIgnoreCase))
            return false;

        var parts = SplitCommand(trimmed);
        if (parts.Count == 0)
            return false;

        var verb = parts[0];
        var targetIndex = 1;

        if (verb.Equals("cd", StringComparison.OrdinalIgnoreCase) && parts.Count > 2 && parts[1].Equals("/d", StringComparison.OrdinalIgnoreCase))
            targetIndex = 2;
        else if ((verb.Equals("Set-Location", StringComparison.OrdinalIgnoreCase) || verb.Equals("sl", StringComparison.OrdinalIgnoreCase)) &&
                 parts.Count > 2 &&
                 (parts[1].Equals("-Path", StringComparison.OrdinalIgnoreCase) || parts[1].Equals("-LiteralPath", StringComparison.OrdinalIgnoreCase)))
            targetIndex = 2;

        if (!verb.Equals("cd", StringComparison.OrdinalIgnoreCase) &&
            !verb.Equals("chdir", StringComparison.OrdinalIgnoreCase) &&
            !verb.Equals("Set-Location", StringComparison.OrdinalIgnoreCase) &&
            !verb.Equals("sl", StringComparison.OrdinalIgnoreCase) &&
            !verb.Equals("pushd", StringComparison.OrdinalIgnoreCase))
            return false;

        if (parts.Count <= targetIndex)
            return false;

        return TryResolveTarget(parts[targetIndex], current, out newDirectory);
    }

    private static bool TryResolveTarget(string target, string currentDirectory, out string newDirectory)
    {
        newDirectory = string.Empty;
        target = target.Trim().Trim('"', '\'');
        if (string.IsNullOrWhiteSpace(target))
            return false;
        if (target == "~")
            target = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        try
        {
            var resolved = Path.IsPathRooted(target)
                ? target
                : Path.GetFullPath(Path.Combine(currentDirectory, target));

            if (!Directory.Exists(resolved))
                return false;

            newDirectory = resolved.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static List<string> SplitCommand(string command)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var ch in command)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
            parts.Add(current.ToString());

        return parts;
    }
}
