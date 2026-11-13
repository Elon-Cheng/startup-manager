namespace StartupManager.Core;

public static class CommandParser
{
    // Parsing never launches a process. Ambiguous unquoted paths stay unresolved.
    public static (string Executable, string Arguments) Parse(string command)
    {
        var text = Environment.ExpandEnvironmentVariables(command.Trim());
        if (text.Length == 0) return ("", "");
        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? (text[1..end], text[(end + 1)..].TrimStart()) : ("", text);
        }
        var split = text.IndexOfAny([' ', '\t']);
        var first = split < 0 ? text : text[..split];
        if (!first.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && split >= 0) return ("", text);
        return (first, split < 0 ? "" : text[split..].TrimStart());
    }
}
