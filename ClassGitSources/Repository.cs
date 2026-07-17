using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;

namespace ClassGit;

partial class Program
{
    private static void InitRepository()
    {
        if (Directory.Exists(GitDir))
        {
            ShowWarning("Репозиторий ClassGit уже инициализирован.");
            return;
        }

        Directory.CreateDirectory(GitDir);
        Directory.CreateDirectory(ObjectsDir);
        Directory.CreateDirectory(HeadsDir);
        Directory.CreateDirectory(TagsDir);
        Directory.CreateDirectory(HooksDir);
        
        File.WriteAllText(Path.Combine(GitDir, "index"), "");
        File.WriteAllText(Path.Combine(GitDir, "HEAD"), "master"); // По умолчанию ветка master

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("Инициализирован пустой репозиторий ClassGit!");
        Console.ResetColor();
    }

    private static void AddToStage(string filePath)
    {
        if (!AssertRepoInitialized()) return;

        string fullPath = Path.GetFullPath(filePath);
        if (fullPath == GitDir || fullPath.StartsWith(GitDir + Path.DirectorySeparatorChar))
        {
            ShowError("Нельзя добавлять содержимое служебной папки .classgit.");
            return;
        }

        var ignorePatterns = LoadIgnorePatterns();
        List<string> targets;

        if (filePath == "." || Directory.Exists(fullPath))
        {
            if (!Directory.Exists(fullPath))
            {
                ShowError($"Папка '{filePath}' не найдена.");
                return;
            }

            targets = Directory.GetFiles(fullPath, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(Directory.GetCurrentDirectory(), f).Replace('\\', '/'))
                .Where(f => !f.StartsWith(".classgit/"))
                .Where(f => !IsIgnored(f, ignorePatterns))
                .ToList();

            if (!targets.Any())
            {
                ShowWarning("Нет файлов для добавления.");
                return;
            }
        }
        else
        {
            if (IsIgnored(filePath, ignorePatterns))
            {
                ShowWarning($"Файл '{filePath}' игнорируется согласно .classgitignore.");
                return;
            }

            if (!File.Exists(filePath))
            {
                ShowError($"Файл '{filePath}' не найден.");
                return;
            }

            targets = new List<string> { Path.GetRelativePath(Directory.GetCurrentDirectory(), Path.GetFullPath(filePath)).Replace('\\', '/') };
        }

        string indexFile = Path.Combine(GitDir, "index");
        var indexMap = LoadIndexMap(indexFile);

        string lastCommitHash = GetLastCommitHashForCurrentBranch();
        var committedFiles = string.IsNullOrEmpty(lastCommitHash) ? new Dictionary<string, string>() : GetFilesFromCommit(lastCommitHash);

        int added = 0, modified = 0, unchanged = 0;

        foreach (var relativePath in targets)
        {
            byte[] fileBytes = File.ReadAllBytes(relativePath);
            string hash = GetSha1Hash(fileBytes);

            bool alreadyStagedSame = indexMap.TryGetValue(relativePath, out var stagedHash) && stagedHash == hash;
            bool sameAsCommit = committedFiles.TryGetValue(relativePath, out var committedHash) && committedHash == hash;

            if (alreadyStagedSame || sameAsCommit)
            {
                unchanged++;
                continue;
            }

            string objectPath = Path.Combine(ObjectsDir, hash);
            if (!File.Exists(objectPath)) File.WriteAllBytes(objectPath, fileBytes);

            indexMap[relativePath] = hash;

            bool isNew = !committedFiles.ContainsKey(relativePath);
            Console.ForegroundColor = isNew ? ConsoleColor.Green : ConsoleColor.Yellow;
            Console.WriteLine($"[{(isNew ? "новый" : "изменён")}] {relativePath} (hash: {hash.Substring(0, 7)})");
            Console.ResetColor();

            if (isNew) added++; else modified++;
        }

        File.WriteAllLines(indexFile, indexMap.Select(kv => $"{kv.Key} {kv.Value}"));

        if (added == 0 && modified == 0)
        {
            ShowWarning($"Изменений нет — все {unchanged} файл(ов) уже актуальны.");
        }
        else
        {
            Console.WriteLine($"\nДобавлено новых: {added}, изменено: {modified}, без изменений: {unchanged}.");
        }
    }

    private static Dictionary<string, string> LoadIndexMap(string indexFile)
    {
        var map = new Dictionary<string, string>();
        if (!File.Exists(indexFile)) return map;

        foreach (var line in File.ReadAllLines(indexFile))
        {
            var parts = line.Split(' ');
            if (parts.Length >= 2) map[parts[0]] = parts[1];
        }

        return map;
    }

    private static string GetLastCommitHashForCurrentBranch()
    {
        string currentBranch = GetCurrentBranch();
        string branchFile = Path.Combine(HeadsDir, currentBranch);
        return File.Exists(branchFile) ? File.ReadAllText(branchFile).Trim() : "";
    }

