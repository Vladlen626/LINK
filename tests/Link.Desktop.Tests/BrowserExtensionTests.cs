using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace Link.Desktop.Tests;

/// <summary>
/// The browser companion's own checks (tests/browser-extension): the real content and background scripts against a
/// mock ChatGPT page and stubbed extension APIs, run in headless Edge or Chrome with virtual time.
/// </summary>
internal static class BrowserExtensionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    public static void Run()
    {
        var browser = FindBrowser();
        if (browser is null)
        {
            Console.WriteLine("SKIP browser companion checks: neither Edge nor Chrome is installed.");
            return;
        }

        var page = Path.Combine(FindRepository(), "tests", "browser-extension", "run.html");
        var profile = Path.Combine(Path.GetTempPath(), $"link-companion-checks-{Guid.NewGuid():N}");
        try
        {
            var start = new ProcessStartInfo(browser)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var argument in new[]
                     {
                         "--headless",
                         "--disable-gpu",
                         "--no-first-run",
                         "--no-default-browser-check",
                         // The mock chat is another file in an iframe; the checks reach into it.
                         "--allow-file-access-from-files",
                         $"--user-data-dir={profile}",
                         // The companion's waits of seconds and minutes pass at once.
                         "--virtual-time-budget=1800000",
                         "--dump-dom",
                         new Uri(page).AbsoluteUri
                     })
            {
                start.ArgumentList.Add(argument);
            }

            using var process = Process.Start(start)!;
            var dom = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(Timeout))
            {
                process.Kill(entireProcessTree: true);
                Program.Equal("browser companion checks finish", "timed out", "finished");
                return;
            }

            Report(dom.Result);
        }
        finally
        {
            DeleteProfile(profile);
        }
    }

    /// <summary>Each PASS / FAIL line of the page counts as a check; a run cut short says where it stopped.</summary>
    private static void Report(string dom)
    {
        var results = Regex.Match(dom, "<pre id=\"results\">(.*?)</pre>", RegexOptions.Singleline);
        var lines = WebUtility.HtmlDecode(results.Groups[1].Value).Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            if (line.StartsWith("PASS ", StringComparison.Ordinal))
            {
                Program.Record(line[5..], passed: true);
            }
            else if (line.StartsWith("FAIL ", StringComparison.Ordinal))
            {
                var details = lines.Skip(index + 1).TakeWhile(next => next.StartsWith("  ", StringComparison.Ordinal));
                Program.Record($"{line[5..]}\n{string.Join('\n', details)}", passed: false);
            }
        }

        var end = lines.LastOrDefault(line => line.Length > 0) ?? "no results";
        Program.Equal("browser companion checks ran to the end", end.StartsWith("DONE", StringComparison.Ordinal) ? "DONE" : end, "DONE");
    }

    private static string? FindBrowser()
    {
        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft\Edge\Application\msedge.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Google\Chrome\Application\chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Google\Chrome\Application\chrome.exe")
        ];
        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>The checkout the runner was built in, or the one it is run from.</summary>
    private static string FindRepository()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "Link.sln"))) return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Run the checks from the repository: Link.sln was not found.");
    }

    private static void DeleteProfile(string profile)
    {
        try
        {
            if (Directory.Exists(profile)) Directory.Delete(profile, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The browser may hold a file a moment longer; the temp folder is cleaned by Windows.
        }
    }
}
