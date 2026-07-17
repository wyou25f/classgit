using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ClassGit;

partial class Program
{
    private static void ShowLog()
    {
        if (!AssertRepoInitialized()) return;

        string currentBranch = GetCurrentBranch();
        string branchFile = Path.Combine(HeadsDir, currentBranch);
        string commitHash = File.Exists(branchFile) ? File.ReadAllText(branchFile).Trim() : "";

        if (string.IsNullOrEmpty(commitHash))
        {
            ShowWarning("Коммитов нет.");
            return;
        }

        var visited = new HashSet<string>();

        while (!string.IsNullOrEmpty(commitHash) && commitHash != "none" && visited.Add(commitHash))
        {
            string commitPath = Path.Combine(ObjectsDir, commitHash);
            if (!File.Exists(commitPath)) break;

            string[] lines = File.ReadAllLines(commitPath);
            string parent = "none";
            string date = "";
            string message = "";
            string author = "";

            foreach (var line in lines)
            {
                if (line.StartsWith("parent: ")) parent = line.Substring("parent: ".Length).Trim();
                else if (line.StartsWith("date: ")) date = line.Substring("date: ".Length).Trim();
                else if (line.StartsWith("message: ")) message = line.Substring("message: ".Length).Trim();
                else if (line.StartsWith("author: ")) author = line.Substring("author: ".Length).Trim();
            }

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"commit {commitHash}");
            Console.ResetColor();
            Console.WriteLine($"Author: {author}");
            Console.WriteLine($"Date:  {date}");
            Console.WriteLine($"\n    {message}\n");

            commitHash = parent;
        }
    }

    // --- ФУНКЦИОНАЛ ВЕТОК (BRANCH) ---
    private static void HandleBranch(string[] args)
    {
        if (!AssertRepoInitialized()) return;

        string currentBranch = GetCurrentBranch();

        if (args.Length < 2)
        {
            // Вывод списка веток
            var branches = Directory.GetFiles(HeadsDir).Select(Path.GetFileName).ToList();
            if (branches.Count == 0)
            {
                Console.WriteLine($"* {currentBranch} (нет коммитов)");
                return;
            }

            Console.WriteLine("Ветки:");
            foreach (var branch in branches)
            {
                if (branch == currentBranch)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine($"* {branch}");
                    Console.ResetColor();
                }
                else
                {
                    Console.WriteLine($"  {branch}");
                }
            }
        }
        else
        {
            // Создание ветки
            string newBranchName = args[1];
            string newBranchFile = Path.Combine(HeadsDir, newBranchName);

            if (File.Exists(newBranchFile))
            {
                ShowWarning($"Ветка '{newBranchName}' уже существует.");
                return;
            }

            // Новая ветка указывает на тот же коммит, что и текущая
            string currentBranchFile = Path.Combine(HeadsDir, currentBranch);
            string currentCommit = File.Exists(currentBranchFile) ? File.ReadAllText(currentBranchFile).Trim() : "";

            File.WriteAllText(newBranchFile, currentCommit);
            Console.WriteLine($"Создана ветка '{newBranchName}' на коммите '{(string.IsNullOrEmpty(currentCommit) ? "unborn" : currentCommit.Substring(0, 7))}'");
        }
    }

    // --- ПЕРЕКЛЮЧЕНИЕ (CHECKOUT) ---
    private static void HandleCheckout(string branchName)
    {
        if (!AssertRepoInitialized()) return;

        string targetBranchFile = Path.Combine(HeadsDir, branchName);
        if (!File.Exists(targetBranchFile))
        {
            ShowError($"Ветка '{branchName}' не существует.");
            return;
        }

        // Меняем активную ветку в HEAD
        File.WriteAllText(Path.Combine(GitDir, "HEAD"), branchName);
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"Переключено на ветку '{branchName}'");
        Console.ResetColor();

        // Восстанавливаем файлы этой ветки в рабочей директории!
        string commitHash = File.ReadAllText(targetBranchFile).Trim();
        if (!string.IsNullOrEmpty(commitHash))
        {
            RestoreFilesFromCommit(commitHash);
        }
        else
        {
            ShowWarning("Ветка пуста, восстанавливать нечего.");
        }
    }

    private static void RestoreFilesFromCommit(string commitHash)
    {
        var files = GetFilesFromCommit(commitHash);
        foreach (var file in files)
        {
            string objectPath = Path.Combine(ObjectsDir, file.Value);
            if (File.Exists(objectPath))
            {
                string dir = Path.GetDirectoryName(file.Key);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                File.WriteAllBytes(file.Key, File.ReadAllBytes(objectPath));
                Console.WriteLine($"Восстановлен файл: {file.Key}");
            }
        }
    }

    // --- СЛИЯНИЕ (MERGE) ---
    private static void HandleMerge(string targetBranch)
    {
        if (!AssertRepoInitialized()) return;

        string currentBranch = GetCurrentBranch();
        if (currentBranch == targetBranch)
        {
            ShowWarning("Нельзя слить ветку саму в себя.");
            return;
        }

        string currentBranchFile = Path.Combine(HeadsDir, currentBranch);
        string targetBranchFile = Path.Combine(HeadsDir, targetBranch);

        if (!File.Exists(targetBranchFile))
        {
            ShowError($"Ветка '{targetBranch}' не существует.");
            return;
        }

        string currentCommit = File.Exists(currentBranchFile) ? File.ReadAllText(currentBranchFile).Trim() : "";
        string targetCommit = File.ReadAllText(targetBranchFile).Trim();

        if (string.IsNullOrEmpty(targetCommit))
        {
            ShowWarning($"Ветка '{targetBranch}' пуста, нечего сливать.");
            return;
        }

        if (currentCommit == targetCommit)
        {
            Console.WriteLine("Уже обновлено (Already up to date).");
            return;
        }

        // Fast-Forward слияние (если на текущей ветке еще нет коммитов)
        if (string.IsNullOrEmpty(currentCommit))
        {
            File.WriteAllText(currentBranchFile, targetCommit);
            RestoreFilesFromCommit(targetCommit);
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Fast-forward: '{currentBranch}' теперь указывает на {targetCommit.Substring(0, 7)}");
            Console.ResetColor();
            return;
        }

        // Слияние файлов из двух коммитов
        var currentFiles = GetFilesFromCommit(currentCommit);
        var targetFiles = GetFilesFromCommit(targetCommit);
        var mergedFiles = new Dictionary<string, string>(currentFiles);

        bool conflictResolved = false;
        foreach (var file in targetFiles)
        {
            if (mergedFiles.ContainsKey(file.Key))
            {
                if (mergedFiles[file.Key] != file.Value)
                {
                    // Конфликт! В нашей мини-версии мы просто берём версию из сливаемой ветки, предупреждая юзера
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.WriteLine($"Авто-слияние конфликта в '{file.Key}': взята версия из ветки '{targetBranch}'");
                    Console.ResetColor();
                    mergedFiles[file.Key] = file.Value;
                    conflictResolved = true;
                }
            }
            else
            {
                mergedFiles[file.Key] = file.Value;
            }
        }

        foreach (var file in mergedFiles)
        {
            string objectPath = Path.Combine(ObjectsDir, file.Value);
            if (File.Exists(objectPath))
            {
                string dir = Path.GetDirectoryName(file.Key);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                File.WriteAllBytes(file.Key, File.ReadAllBytes(objectPath));
            }
        }

        string mergeTreeHash = BuildTree(mergedFiles.Select(f => (f.Key, f.Value)).ToList());

        StringBuilder commitBuilder = new StringBuilder();
        commitBuilder.AppendLine($"parent: {currentCommit}");
        commitBuilder.AppendLine($"parent2: {targetCommit}");
        commitBuilder.AppendLine($"author: {GetAuthorName()}");
        commitBuilder.AppendLine($"date: {FormatNow()}");
        commitBuilder.AppendLine($"message: Merge branch '{targetBranch}' into '{currentBranch}'");
        commitBuilder.AppendLine($"tree: {mergeTreeHash}");

        string commitContent = commitBuilder.ToString();
        string commitHash = GetSha1Hash(Encoding.UTF8.GetBytes(commitContent));

        File.WriteAllText(Path.Combine(ObjectsDir, commitHash), commitContent);
        File.WriteAllText(currentBranchFile, commitHash);

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\nУспешное слияние! Создан коммит слияния {commitHash.Substring(0, 7)}");
        Console.ResetColor();
    }

    // --- ТЕГИ (TAGS) ---
    private static void HandleTag(string[] args)
    {
        if (!AssertRepoInitialized()) return;

        if (args.Length < 2)
        {
            var tags = Directory.GetFiles(TagsDir).Select(Path.GetFileName).ToList();
            if (!tags.Any())
            {
                Console.WriteLine("Тегов нет.");
                return;
            }

            Console.WriteLine("Теги:");
            foreach (var tag in tags)
            {
                string commit = File.ReadAllText(Path.Combine(TagsDir, tag)).Trim();
                Console.WriteLine($"  {tag} -> {commit.Substring(0, 7)}");
            }
        }
        else
        {
            string tagName = args[1];
            string currentBranch = GetCurrentBranch();
            string currentBranchFile = Path.Combine(HeadsDir, currentBranch);
            string currentCommit = File.Exists(currentBranchFile) ? File.ReadAllText(currentBranchFile).Trim() : "";

            if (string.IsNullOrEmpty(currentCommit))
            {
                ShowError("Нельзя создать тег: на текущей ветке еще нет коммитов.");
                return;
            }

            string tagFile = Path.Combine(TagsDir, tagName);
            File.WriteAllText(tagFile, currentCommit);

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"Создан тег '{tagName}' на коммите {currentCommit.Substring(0, 7)}");
            Console.ResetColor();
        }
    }

    // --- ОТКАТ (RESET --HARD) ---
    private static void HandleReset(string[] args)
    {
        if (!AssertRepoInitialized()) return;

        if (args.Length < 2 || args[1] != "--hard")
        {
            ShowError("Использование: reset --hard");
            return;
        }

        string currentBranch = GetCurrentBranch();
        string branchFile = Path.Combine(HeadsDir, currentBranch);
        string commitHash = File.Exists(branchFile) ? File.ReadAllText(branchFile).Trim() : "";

        if (string.IsNullOrEmpty(commitHash))
        {
            ShowWarning("Нет коммитов для отката.");
            return;
        }

        var committedFiles = GetFilesFromCommit(commitHash);
        var ignorePatterns = LoadIgnorePatterns();

        var workingFiles = Directory.GetFiles(Directory.GetCurrentDirectory(), "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Directory.GetCurrentDirectory(), f).Replace('\\', '/'))
            .Where(f => !f.StartsWith(".classgit/"))
            .Where(f => !IsIgnored(f, ignorePatterns))
            .ToList();

        foreach (var file in workingFiles)
        {
            if (!committedFiles.ContainsKey(file))
            {
                File.Delete(file);
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Удалён файл: {file}");
                Console.ResetColor();
            }
        }

        RestoreFilesFromCommit(commitHash);

        File.WriteAllText(Path.Combine(GitDir, "index"), "");

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\nРабочая директория сброшена к коммиту {commitHash.Substring(0, 7)}");
        Console.ResetColor();
    }
}