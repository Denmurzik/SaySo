using System.Text;
using System.Text.RegularExpressions;

namespace SaySo.Core.Tests.Text;

/// <summary>
/// Сканирует исходники интерфейса, которые не достать из модульных тестов: XAML окна настроек, строки в коде окна настроек и
/// тексты установщика. Длинное (—) и среднее (–) тире допустимы только в комментариях и журнале.
/// </summary>
public partial class NoDashSourceScanTests
{
    private static readonly string Root = FindRepoRoot();

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "SaySo.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("SaySo.slnx не найден выше " + AppContext.BaseDirectory);
    }

    private static IEnumerable<string> SourceFiles(string folder, string pattern) =>
        Directory.EnumerateFiles(Path.Combine(Root, folder), pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"));

    private static bool HasDash(string text) => text.AsSpan().IndexOfAny('—', '–') >= 0;

    /// <summary>Оператор, начинающийся с вызова журнала: его строки не интерфейс.</summary>
    [GeneratedRegex(@"\b(?:Platform)?Log\.(?:Info|Warn|Error|Debug)\s*\(")]
    private static partial Regex LogCall();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex XmlComment();

    [GeneratedRegex(@"^\[[A-Za-z]+\]\s*$")]
    private static partial Regex SectionHeader();

    [GeneratedRegex(@"&#(?:8212|8211|x2014|x2013);", RegexOptions.IgnoreCase)]
    private static partial Regex DashEntity();

    /// <summary>Строки в тексте XAML (атрибуты и содержимое), без XML-комментариев; тире можно записать и числовой сущностью.</summary>
    internal static IEnumerable<string> XamlDashLines(string xaml) =>
        XmlComment().Replace(xaml, m => new string('\n', m.Value.Count(c => c == '\n')))
            .Split('\n')
            .Select((line, i) => (line, number: i + 1))
            .Where(x => HasDash(x.line) || DashEntity().IsMatch(x.line))
            .Select(x => $"{x.number}: {x.line.Trim()}");

    /// <summary>
    /// Строковые литералы C#, где есть тире: комментарии (//, /* */) пропускаются, обычные, дословные, интерполированные,
    /// «сырые» строки и символьные литералы разбираются целиком. Литералы в операторе, который начинается с вызова журнала
    /// (<c>Log.Info(...)</c>, <c>PlatformLog.Error(...)</c>), пропускаются: журнал не интерфейс.
    /// </summary>
    internal static IEnumerable<string> CSharpLiteralsWithDash(string code)
    {
        var line = 1;
        var i = 0;
        var statementStart = 0; // начало текущего оператора: после «;», «{» или «}» вне литералов
        while (i < code.Length)
        {
            var c = code[i];
            if (c == '\n')
            {
                line++;
                i++;
            }
            else if (c == '/' && i + 1 < code.Length && code[i + 1] == '/')
            {
                while (i < code.Length && code[i] != '\n')
                {
                    i++;
                }
            }
            else if (c == '/' && i + 1 < code.Length && code[i + 1] == '*')
            {
                var end = code.IndexOf("*/", i + 2, StringComparison.Ordinal);
                end = end < 0 ? code.Length : end + 2;
                line += code.AsSpan(i, end - i).Count('\n');
                i = end;
            }
            else if (c == ';' || c == '{' || c == '}')
            {
                statementStart = ++i;
            }
            else if (c == '\'')
            {
                var start = ++i;
                while (i < code.Length && code[i] != '\'')
                {
                    i += code[i] == '\\' ? 2 : 1;
                }

                var literal = code[start..Math.Min(i, code.Length)];
                i++;
                if (HasDash(literal) && !LogCall().IsMatch(code.AsSpan(statementStart, start - statementStart)))
                {
                    yield return $"{line}: '{literal}'";
                }
            }
            else if (c == '"' || c == '$' || c == '@')
            {
                var start = i;
                var startLine = line;
                var literal = ReadStringLiteral(code, ref i, ref line);
                if (literal is null)
                {
                    i = start + 1; // «$» или «@» не перед строкой
                    continue;
                }

                if (HasDash(literal) && !LogCall().IsMatch(code.AsSpan(statementStart, start - statementStart)))
                {
                    yield return $"{startLine}: {literal.Trim()}";
                }
            }
            else
            {
                i++;
            }
        }
    }

    private static string? ReadStringLiteral(string code, ref int i, ref int line)
    {
        var p = i;
        var verbatim = false;
        while (p < code.Length && (code[p] == '$' || code[p] == '@'))
        {
            verbatim |= code[p] == '@';
            p++;
        }

        if (p >= code.Length || code[p] != '"')
        {
            return null;
        }

        var quotes = 0;
        while (p + quotes < code.Length && code[p + quotes] == '"')
        {
            quotes++;
        }

        var text = new StringBuilder();
        if (quotes >= 3)
        {
            // Сырая строка: заканчивается столькими же кавычками, сколько в начале.
            var close = new string('"', quotes);
            var end = code.IndexOf(close, p + quotes, StringComparison.Ordinal);
            end = end < 0 ? code.Length : end;
            text.Append(code, p + quotes, end - p - quotes);
            i = Math.Min(code.Length, end + quotes);
        }
        else
        {
            p++;
            while (p < code.Length)
            {
                var ch = code[p];
                if (ch == '"' && verbatim && p + 1 < code.Length && code[p + 1] == '"')
                {
                    text.Append('"');
                    p += 2;
                }
                else if (ch == '"')
                {
                    p++;
                    break;
                }
                else if (ch == '\\' && !verbatim && p + 1 < code.Length)
                {
                    text.Append(ch).Append(code[p + 1]);
                    p += 2;
                }
                else
                {
                    text.Append(ch);
                    p++;
                }
            }

            i = p;
        }

        line += text.ToString().Count(ch => ch == '\n');
        return text.ToString();
    }

    /// <summary>
    /// Тексты установщика Inno Setup: все разделы, кроме [Code], целиком, а в [Code] только строки вне комментариев и вызовов Log
    /// (журнал установки не интерфейс). Комментарии: строки на «;» и «//».
    /// </summary>
    internal static IEnumerable<string> InnoDashLines(string script)
    {
        var inCode = false;
        var number = 0;
        foreach (var raw in script.Split('\n'))
        {
            number++;
            var line = raw.TrimEnd('\r');
            var trimmed = line.TrimStart();
            if (SectionHeader().IsMatch(trimmed))
            {
                inCode = trimmed.Equals("[Code]", StringComparison.OrdinalIgnoreCase);
                continue;
            }

            if (trimmed.StartsWith(';') || trimmed.StartsWith("//"))
            {
                continue;
            }

            var code = StripPascalLineComment(line);
            if (inCode && code.Contains("Log(", StringComparison.Ordinal))
            {
                continue;
            }

            if (HasDash(code))
            {
                yield return $"{number}: {code.Trim()}";
            }
        }
    }

    private static string StripPascalLineComment(string line)
    {
        var inString = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '\'')
            {
                inString = !inString;
            }
            else if (!inString && line[i] == '/' && i + 1 < line.Length && line[i + 1] == '/')
            {
                return line[..i];
            }
        }

        return line;
    }

    [Fact]
    public void XamlFiles_HaveNoDashes()
    {
        var files = SourceFiles("src", "*.xaml").ToList();
        var found = files.SelectMany(f => XamlDashLines(File.ReadAllText(f)).Select(l => $"{Path.GetRelativePath(Root, f)}:{l}")).ToList();

        Assert.True(files.Count >= 6, $"найдено слишком мало XAML-файлов: {files.Count}");
        Assert.Empty(found);
    }

    private static List<string> DashLiteralsIn(IEnumerable<string> files) =>
        files.SelectMany(f => CSharpLiteralsWithDash(File.ReadAllText(f)).Select(l => $"{Path.GetRelativePath(Root, f)}:{l}")).ToList();

    [Fact]
    public void SettingsCodeBehind_StringLiterals_HaveNoDashes()
    {
        var files = SourceFiles(Path.Combine("src", "SaySo", "Settings"), "*.cs").ToList();

        Assert.True(files.Count >= 10, $"найдено слишком мало файлов окна настроек: {files.Count}");
        Assert.Empty(DashLiteralsIn(files));
    }

    [Fact]
    public void DaemonAndAppCode_StringLiterals_HaveNoDashes_OutsideLogCalls()
    {
        // Демон: меню и подсказки трея, уведомления, плашка; корень проекта: Program.cs и App.xaml.cs.
        var daemon = SourceFiles(Path.Combine("src", "SaySo", "Daemon"), "*.cs").ToList();
        var root = Directory.EnumerateFiles(Path.Combine(Root, "src", "SaySo"), "*.cs", SearchOption.TopDirectoryOnly).ToList();

        Assert.True(daemon.Count >= 5, $"найдено слишком мало файлов демона: {daemon.Count}");
        Assert.True(root.Count >= 2, $"найдено слишком мало файлов корня проекта: {root.Count}");
        Assert.Empty(DashLiteralsIn(daemon.Concat(root)));
    }

    [Fact]
    public void InstallerScript_UserVisibleText_HasNoDashes()
    {
        var path = Path.Combine(Root, "installer", "SaySo.iss");
        var script = File.ReadAllText(path);

        Assert.Contains("[CustomMessages]", script);
        Assert.Empty(InnoDashLines(script).Select(l => $"installer/SaySo.iss:{l}"));
    }

    [Fact]
    public void XamlScanner_FindsDashesInTextAndEntities_ButNotInComments()
    {
        const string Xaml = "<Page>\n<!-- комментарий — можно -->\n<TextBlock Text=\"раз — два\" />\n<TextBlock Text=\"раз &#8212; два\" />\n<TextBlock Text=\"раз &#x2013; два\" />\n<TextBlock Text=\"чисто\" />\n</Page>";

        var found = XamlDashLines(Xaml).ToList();

        Assert.Equal(3, found.Count);
        Assert.StartsWith("3:", found[0]);
        Assert.StartsWith("4:", found[1]);
        Assert.StartsWith("5:", found[2]);
    }

    [Fact]
    public void CSharpScanner_FindsDashesInLiterals_ButNotInComments()
    {
        const string Code = """
            // комментарий — можно
            var a = "строка — плохо";
            /* блок — можно */
            var b = $"интерполяция – плохо {x}";
            var c = @"дословная — плохо ""кавычка"" ";
            var d = "чисто"; // хвост — можно
            var e = '—';
            var f = "экранированная \" — плохо";
            var g = "без тире";
            Log.Info("журнал — можно");
            PlatformLog.Error($"журнал – можно {x}");
            Log.Info($"многострочный журнал"
                     + " — можно");
            var h = "после журнала — плохо";
            """;

        var found = CSharpLiteralsWithDash(Code).ToList();

        Assert.Equal(6, found.Count);
        Assert.StartsWith("2:", found[0]);
        Assert.StartsWith("4:", found[1]);
        Assert.StartsWith("5:", found[2]);
        Assert.StartsWith("7:", found[3]); // символьный литерал: тоже строка интерфейса, если окажется в тексте
        Assert.StartsWith("8:", found[4]);
        Assert.StartsWith("14:", found[5]); // оператор после вызова журнала снова проверяется
    }

    [Fact]
    public void InnoScanner_SkipsCommentsAndLog_ButFindsMessagesAndMsgBoxes()
    {
        const string Script = "[Setup]\n; комментарий — можно\n[CustomMessages]\nrussian.A=Раз — два\nrussian.B=Чисто\n[Code]\n// комментарий — можно\nLog('журнал — можно');\nx := 1; // хвост — можно\nSuppressibleMsgBox('диалог – плохо', mbError, MB_OK, IDOK);";

        var found = InnoDashLines(Script).ToList();

        Assert.Equal(2, found.Count);
        Assert.StartsWith("4:", found[0]);
        Assert.StartsWith("10:", found[1]);
    }
}
