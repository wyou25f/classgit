using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;

namespace ClassGit;

partial class Program
{
    private static void HandleDiff(string[] args)
    {
        if (!AssertRepoInitialized()) return;

        string currentBranch = GetCurrentBranch();
        string branchFile = Path.Combine(HeadsDir, currentBranch);
        string lastCommit = File.Exists(branchFile) ? File.ReadAllText(branchFile).Trim() : "";

        if (string.IsNullOrEmpty(lastCommit))
        {
            ShowWarning("Нет коммитов для сравнения.");
            return;
        }

        var committedFiles = GetFilesFromCommit(lastCommit);
        List<string> targets = args.Length >= 2 ? new List<string> { args[1] } : committedFiles.Keys.ToList();

        if (!targets.Any())
        {
            ShowWarning("Нет файлов для сравнения.");
            return;
        }

        foreach (var file in targets)
        {
            if (!committedFiles.ContainsKey(file))
            {
                ShowWarning($"Файл '{file}' отсутствует в последнем коммите.");
                continue;
            }

            string objectPath = Path.Combine(ObjectsDir, committedFiles[file]);
            string[] oldLines = File.Exists(objectPath) ? File.ReadAllLines(objectPath) : Array.Empty<string>();
            string[] newLines = File.Exists(file) ? File.ReadAllLines(file) : Array.Empty<string>();

            PrintDiff(file, oldLines, newLines);
        }
    }

    private static void PrintDiff(string fileName, string[] oldLines, string[] newLines)
    {
        var ops = ComputeDiffOps(oldLines, newLines);
        bool hasChanges = ops.Any(o => o.Item1 != ' ');

        if (!hasChanges)
        {
            Console.WriteLine($"diff {fileName}: без изменений");
            return;
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"\n--- {fileName}");
        Console.WriteLine($"+++ {fileName}");
        Console.ResetColor();

        foreach (var (op, line) in ops)
        {
            if (op == '+')
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"+ {line}");
                Console.ResetColor();
            }
            else if (op == '-')
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"- {line}");
                Console.ResetColor();
            }
            else
            {
                Console.WriteLine($"  {line}");
            }
        }
    }

    private static List<(char, string)> ComputeDiffOps(string[] oldLines, string[] newLines)
    {
        int n = oldLines.Length;
        int m = newLines.Length;
        var dp = new int[n + 1, m + 1];

        for (int i = n - 1; i >= 0; i--)
        {
            for (int j = m - 1; j >= 0; j--)
            {
                dp[i, j] = oldLines[i] == newLines[j] ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);
            }
        }

        var result = new List<(char, string)>();
        int x = 0, y = 0;

        while (x < n && y < m)
        {
            if (oldLines[x] == newLines[y])
            {
                result.Add((' ', oldLines[x]));
                x++;
                y++;
            }
            else if (dp[x + 1, y] >= dp[x, y + 1])
            {
                result.Add(('-', oldLines[x]));
                x++;
            }
            else
            {
                result.Add(('+', newLines[y]));
                y++;
            }
        }

        while (x < n)
        {
            result.Add(('-', oldLines[x]));
            x++;
        }

        while (y < m)
        {
            result.Add(('+', newLines[y]));
            y++;
        }

        return result;
    }
}