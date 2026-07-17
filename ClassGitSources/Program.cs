using System;
using System.IO;

namespace ClassGit;

partial class Program
{
    private static readonly string GitDir = Path.Combine(Directory.GetCurrentDirectory(), ".classgit");

    private static readonly string ObjectsDir = Path.Combine(GitDir, "objects");

    private static readonly string HeadsDir = Path.Combine(GitDir, "refs", "heads");

    private static readonly string TagsDir = Path.Combine(GitDir, "refs", "tags");

    private static readonly string HooksDir = Path.Combine(GitDir, "hooks");

    static void Main(string[] args)
    {
        if (args.Length > 0)
        {
            ExecuteCommand(args);
            return;
        }

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("=== Добро пожаловать в ClassGit CLI! ===");
        Console.WriteLine("Введите 'help' для списка команд или 'exit' для выхода.");
        Console.ResetColor();

        while (true)
        {
            Console.Write("\nclassgit > ");
            string? input = Console.ReadLine();

            if (string.IsNullOrWhiteSpace(input)) continue;

            string[] parts = input.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0) continue;

            if (parts[0].ToLower() == "exit")
            {
                Console.WriteLine("Пока!");
                break;
            }

            ExecuteCommand(parts);
        }
    }

    private static void ExecuteCommand(string[] args)
    {
        string command = args[0].ToLower();

        switch (command)
        {
            case "help":
                PrintHelp();
                break;
            case "init":
                InitRepository();
                break;
            case "add":
                if (args.Length < 2) ShowError("Укажите файл, папку или '.'. Пример: add test.txt / add src / add .");
                else AddToStage(args[1]);
                break;
            case "commit":
                if (args.Length < 2) ShowError("Укажите сообщение коммита. Пример: commit \"My commit\"");
                else CreateCommit(string.Join(" ", args[1..]).Trim('"'));
                break;
            case "status":
                ShowStatus();
                break;
            case "log":
                ShowLog();
                break;
            case "diff":
                HandleDiff(args);
                break;
            case "branch":
                HandleBranch(args);
                break;
            case "checkout":
                if (args.Length < 2) ShowError("Укажите ветку для перехода. Пример: checkout feature");
                else HandleCheckout(args[1]);
                break;
            case "tag":
                HandleTag(args);
                break;
            case "merge":
                if (args.Length < 2) ShowError("Укажите ветку для слияния. Пример: merge feature");
                else HandleMerge(args[1]);
                break;
            case "reset":
                HandleReset(args);
                break;
            case "serve":
                HandleServe(args);
                break;
            case "config":
                HandleConfig(args);
                break;
            default:
                ShowError($"Неизвестная команда: {command}. Введите 'help'.");
                break;
        }
    }

    private static void PrintHelp()
    {
        Console.WriteLine("\nДоступные команды:");
        Console.WriteLine("  init                    - Инициализировать репозиторий ClassGit");
        Console.WriteLine("  add <файл|папка|.>      - Добавить файл/папку (только новые и изменённые файлы)");
        Console.WriteLine("  commit <сообщение>       - Создать коммит на текущей ветке");
        Console.WriteLine("  status                  - Показать текущую ветку и файлы в индексе");
        Console.WriteLine("  log                     - История коммитов текущей ветки");
        Console.WriteLine("  diff [файл]              - Показать изменения относительно последнего коммита");
        Console.WriteLine("  branch                  - Список веток");
        Console.WriteLine("  branch <имя>            - Создать новую ветку");
        Console.WriteLine("  checkout <ветка>        - Перейти на ветку (и восстановить файлы!)");
        Console.WriteLine("  tag                     - Список тегов");
        Console.WriteLine("  tag <имя>               - Поставить тег на текущий коммит");
        Console.WriteLine("  merge <ветка>           - Слить указанную ветку в текущую");
        Console.WriteLine("  reset --hard            - Отменить все изменения, вернуть файлы к последнему коммиту");
        Console.WriteLine("  serve [порт]            - Запустить веб-сервер (просмотр веток/файлов/тегов в браузере)");
        Console.WriteLine("  config user.name <имя>  - Задать имя автора коммитов для этого репозитория");
        Console.WriteLine("  exit                    - Выйти из ClassGit CLI");
        Console.WriteLine("\nДополнительно:");
        Console.WriteLine("  .classgitignore         - Файл в корне репозитория с паттернами игнорируемых файлов");
        Console.WriteLine("  .classgit/hooks/pre-commit - Скрипт, который запускается перед каждым коммитом");
    }
}