    private static List<string> LoadIgnorePatterns()
    {
        string ignoreFile = Path.Combine(Directory.GetCurrentDirectory(), ".classgitignore");
        if (!File.Exists(ignoreFile)) return new List<string>();

        return File.ReadAllLines(ignoreFile)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("#"))
            .ToList();
    }

    private static bool IsIgnored(string relativePath, List<string> patterns)
    {
        string normalized = relativePath.Replace('\\', '/');

        foreach (var pattern in patterns)
        {
            string trimmed = pattern.TrimEnd('/');
            if (normalized == trimmed) return true;
            if (normalized.StartsWith(trimmed + "/")) return true;

            string regexPattern = "^" + Regex.Escape(trimmed).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            if (Regex.IsMatch(normalized, regexPattern, RegexOptions.IgnoreCase)) return true;

            string fileName = Path.GetFileName(normalized);
            if (Regex.IsMatch(fileName, regexPattern, RegexOptions.IgnoreCase)) return true;
        }

        return false;
    }

    private static void CreateCommit(string message)
    {
        if (!AssertRepoInitialized()) return;

        string indexFile = Path.Combine(GitDir, "index");
        if (!File.Exists(indexFile) || string.IsNullOrWhiteSpace(File.ReadAllText(indexFile)))
        {
            ShowWarning("Нечего коммитить. Сначала добавьте файлы через 'add'.");
            return;
        }

        if (!RunPreCommitHook()) return;

        var indexEntries = File.ReadAllLines(indexFile)
            .Select(l => l.Split(' '))
            .Where(p => p.Length >= 2)
            .Select(p => (path: p[0], hash: p[1]))
            .ToList();

        string rootTreeHash = BuildTree(indexEntries);

        string currentBranch = GetCurrentBranch();
        string branchRefFile = Path.Combine(HeadsDir, currentBranch);

        string parentCommitHash = File.Exists(branchRefFile) ? File.ReadAllText(branchRefFile).Trim() : "none";

        StringBuilder commitBuilder = new StringBuilder();
        commitBuilder.AppendLine($"parent: {parentCommitHash}");
        commitBuilder.AppendLine($"author: {GetAuthorName()}");
        commitBuilder.AppendLine($"date: {FormatNow()}");
        commitBuilder.AppendLine($"message: {message}");
        commitBuilder.AppendLine($"tree: {rootTreeHash}");

        string commitContent = commitBuilder.ToString();
        string commitHash = GetSha1Hash(Encoding.UTF8.GetBytes(commitContent));

        File.WriteAllText(Path.Combine(ObjectsDir, commitHash), commitContent);
        File.WriteAllText(branchRefFile, commitHash);
        File.WriteAllText(indexFile, "");

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\n[{currentBranch} {commitHash.Substring(0, 7)}] {message}");
        Console.WriteLine("Коммит успешно создан!");
        Console.ResetColor();
    }

    private static bool RunPreCommitHook()
    {
        string hookPath = Path.Combine(HooksDir, "pre-commit");
        if (!File.Exists(hookPath)) return true;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = hookPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Directory.GetCurrentDirectory()
            };

            using var process = Process.Start(psi);
            process.WaitForExit();

            string stdout = process.StandardOutput.ReadToEnd();
            string stderr = process.StandardError.ReadToEnd();

            if (!string.IsNullOrWhiteSpace(stdout)) Console.WriteLine(stdout.TrimEnd());
            if (!string.IsNullOrWhiteSpace(stderr)) Console.WriteLine(stderr.TrimEnd());

            if (process.ExitCode != 0)
            {
                ShowError($"pre-commit хук завершился с ошибкой (код {process.ExitCode}). Коммит отменён.");
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            ShowError($"Не удалось запустить pre-commit хук: {ex.Message}. На Linux/Mac убедитесь, что файл исполняемый (chmod +x .classgit/hooks/pre-commit) и начинается с shebang (#!/bin/sh).");
            return false;
        }
    }

    private static string BuildTree(List<(string path, string hash)> entries)
    {
        var topLevelFiles = new List<(string name, string hash)>();
        var subDirs = new Dictionary<string, List<(string path, string hash)>>();

        foreach (var entry in entries)
        {
            int slashIndex = entry.path.IndexOf('/');
            if (slashIndex < 0)
            {
                topLevelFiles.Add((entry.path, entry.hash));
            }
            else
            {
                string dir = entry.path.Substring(0, slashIndex);
                string rest = entry.path.Substring(slashIndex + 1);
                if (!subDirs.ContainsKey(dir)) subDirs[dir] = new List<(string, string)>();
                subDirs[dir].Add((rest, entry.hash));
            }
        }

        var lines = new List<string>();

        foreach (var file in topLevelFiles.OrderBy(f => f.name, StringComparer.Ordinal))
        {
            lines.Add($"blob {file.hash} {file.name}");
        }

        foreach (var dir in subDirs.Keys.OrderBy(d => d, StringComparer.Ordinal))
        {
            string subTreeHash = BuildTree(subDirs[dir]);
            lines.Add($"tree {subTreeHash} {dir}");
        }

        string content = string.Join("\n", lines);
        string treeHash = GetSha1Hash(Encoding.UTF8.GetBytes(content));

        string treePath = Path.Combine(ObjectsDir, treeHash);
        if (!File.Exists(treePath)) File.WriteAllText(treePath, content);

        return treeHash;
    }

    private static Dictionary<string, string> ReadTree(string treeHash, string prefix = "")
    {
        var result = new Dictionary<string, string>();
        string treePath = Path.Combine(ObjectsDir, treeHash);
        if (!File.Exists(treePath)) return result;

        foreach (var line in File.ReadAllLines(treePath))
        {
            var parts = line.Split(' ', 3);
            if (parts.Length < 3) continue;

            string type = parts[0];
            string hash = parts[1];
            string name = parts[2];
            string fullPath = prefix.Length == 0 ? name : prefix + "/" + name;

            if (type == "blob")
            {
                result[fullPath] = hash;
            }
            else if (type == "tree")
            {
                foreach (var kv in ReadTree(hash, fullPath))
                {
                    result[kv.Key] = kv.Value;
                }
            }
        }

        return result;
    }

    private static void ShowStatus()
    {
        if (!AssertRepoInitialized()) return;

        string currentBranch = GetCurrentBranch();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"На ветке: {currentBranch}");
        Console.ResetColor();

        string indexFile = Path.Combine(GitDir, "index");
        string indexContent = File.Exists(indexFile) ? File.ReadAllText(indexFile).Trim() : "";

        if (string.IsNullOrEmpty(indexContent))
        {
            ShowWarning("Индекс пуст. Нет файлов для коммита.");
            return;
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("Файлы в индексе:");
        foreach (var line in indexContent.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries))
        {
            Console.WriteLine("  + " + line);
        }
        Console.ResetColor();
    }

    private static Dictionary<string, string> GetFilesFromCommit(string commitHash)
    {
        string commitPath = Path.Combine(ObjectsDir, commitHash);
        if (!File.Exists(commitPath)) return new Dictionary<string, string>();

        string treeHash = "";
        foreach (var line in File.ReadAllLines(commitPath))
        {
            if (line.StartsWith("tree: "))
            {
                treeHash = line.Substring("tree: ".Length).Trim();
                break;
            }
        }

        if (string.IsNullOrEmpty(treeHash)) return new Dictionary<string, string>();
        return ReadTree(treeHash);
    }

    private static bool AssertRepoInitialized()
    {
        if (!Directory.Exists(GitDir))
        {
            ShowError("Репозиторий не инициализирован. Запустите 'init'.");
            return false;
        }

        Directory.CreateDirectory(ObjectsDir);
        Directory.CreateDirectory(HeadsDir);
        Directory.CreateDirectory(TagsDir);
        Directory.CreateDirectory(HooksDir);

        return true;
    }

    private static void HandleConfig(string[] args)
    {
        if (args.Length < 3 || args[1] != "user.name")
        {
            ShowError("Использование: config user.name \"Имя Фамилия\"");
            return;
        }

        if (!Directory.Exists(GitDir))
        {
            ShowError("Репозиторий не инициализирован. Запустите 'init'.");
            return;
        }

        string name = string.Join(" ", args[2..]).Trim('"');
        File.WriteAllText(Path.Combine(GitDir, "config"), $"user.name={name}");

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Имя автора установлено: {name}");
        Console.ResetColor();
    }

    private static string GetAuthorName()
    {
        string configFile = Path.Combine(GitDir, "config");
        if (File.Exists(configFile))
        {
            foreach (var line in File.ReadAllLines(configFile))
            {
                if (line.StartsWith("user.name="))
                {
                    string name = line.Substring("user.name=".Length).Trim();
                    if (!string.IsNullOrEmpty(name)) return name;
                }
            }
        }

        return Environment.UserName;
    }

    private static string FormatNow()
    {
        return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    }

    // --- ВСПОМОГАТЕЛЬНЫЕ МЕТОДЫ ---
    private static string GetCurrentBranch()
    {
        string headFile = Path.Combine(GitDir, "HEAD");
        return File.Exists(headFile) ? File.ReadAllText(headFile).Trim() : "master";
    }

    private static string GetSha1Hash(byte[] input)
    {
        byte[] hashBytes = SHA1.HashData(input);
        StringBuilder sb = new StringBuilder();
        foreach (byte b in hashBytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    private static void ShowError(string msg)
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"Ошибка: {msg}");
        Console.ResetColor();
    }

    private static void ShowWarning(string msg)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine(msg);
        Console.ResetColor();
    }
}