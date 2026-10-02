using System.Text;
using System.Text.RegularExpressions;

namespace FileUpdaterClient.Updating;

//The player's list of files and folders never to download, kept in the install folder as .launcherignore and read like a
//.gitignore: one pattern per line, # for comments, * and ? within a name, ** across folders, a trailing / for folders only,
//a leading / (or any / in the middle) to match from the top of the install folder, and ! to bring back something an earlier
//line ignored. As in git, a file inside an ignored folder can't be brought back. Case is ignored
public class IgnoreRules
{
    public const string FileName = ".launcherignore";

    public static readonly IgnoreRules None = new([]);

    private readonly List<Rule> _rules;

    private IgnoreRules(List<Rule> rules) => _rules = rules;

    public static string PathIn(string installPath) => Path.Combine(installPath, FileName);

    public static IgnoreRules Parse(string? text)
    {
        var rules = new List<Rule>();
        foreach (var rawLine in (text ?? string.Empty).Split('\n'))
        {
            var line = rawLine.TrimEnd('\r', ' ', '\t');
            if (line.Length == 0 || line.StartsWith('#')) continue;

            var negated = line.StartsWith('!');
            if (negated) line = line[1..];
            if (line.StartsWith(@"\#") || line.StartsWith(@"\!")) line = line[1..]; //A name that really starts with # or !

            var directoryOnly = line.EndsWith('/');
            line = line.TrimEnd('/');
            if (line.Length == 0) continue;

            //A slash anywhere but the end ties the pattern to the top of the install folder
            var anchored = line.Contains('/');
            line = line.TrimStart('/');
            if (line.Length == 0) continue;

            var regex = new Regex((anchored ? "^" : "^(?:.*/)?") + ToRegex(line) + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            rules.Add(new Rule(regex, negated, directoryOnly));
        }

        return new IgnoreRules(rules);
    }

    public bool IsEmpty => _rules.Count == 0;

    //What ignores a server file name (always with / between folders): the topmost ignored folder it is in, written with a
    //trailing / (e.g. "Music/"), or the name itself when only the file is ignored. Null when it isn't ignored
    public string? Match(string name)
    {
        if (IsEmpty) return null;

        var segments = name.Split('/');
        for (var i = 1; i < segments.Length; i++)
        {
            var folder = string.Join('/', segments[..i]);
            if (IsIgnored(folder, isFolder: true)) return folder + "/";
        }

        return IsIgnored(name, isFolder: false) ? name : null;
    }

    //The last line that matches decides, so a later ! line can bring back what an earlier line ignored
    private bool IsIgnored(string path, bool isFolder)
    {
        var ignored = false;
        foreach (var rule in _rules)
        {
            if (rule.FoldersOnly && !isFolder) continue;
            if (rule.Pattern.IsMatch(path)) ignored = !rule.Negated;
        }

        return ignored;
    }

    private static string ToRegex(string pattern)
    {
        var regex = new StringBuilder();
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '*' && i + 1 < pattern.Length && pattern[i + 1] == '*')
            {
                var atStart = i == 0 || pattern[i - 1] == '/';
                var atEnd = i + 2 == pattern.Length;
                var beforeSlash = i + 2 < pattern.Length && pattern[i + 2] == '/';
                if (atStart && beforeSlash)
                {
                    regex.Append("(?:.*/)?"); //**/ is any number of folders, including none
                    i += 2;
                }
                else if (atStart && atEnd)
                {
                    regex.Append(".*"); //A trailing /** is everything inside
                    i += 1;
                }
                else
                {
                    regex.Append("[^/]*"); //Anywhere else ** is just *
                    i += 1;
                }
            }
            else if (c == '*')
            {
                regex.Append("[^/]*");
            }
            else if (c == '?')
            {
                regex.Append("[^/]");
            }
            else if (c == '[' && pattern.IndexOf(']', i + 1) is var close and > 0)
            {
                var set = pattern[(i + 1)..close];
                if (set.StartsWith('!')) set = "^" + set[1..];
                regex.Append('[').Append(set.Replace(@"\", @"\\")).Append(']');
                i = close;
            }
            else if (c == '\\' && i + 1 < pattern.Length)
            {
                regex.Append(Regex.Escape(pattern[++i].ToString()));
            }
            else
            {
                regex.Append(Regex.Escape(c.ToString()));
            }
        }

        return regex.ToString();
    }

    private record Rule(Regex Pattern, bool Negated, bool FoldersOnly);
}
