using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace CSharpScriptingConsole
{
    // 1. StateHolder – keeps script state across executions
    class StateHolder
    {
        public ScriptState<object>? State { get; set; }
    }

    // 2. Program class – main REPL loop
    class Program
    {
        // 3. History – stores previous commands
        private static readonly List<string> history = new List<string>();
        private const int MaxHistorySize = 100;

        // 4. C# Keywords – for syntax highlighting
        private static readonly HashSet<string> CSharpKeywords = new HashSet<string>(StringComparer.Ordinal)
        {
            "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked", "class",
            "const", "continue", "decimal", "default", "delegate", "do", "double", "else", "enum", "event",
            "explicit", "extern", "false", "finally", "fixed", "float", "for", "foreach", "goto", "if",
            "implicit", "in", "int", "interface", "internal", "is", "lock", "long", "namespace", "new",
            "null", "object", "operator", "out", "override", "params", "private", "protected", "public",
            "readonly", "ref", "return", "sbyte", "sealed", "short", "sizeof", "stackalloc", "static",
            "string", "struct", "switch", "this", "throw", "true", "try", "typeof", "uint", "ulong",
            "unchecked", "unsafe", "ushort", "using", "virtual", "void", "volatile", "while",
            "add", "alias", "ascending", "async", "await", "by", "descending", "from", "get", "global",
            "group", "into", "join", "let", "nameof", "on", "orderby", "partial", "remove", "select",
            "set", "value", "var", "when", "where", "yield",
            "record", "init", "required", "file", "scoped", "nint", "nuint",
            "and", "or", "not", "with", "managed", "unmanaged", "notnull"
        };

        // 5. Token Regex – for syntax highlighting
        private static readonly Regex TokenRegex = new Regex(@"(?x)
            (?<comment>//.*?$|/\*[\s\S]*?\*/)
            | (?<string>
                @"" (?: "" | [^""] )* "" 
                | """" (?: [^""] | ""(?!"")) * """" 
                | "" (?: \\. | [^""\\] )* "" 
                | ' (?: \\. | [^'\\] )* ' 
            )
            | (?<number>
                \b(
                    0x[0-9a-fA-F_]+[uU]?[lL]? 
                    | 0b[01_]+[uU]?[lL]? 
                    | \d[\d_]*(\.\d[\d_]*)?([eE][+-]?\d[\d_]*)?[fFdDmM]?[uU]?[lL]? 
                )\b
            )
            | (?<keyword>\b(" + string.Join("|", CSharpKeywords) + @")\b)
        ", RegexOptions.Compiled | RegexOptions.Multiline);

        // 6. Main method – REPL loop
        static async Task Main(string[] args)
        {
            Console.Title = "C# Scripting Console – Full REPL";

            Console.WriteLine("══════════════════════════════════════════════════════════════");
            Console.WriteLine("C# Scripting Console (Roslyn) – Multi-line + .csx + History + Ctrl+R + Syntax Highlighting");
            Console.WriteLine("• ↑ ↓                    Browse history");
            Console.WriteLine("• Ctrl+R                 Reverse incremental search");
            Console.WriteLine("• Live syntax highlighting (keywords, strings, numbers, comments)");
            Console.WriteLine("• Multi-line: blank line to submit");
            Console.WriteLine("══════════════════════════════════════════════════════════════\n");

            var scriptOptions = ScriptOptions.Default
                .WithLanguageVersion(Microsoft.CodeAnalysis.CSharp.LanguageVersion.Latest)
                .WithReferences(
                    typeof(object).Assembly, typeof(Console).Assembly,
                    typeof(System.Linq.Enumerable).Assembly,
                    typeof(System.Collections.Generic.List<>).Assembly,
                    typeof(System.Text.StringBuilder).Assembly,
                    typeof(System.Math).Assembly,
                    typeof(System.IO.File).Assembly,
                    typeof(System.Threading.Tasks.Task).Assembly
                )
                .WithImports("System", "System.Linq", "System.Collections.Generic", 
                             "System.Text", "System.IO", "System.Threading.Tasks", "System.Diagnostics");

            var holder = new StateHolder();

            if (args.Length > 0 && args[0].EndsWith(".csx", StringComparison.OrdinalIgnoreCase))
                await LoadScriptFileAsync(args[0], holder, scriptOptions);

            while (true)
            {
                Console.Write("> ");
                string firstLine = ReadLineWithHistory();

                if (string.IsNullOrWhiteSpace(firstLine)) continue;

                string trimmed = firstLine.Trim();

                if (trimmed.ToLower() is "exit" or "quit") break;

                if (trimmed.StartsWith("#"))
                {
                    if (trimmed.StartsWith("#load ", StringComparison.OrdinalIgnoreCase))
                    {
                        await HandleLoadCommand(firstLine, holder, scriptOptions);
                        continue;
                    }
                    if (trimmed.Equals("#help", StringComparison.OrdinalIgnoreCase))
                    {
                        ShowHelp();
                        continue;
                    }
                    if (trimmed.Equals("#clear", StringComparison.OrdinalIgnoreCase))
                    {
                        holder.State = null;
                        history.Clear();
                        Console.WriteLine("Session and history cleared.");
                        continue;
                    }
                }

                var codeBuilder = new StringBuilder(firstLine + Environment.NewLine);

                while (true)
                {
                    string? nextLine = ReadContinuationLine();
                    if (string.IsNullOrWhiteSpace(nextLine)) break;
                    codeBuilder.AppendLine(nextLine);
                }

                string fullCode = codeBuilder.ToString().TrimEnd('\r', '\n');
                if (string.IsNullOrWhiteSpace(fullCode)) continue;

                bool success = await ExecuteCodeAsync(fullCode, holder, scriptOptions);

                if (success && !string.IsNullOrWhiteSpace(fullCode))
                {
                    if (history.Count == 0 || history[^1] != fullCode)
                    {
                        history.Add(fullCode);
                        if (history.Count > MaxHistorySize)
                            history.RemoveAt(0);
                    }
                }
            }

            Console.WriteLine("\nGoodbye!");
        }

        // 7. ReadLineWithHistory – reads line with history support
        private static string ReadLineWithHistory()
        {
            var buffer = new StringBuilder();
            int historyIndex = history.Count;
            bool inSearch = false;
            string searchTerm = "";
            int searchIndex = -1;

            while (true)
            {
                var key = Console.ReadKey(true);

                if (inSearch)
                {
                    if (key.Key == ConsoleKey.Escape)
                    {
                        inSearch = false;
                        searchTerm = "";
                        RedrawColoredLine(buffer.ToString());
                        continue;
                    }
                    if (key.Key == ConsoleKey.Enter)
                    {
                        Console.WriteLine();
                        return (searchIndex >= 0 && searchIndex < history.Count) ? history[searchIndex] : buffer.ToString();
                    }
                    if (key.Key == ConsoleKey.Backspace && searchTerm.Length > 0)
                        searchTerm = searchTerm[0..^1];
                    else if (key.Key == ConsoleKey.R && key.Modifiers == ConsoleModifiers.Control)
                        searchIndex = FindPreviousMatch(searchTerm, searchIndex - 1);
                    else if (!char.IsControl(key.KeyChar))
                        searchTerm += key.KeyChar;
                    else continue;

                    searchIndex = FindPreviousMatch(searchTerm, searchIndex);
                    RedrawSearchLine(searchTerm, searchIndex);
                    continue;
                }

                if (key.Key == ConsoleKey.Enter)
                {
                    Console.WriteLine();
                    return buffer.ToString();
                }
                if (key.Key == ConsoleKey.UpArrow && history.Count > 0)
                {
                    historyIndex = Math.Max(0, historyIndex - 1);
                    buffer.Clear();
                    buffer.Append(history[historyIndex]);
                    RedrawColoredLine(buffer.ToString());
                }
                else if (key.Key == ConsoleKey.DownArrow && history.Count > 0)
                {
                    historyIndex++;
                    if (historyIndex >= history.Count) { historyIndex = history.Count; buffer.Clear(); }
                    else { buffer.Clear(); buffer.Append(history[historyIndex]); }
                    RedrawColoredLine(buffer.ToString());
                }
                else if (key.Key == ConsoleKey.R && key.Modifiers == ConsoleModifiers.Control)
                {
                    inSearch = true;
                    searchTerm = "";
                    searchIndex = history.Count - 1;
                    RedrawSearchLine("", searchIndex);
                }
                else if (key.Key == ConsoleKey.Backspace && buffer.Length > 0)
                {
                    buffer.Length--;
                    RedrawColoredLine(buffer.ToString());
                }
                else if (!char.IsControl(key.KeyChar))
                {
                    buffer.Append(key.KeyChar);
                    RedrawColoredLine(buffer.ToString());
                }
            }
        }

        // 8. FindPreviousMatch – finds previous match in history
        private static int FindPreviousMatch(string term, int startFrom)
        {
            if (string.IsNullOrEmpty(term)) return -1;
            for (int i = Math.Min(startFrom, history.Count - 1); i >= 0; i--)
                if (history[i].Contains(term, StringComparison.OrdinalIgnoreCase))
                    return i;
            return -1;
        }

        // 9. WriteHighlightedTokens – shared token-coloring core
        private static void WriteHighlightedTokens(string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            int lastPos = 0;
            foreach (Match match in TokenRegex.Matches(text))
            {
                if (match.Index > lastPos)
                    Console.Write(text.Substring(lastPos, match.Index - lastPos));

                if      (match.Groups["comment"].Success) Console.ForegroundColor = ConsoleColor.DarkGray;
                else if (match.Groups["string" ].Success) Console.ForegroundColor = ConsoleColor.Green;
                else if (match.Groups["number" ].Success) Console.ForegroundColor = ConsoleColor.Magenta;
                else if (match.Groups["keyword"].Success) Console.ForegroundColor = ConsoleColor.Cyan;

                Console.Write(match.Value);
                Console.ResetColor();

                lastPos = match.Index + match.Length;
            }

            if (lastPos < text.Length)
                Console.Write(text.Substring(lastPos));
        }

        // RedrawColoredLine – redraws the primary "> " prompt line
        private static void RedrawColoredLine(string text)
        {
            Console.Write("\r> " + new string(' ', Console.WindowWidth - 5) + "\r> ");
            WriteHighlightedTokens(text);
        }

        // ReadContinuationLine – reads a "..> " line with live syntax highlighting
        private static string ReadContinuationLine()
        {
            var buffer = new StringBuilder();

            // Draw initial prompt
            Console.Write("..> ");

            while (true)
            {
                var key = Console.ReadKey(true);

                if (key.Key == ConsoleKey.Enter)
                {
                    Console.WriteLine();
                    return buffer.ToString();
                }

                if (key.Key == ConsoleKey.Backspace && buffer.Length > 0)
                    buffer.Length--;
                else if (!char.IsControl(key.KeyChar))
                    buffer.Append(key.KeyChar);

                // Redraw the continuation line with syntax highlighting
                int clearWidth = Math.Max(0, Console.WindowWidth - 5);
                Console.Write("\r..> " + new string(' ', clearWidth) + "\r..> ");
                WriteHighlightedTokens(buffer.ToString());
            }
        }

        // 10. RedrawSearchLine – draws search line for reverse-i-search
        private static void RedrawSearchLine(string term, int index)
        {
            string match = (index >= 0 && index < history.Count) ? history[index] : "";
            string prompt = $"(reverse-i-search)`{term}': ";
            Console.Write("\r" + new string(' ', Console.WindowWidth - 1) + "\r" + prompt + match);
        }

        // 11. ExecuteCodeAsync – executes code with Roslyn
        private static async Task<bool> ExecuteCodeAsync(string code, StateHolder holder, ScriptOptions options)
        {
            try
            {
                if (holder.State == null)
                    holder.State = await CSharpScript.RunAsync(code, options);
                else
                    holder.State = await holder.State.ContinueWithAsync(code, options);

                if (holder.State.ReturnValue != null)
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.WriteLine(holder.State.ReturnValue);
                    Console.ResetColor();
                }
                return true;
            }
            catch (CompilationErrorException cex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                foreach (var d in cex.Diagnostics) Console.WriteLine(d);
                Console.ResetColor();
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Error: {ex.Message}");
                Console.ResetColor();
            }
            return false;
        }

        // 12. HandleLoadCommand – handles #load command    
        private static async Task HandleLoadCommand(string command, StateHolder holder, ScriptOptions options)
        {
            string path = command.Substring(6).Trim().Trim('"');
            await LoadScriptFileAsync(path, holder, options);
        }

        // 13. LoadScriptFileAsync – loads script file
        private static async Task LoadScriptFileAsync(string filePath, StateHolder holder, ScriptOptions options)
        {
            try
            {
                string resolved = Path.IsPathRooted(filePath) ? filePath : Path.Combine(Environment.CurrentDirectory, filePath);
                if (!File.Exists(resolved))
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"File not found: {filePath}");
                    Console.ResetColor();
                    return;
                }

                string content = await File.ReadAllTextAsync(resolved);
                Console.WriteLine($"[Loading {Path.GetFileName(resolved)}...]");

                if (holder.State == null)
                    holder.State = await CSharpScript.RunAsync(content, options);
                else
                    holder.State = await holder.State.ContinueWithAsync(content, options);

                Console.WriteLine($"[Loaded {Path.GetFileName(resolved)} successfully]");
                if (!history.Contains(content))
                    history.Add(content);
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Load failed: {ex.Message}");
                Console.ResetColor();
            }
        }


        private static void ShowHelp()
        {
            Console.WriteLine("\n=== Controls ===");
            Console.WriteLine("  ↑ / ↓                    Browse history");
            Console.WriteLine("  Ctrl+R                   Reverse incremental search");
            Console.WriteLine("  Esc                      Cancel search");
            Console.WriteLine("  #load \"file.csx\"         Load script file");
            Console.WriteLine("  #clear                   Clear session & history");
            Console.WriteLine("  #help                    Show help");
            Console.WriteLine("  exit / quit              Exit");
            Console.WriteLine("\nLive syntax highlighting is active while typing!");
            Console.WriteLine();
        }
    }
}