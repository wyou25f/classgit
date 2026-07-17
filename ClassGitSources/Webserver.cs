using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using System.Globalization;

namespace ClassGit;

partial class Program
{
    // --- ВЕБ-СЕРВЕР (CLASSGIT WEB) ---
    private static void HandleServe(string[] args)
    {
        if (!AssertRepoInitialized()) return;

        int port = 8080;
        if (args.Length >= 2 && int.TryParse(args[1], out int parsedPort)) port = parsedPort;

        string prefix = $"http://localhost:{port}/";
        var listener = new HttpListener();
        listener.Prefixes.Add(prefix);

        try
        {
            listener.Start();
        }
        catch (Exception ex)
        {
            ShowError($"Не удалось запустить сервер на порту {port}: {ex.Message}");
            return;
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"\nClassGit web-сервер запущен: {prefix}");
        Console.WriteLine("Открой эту ссылку в браузере. Для остановки закрой приложение (Ctrl+C).");
        Console.ResetColor();

        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = listener.GetContext();
            }
            catch
            {
                break;
            }

            try
            {
                HandleHttpRequest(context);
            }
            catch (Exception ex)
            {
                try
                {
                    context.Response.StatusCode = 500;
                    WriteHtmlResponse(context, RenderMessagePage("Ошибка сервера", ex.Message));
                }
                catch
                {
                }
            }
        }
    }

    private static void HandleHttpRequest(HttpListenerContext context)
    {
        string path = context.Request.Url?.AbsolutePath ?? "/";
        var query = context.Request.QueryString;

        if (context.Request.HttpMethod == "POST" && path.StartsWith("/api/"))
        {
            HandleApiPost(context, path);
            return;
        }

        if (path == "/" || path == "/index.html")
        {
            WriteHtmlResponse(context, RenderHomePage());
        }
        else if (path == "/style.css")
        {
            WriteCssResponse(context, GetCss());
        }
        else if (path == "/workspace")
        {
            WriteHtmlResponse(context, RenderWorkspacePage(query["msg"] ?? ""));
        }
        else if (path == "/tags")
        {
            WriteHtmlResponse(context, RenderTagsPage());
        }
        else if (path == "/log")
        {
            string branchName = query["branch"] ?? GetCurrentBranch();
            WriteHtmlResponse(context, RenderLogPage(branchName));
        }
        else if (path.StartsWith("/branch/"))
        {
            string branchName = Uri.UnescapeDataString(path.Substring("/branch/".Length));
            string filePath = query["path"];
            string dir = query["dir"] ?? "";

            if (!string.IsNullOrEmpty(filePath))
            {
                WriteHtmlResponse(context, RenderFilePage(branchName, filePath));
            }
            else
            {
                WriteHtmlResponse(context, RenderBranchPage(branchName, dir));
            }
        }
        else
        {
            context.Response.StatusCode = 404;
            WriteHtmlResponse(context, RenderMessagePage("404", "Страница не найдена."));
        }
    }

    private static void HandleApiPost(HttpListenerContext context, string path)
    {
        string body = ReadRequestBody(context);
        var form = ParseFormBody(body);

        string GetField(string name) => form.TryGetValue(name, out var v) && v.Count > 0 ? v[0] : "";
        List<string> GetFieldList(string name) => form.TryGetValue(name, out var v) ? v : new List<string>();

        string message;

        try
        {
            switch (path)
            {
                case "/api/add":
                    var files = GetFieldList("files");
                    foreach (var f in files) AddToStage(f);
                    message = files.Count > 0 ? $"Добавлено файлов: {files.Count}" : "Ничего не выбрано";
                    break;

                case "/api/commit":
                    string commitMsg = GetField("message");
                    if (string.IsNullOrWhiteSpace(commitMsg))
                    {
                        message = "Введите сообщение коммита";
                    }
                    else
                    {
                        CreateCommit(commitMsg);
                        message = "Коммит создан";
                    }
                    break;

                case "/api/branch/create":
                    string newBranchName = GetField("name");
                    if (string.IsNullOrWhiteSpace(newBranchName))
                    {
                        message = "Введите имя ветки";
                    }
                    else
                    {
                        HandleBranch(new[] { "branch", newBranchName });
                        message = $"Ветка '{newBranchName}' создана";
                    }
                    break;

                case "/api/checkout":
                    string checkoutBranch = GetField("branch");
                    HandleCheckout(checkoutBranch);
                    message = $"Переключено на '{checkoutBranch}'";
                    break;

                case "/api/merge":
                    string mergeBranch = GetField("branch");
                    HandleMerge(mergeBranch);
                    message = $"Слияние с '{mergeBranch}' выполнено";
                    break;

                case "/api/tag":
                    string tagName = GetField("name");
                    if (string.IsNullOrWhiteSpace(tagName))
                    {
                        message = "Введите имя тега";
                    }
                    else
                    {
                        HandleTag(new[] { "tag", tagName });
                        message = $"Тег '{tagName}' создан";
                    }
                    break;

                case "/api/reset":
                    HandleReset(new[] { "reset", "--hard" });
                    message = "Рабочая директория сброшена к последнему коммиту";
                    break;

                default:
                    context.Response.StatusCode = 404;
                    WriteHtmlResponse(context, RenderMessagePage("404", "Неизвестное действие."));
                    return;
            }
        }
        catch (Exception ex)
        {
            message = "Ошибка: " + ex.Message;
        }

        string redirectUrl = "/workspace?msg=" + Uri.EscapeDataString(message);
        context.Response.StatusCode = 302;
        context.Response.RedirectLocation = redirectUrl;
        context.Response.OutputStream.Close();
    }

    private static string ReadRequestBody(HttpListenerContext context)
    {
        using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding ?? Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static Dictionary<string, List<string>> ParseFormBody(string body)
    {
        var result = new Dictionary<string, List<string>>();
        if (string.IsNullOrEmpty(body)) return result;

        foreach (var pair in body.Split('&'))
        {
            if (string.IsNullOrEmpty(pair)) continue;

            var kv = pair.Split('=', 2);
            string key = Uri.UnescapeDataString(kv[0].Replace('+', ' '));
            string value = kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : "";

            if (!result.ContainsKey(key)) result[key] = new List<string>();
            result[key].Add(value);
        }

        return result;
    }

    private static List<(string status, string path)> GetWorkingTreeChanges()
    {
        string lastCommitHash = GetLastCommitHashForCurrentBranch();
        var committedFiles = string.IsNullOrEmpty(lastCommitHash) ? new Dictionary<string, string>() : GetFilesFromCommit(lastCommitHash);
        var ignorePatterns = LoadIgnorePatterns();

        var workingFiles = Directory.GetFiles(Directory.GetCurrentDirectory(), "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Directory.GetCurrentDirectory(), f).Replace('\\', '/'))
            .Where(f => !f.StartsWith(".classgit/"))
            .Where(f => !IsIgnored(f, ignorePatterns))
            .ToList();

        var workingSet = new HashSet<string>(workingFiles);
        var result = new List<(string status, string path)>();

        foreach (var f in workingFiles)
        {
            byte[] bytes = File.ReadAllBytes(f);
            string hash = GetSha1Hash(bytes);

            if (!committedFiles.TryGetValue(f, out var committedHash))
            {
                result.Add(("new", f));
            }
            else if (committedHash != hash)
            {
                result.Add(("modified", f));
            }
        }

        foreach (var committedPath in committedFiles.Keys)
        {
            if (!workingSet.Contains(committedPath))
            {
                result.Add(("deleted", committedPath));
            }
        }

        return result.OrderBy(r => r.path, StringComparer.Ordinal).ToList();
    }

    private static string RenderWorkspacePage(string message)
    {
        var sb = new StringBuilder();
        sb.Append(PageHeader("Рабочая область"));

        if (!string.IsNullOrEmpty(message))
        {
            sb.Append($"<div class=\"flash\">{HtmlEscape(message)}</div>");
        }

        sb.Append("<p><a href=\"/\" class=\"back-link\">" + IconBack() + " на главную</a></p>");
        sb.Append($"<h1>{IconGear()} Рабочая область</h1>");

        string currentBranch = GetCurrentBranch();
        sb.Append($"<p class=\"muted\">Текущая ветка: <strong>{HtmlEscape(currentBranch)}</strong></p>");

        var changes = GetWorkingTreeChanges();
        var indexMap = LoadIndexMap(Path.Combine(GitDir, "index"));

        sb.Append("<section class=\"panel\">");
        sb.Append($"<h2>{IconFile()} Изменения в рабочей директории</h2>");

        if (changes.Count == 0)
        {
            sb.Append("<p class=\"muted\">Рабочая директория чистая — изменений нет.</p>");
        }
        else
        {
            sb.Append("<form method=\"post\" action=\"/api/add\">");
            sb.Append("<ul class=\"change-list\">");

            foreach (var (status, filePath) in changes)
            {
                string cls = status switch { "new" => "st-new", "modified" => "st-mod", "deleted" => "st-del", _ => "" };
                string label = status switch { "new" => "новый", "modified" => "изменён", "deleted" => "удалён", _ => status };
                bool alreadyStaged = indexMap.ContainsKey(filePath);

                sb.Append("<li><label>");
                sb.Append($"<input type=\"checkbox\" name=\"files\" value=\"{HtmlEscape(filePath)}\"{(alreadyStaged ? " checked" : "")}> ");
                sb.Append($"<span class=\"{cls}\">{label}</span> {HtmlEscape(filePath)}");
                sb.Append("</label></li>");
            }

            sb.Append("</ul>");
            sb.Append("<button type=\"submit\">Добавить выбранное</button>");
            sb.Append("</form>");
        }

        sb.Append("</section>");

        sb.Append("<section class=\"panel\">");
        sb.Append($"<h2>{IconBox()} Индекс (готово к коммиту)</h2>");

        if (indexMap.Count == 0)
        {
            sb.Append("<p class=\"muted\">Индекс пуст. Сначала добавьте файлы выше.</p>");
        }
        else
        {
            sb.Append("<ul class=\"staged-list\">");
            foreach (var stagedPath in indexMap.Keys.OrderBy(k => k, StringComparer.Ordinal))
            {
                sb.Append($"<li>{IconFile()} {HtmlEscape(stagedPath)}</li>");
            }
            sb.Append("</ul>");

            sb.Append("<form method=\"post\" action=\"/api/commit\">");
            sb.Append("<textarea name=\"message\" rows=\"3\" placeholder=\"Сообщение коммита...\"></textarea>");
            sb.Append("<button type=\"submit\">Закоммитить</button>");
            sb.Append("</form>");
        }

        sb.Append("</section>");

        var branches = Directory.Exists(HeadsDir)
            ? Directory.GetFiles(HeadsDir).Select(Path.GetFileName).OrderBy(b => b, StringComparer.Ordinal).ToList()
            : new List<string>();

        sb.Append("<section class=\"panel\">");
        sb.Append($"<h2>{IconBranch()} Ветки</h2>");
        sb.Append("<ul class=\"branch-manage-list\">");

        foreach (var branch in branches)
        {
            sb.Append("<li>");
            sb.Append(branch == currentBranch
                ? $"<strong>{HtmlEscape(branch)}</strong> <span class=\"badge\">текущая</span>"
                : HtmlEscape(branch));

            if (branch != currentBranch)
            {
                sb.Append($"<form method=\"post\" action=\"/api/checkout\" class=\"inline-form\">" +
                          $"<input type=\"hidden\" name=\"branch\" value=\"{HtmlEscape(branch)}\">" +
                          "<button type=\"submit\">Переключиться</button></form>");
                sb.Append($"<form method=\"post\" action=\"/api/merge\" class=\"inline-form\">" +
                          $"<input type=\"hidden\" name=\"branch\" value=\"{HtmlEscape(branch)}\">" +
                          $"<button type=\"submit\">Слить в {HtmlEscape(currentBranch)}</button></form>");
            }

            sb.Append("</li>");
        }

        sb.Append("</ul>");
        sb.Append("<form method=\"post\" action=\"/api/branch/create\" class=\"inline-form\">");
        sb.Append("<input type=\"text\" name=\"name\" placeholder=\"Имя новой ветки\">");
        sb.Append("<button type=\"submit\">Создать ветку</button>");
        sb.Append("</form>");
        sb.Append("</section>");

        var tags = Directory.Exists(TagsDir)
            ? Directory.GetFiles(TagsDir).Select(Path.GetFileName).OrderBy(t => t, StringComparer.Ordinal).ToList()
            : new List<string>();

        sb.Append("<section class=\"panel\">");
        sb.Append($"<h2>{IconTag()} Теги</h2>");

        if (tags.Count > 0)
        {
            sb.Append("<ul>");
            foreach (var tag in tags) sb.Append($"<li>{IconTag()} {HtmlEscape(tag)}</li>");
            sb.Append("</ul>");
        }

        sb.Append("<form method=\"post\" action=\"/api/tag\" class=\"inline-form\">");
        sb.Append("<input type=\"text\" name=\"name\" placeholder=\"Имя тега\">");
        sb.Append("<button type=\"submit\">Создать тег на HEAD</button>");
        sb.Append("</form>");
        sb.Append("</section>");

        sb.Append("<section class=\"panel panel-danger\">");
        sb.Append($"<h2>{IconWarning()} Опасная зона</h2>");
        sb.Append("<p class=\"muted\">Отменяет все несохранённые изменения и удаляет файлы, которых нет в последнем коммите. Действие необратимо.</p>");
        sb.Append("<form method=\"post\" action=\"/api/reset\" onsubmit=\"return confirm('Точно сбросить все изменения без возможности отмены?');\">");
        sb.Append("<button type=\"submit\" class=\"btn-danger\">reset --hard</button>");
        sb.Append("</form>");
        sb.Append("</section>");

        sb.Append(PageFooter());
        return sb.ToString();
    }

    private static void WriteHtmlResponse(HttpListenerContext context, string html)
    {
        byte[] buffer = Encoding.UTF8.GetBytes(html);
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = buffer.Length;
        context.Response.OutputStream.Write(buffer, 0, buffer.Length);
        context.Response.OutputStream.Close();
    }

    private static void WriteCssResponse(HttpListenerContext context, string css)
    {
        byte[] buffer = Encoding.UTF8.GetBytes(css);
        context.Response.ContentType = "text/css; charset=utf-8";
        context.Response.ContentLength64 = buffer.Length;
        context.Response.OutputStream.Write(buffer, 0, buffer.Length);
        context.Response.OutputStream.Close();
    }

    private static string RenderHomePage()
    {
        var branches = Directory.Exists(HeadsDir)
            ? Directory.GetFiles(HeadsDir).Select(Path.GetFileName).OrderBy(b => b, StringComparer.Ordinal).ToList()
            : new List<string>();

        string currentBranch = GetCurrentBranch();

        var sb = new StringBuilder();
        sb.Append(PageHeader("ClassGit"));
        sb.Append("<h1>" + IconBox() + " ClassGit репозиторий</h1>");
        sb.Append($"<p class=\"muted\">{HtmlEscape(Directory.GetCurrentDirectory())}</p>");
        sb.Append("<h2>Ветки</h2>");

        if (branches.Count == 0)
        {
            sb.Append("<p class=\"muted\">Пока нет ни одной ветки с коммитами.</p>");
        }
        else
        {
            sb.Append("<ul class=\"branch-list\">");
            foreach (var branch in branches)
            {
                string badge = branch == currentBranch ? "<span class=\"badge\">текущая</span>" : "";
                sb.Append($"<li>{IconBranch()} <a href=\"/branch/{Uri.EscapeDataString(branch)}\">{HtmlEscape(branch)}</a> {badge}</li>");
            }
            sb.Append("</ul>");
        }

        sb.Append("<p><a href=\"/tags\">" + IconTag() + " Все теги</a></p>");
        sb.Append(PageFooter());
        return sb.ToString();
    }

    private static string RenderBranchPage(string branchName, string dir)
    {
        string branchFile = Path.Combine(HeadsDir, branchName);
        if (!File.Exists(branchFile))
        {
            return RenderMessagePage("404", $"Ветка '{branchName}' не найдена.");
        }

        string commitHash = File.ReadAllText(branchFile).Trim();

        var sb = new StringBuilder();
        sb.Append(PageHeader(branchName));
        sb.Append("<p><a href=\"/\" class=\"back-link\">" + IconBack() + " ко всем веткам</a></p>");
        sb.Append($"<h1>{IconBranch()} {RenderBreadcrumb(branchName, dir)}</h1>");

        if (string.IsNullOrEmpty(commitHash))
        {
            sb.Append("<p class=\"muted\">В этой ветке пока нет коммитов.</p>");
            sb.Append(PageFooter());
            return sb.ToString();
        }

        sb.Append($"<p class=\"muted\">Последний коммит: <code>{commitHash.Substring(0, 7)}</code> " +
                  $"<button class=\"copy-btn\" onclick=\"copyText('{commitHash}')\" title=\"Скопировать полный хэш\">{IconCopy()}</button> — " +
                  $"<a href=\"/log?branch={Uri.EscapeDataString(branchName)}\">история</a></p>");

        var files = GetFilesFromCommit(commitHash);
        var children = GetImmediateChildren(files, dir);

        if (children.Count == 0)
        {
            sb.Append("<p class=\"muted\">Папка пуста.</p>");
            sb.Append(PageFooter());
            return sb.ToString();
        }

        var commitCache = new Dictionary<string, Dictionary<string, string>>();

        if (children.Count > 15)
        {
            sb.Append("<input type=\"text\" class=\"filter-input\" placeholder=\"Фильтр по имени...\" oninput=\"filterFiles(this.value)\">");
        }

        sb.Append("<table class=\"file-table\">");
        sb.Append("<thead><tr><th>Имя</th><th>Коммит</th><th>Автор</th><th>Изменено</th><th>Размер</th></tr></thead><tbody>");

        foreach (var child in children)
        {
            var lastCommit = FindLastCommitForPath(commitHash, child.fullPath, child.isDir, commitCache);

            string icon = child.isDir ? IconFolder() : IconFile();
            string link = child.isDir
                ? $"/branch/{Uri.EscapeDataString(branchName)}?dir={Uri.EscapeDataString(child.fullPath)}"
                : $"/branch/{Uri.EscapeDataString(branchName)}?path={Uri.EscapeDataString(child.fullPath)}";

            string message = lastCommit?.Message ?? "";
            string author = lastCommit?.Author ?? "";
            string ageCell = "";

            if (lastCommit != null &&
                DateTime.TryParseExact(lastCommit.Value.Date, "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate))
            {
                string cls = GetAgeColorClass(parsedDate);
                ageCell = $"<span class=\"{cls}\">{HtmlEscape(FormatRelativeAge(parsedDate))}</span>";
            }

            string sizeCell = "";
            if (!child.isDir && files.TryGetValue(child.fullPath, out var blobHash))
            {
                string objectPath = Path.Combine(ObjectsDir, blobHash);
                if (File.Exists(objectPath)) sizeCell = FormatSize(new FileInfo(objectPath).Length);
            }

            sb.Append($"<tr data-name=\"{HtmlEscape(child.name.ToLowerInvariant())}\">");
            sb.Append($"<td>{icon} <a href=\"{link}\">{HtmlEscape(child.name)}</a></td>");
            sb.Append($"<td class=\"msg\">{HtmlEscape(message)}</td>");
            sb.Append($"<td class=\"author\">{HtmlEscape(author)}</td>");
            sb.Append($"<td>{ageCell}</td>");
            sb.Append($"<td class=\"size\">{HtmlEscape(sizeCell)}</td>");
            sb.Append("</tr>");
        }

        sb.Append("</tbody></table>");

        var readmeChild = children.FirstOrDefault(c => !c.isDir && c.name.Equals("README.md", StringComparison.OrdinalIgnoreCase));
        if (readmeChild.fullPath != null && files.TryGetValue(readmeChild.fullPath, out var readmeHash))
        {
            string readmeObjectPath = Path.Combine(ObjectsDir, readmeHash);
            if (File.Exists(readmeObjectPath))
            {
                string readmeContent = File.ReadAllText(readmeObjectPath);
                sb.Append("<div class=\"readme\">");
                sb.Append($"<div class=\"readme-header\">{IconFile()} {HtmlEscape(readmeChild.name)}</div>");
                sb.Append($"<pre class=\"readme-body\">{HtmlEscape(readmeContent)}</pre>");
                sb.Append("</div>");
            }
        }

        sb.Append(PageFooter());
        return sb.ToString();
    }

    private static string RenderBreadcrumb(string branchName, string dir)
    {
        var sb = new StringBuilder();
        sb.Append($"<a href=\"/branch/{Uri.EscapeDataString(branchName)}\">{HtmlEscape(branchName)}</a>");

        if (!string.IsNullOrEmpty(dir))
        {
            string accum = "";
            foreach (var part in dir.Split('/'))
            {
                accum = accum.Length == 0 ? part : accum + "/" + part;
                sb.Append(" / <a href=\"/branch/" + Uri.EscapeDataString(branchName) + "?dir=" + Uri.EscapeDataString(accum) + "\">" + HtmlEscape(part) + "</a>");
            }
        }

        return sb.ToString();
    }

    private static List<(string name, string fullPath, bool isDir)> GetImmediateChildren(Dictionary<string, string> files, string dirPrefix)
    {
        string prefix = string.IsNullOrEmpty(dirPrefix) ? "" : dirPrefix + "/";
        var seenDirs = new HashSet<string>();
        var seenFiles = new HashSet<string>();
        var result = new List<(string, string, bool)>();

        foreach (var key in files.Keys)
        {
            if (!key.StartsWith(prefix)) continue;

            string rest = key.Substring(prefix.Length);
            if (rest.Length == 0) continue;

            int slash = rest.IndexOf('/');
            if (slash < 0)
            {
                if (seenFiles.Add(rest)) result.Add((rest, prefix + rest, false));
            }
            else
            {
                string dirName = rest.Substring(0, slash);
                if (seenDirs.Add(dirName)) result.Add((dirName, prefix + dirName, true));
            }
        }

        return result
            .OrderBy(r => !r.Item3)
            .ThenBy(r => r.Item1, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static (string Hash, string Message, string Author, string Date)? FindLastCommitForPath(
        string startCommitHash, string path, bool isDirectory, Dictionary<string, Dictionary<string, string>> commitCache)
    {
        Dictionary<string, string> GetFilesCached(string commitHash)
        {
            if (string.IsNullOrEmpty(commitHash) || commitHash == "none") return new Dictionary<string, string>();
            if (commitCache.TryGetValue(commitHash, out var cached)) return cached;
            var files = GetFilesFromCommit(commitHash);
            commitCache[commitHash] = files;
            return files;
        }

        Dictionary<string, string> Filter(Dictionary<string, string> files)
        {
            if (isDirectory)
            {
                string prefix = path.Length == 0 ? "" : path + "/";
                return files.Where(f => f.Key.StartsWith(prefix)).ToDictionary(f => f.Key, f => f.Value);
            }

            return files.TryGetValue(path, out var hash)
                ? new Dictionary<string, string> { [path] = hash }
                : new Dictionary<string, string>();
        }

        string current = startCommitHash;
        var visited = new HashSet<string>();

        while (!string.IsNullOrEmpty(current) && current != "none" && visited.Add(current))
        {
            string commitPath = Path.Combine(ObjectsDir, current);
            if (!File.Exists(commitPath)) break;

            string parent = "none", date = "", message = "", author = "";
            foreach (var line in File.ReadAllLines(commitPath))
            {
                if (line.StartsWith("parent: ")) parent = line.Substring("parent: ".Length).Trim();
                else if (line.StartsWith("date: ")) date = line.Substring("date: ".Length).Trim();
                else if (line.StartsWith("message: ")) message = line.Substring("message: ".Length).Trim();
                else if (line.StartsWith("author: ")) author = line.Substring("author: ".Length).Trim();
            }

            var currentFiltered = Filter(GetFilesCached(current));
            var parentFiltered = Filter(GetFilesCached(parent));

            bool changed = currentFiltered.Count != parentFiltered.Count ||
                           currentFiltered.Any(kv => !parentFiltered.TryGetValue(kv.Key, out var v) || v != kv.Value);

            if (changed)
            {
                return (current, message, string.IsNullOrEmpty(author) ? "неизвестно" : author, date);
            }

            current = parent;
        }

        return null;
    }

    private static string GetAgeColorClass(DateTime commitDate)
    {
        var age = DateTime.Now - commitDate;
        if (age.TotalDays <= 1) return "age-green";
        if (age.TotalDays <= 21) return "age-yellow";
        return "age-red";
    }

    private static string FormatRelativeAge(DateTime date)
    {
        var span = DateTime.Now - date;
        if (span.TotalMinutes < 1) return "только что";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} мин. назад";
        if (span.TotalDays < 1) return $"{(int)span.TotalHours} ч. назад";
        if (span.TotalDays < 30) return $"{(int)span.TotalDays} дн. назад";
        if (span.TotalDays < 365) return $"{(int)(span.TotalDays / 30)} мес. назад";
        return $"{(int)(span.TotalDays / 365)} г. назад";
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:0.#} KB";
        return $"{bytes / (1024.0 * 1024.0):0.#} MB";
    }

    private static string RenderFilePage(string branchName, string filePath)
    {
        string branchFile = Path.Combine(HeadsDir, branchName);
        if (!File.Exists(branchFile))
        {
            return RenderMessagePage("404", $"Ветка '{branchName}' не найдена.");
        }

        string commitHash = File.ReadAllText(branchFile).Trim();
        var files = GetFilesFromCommit(commitHash);

        if (!files.ContainsKey(filePath))
        {
            return RenderMessagePage("404", $"Файл '{filePath}' не найден в ветке '{branchName}'.");
        }

        string objectPath = Path.Combine(ObjectsDir, files[filePath]);
        string content = File.Exists(objectPath) ? File.ReadAllText(objectPath) : "";

        var sb = new StringBuilder();
        sb.Append(PageHeader(filePath));
        sb.Append($"<p><a href=\"/branch/{Uri.EscapeDataString(branchName)}\" class=\"back-link\">{IconBack()} назад к дереву файлов</a></p>");
        sb.Append($"<h1>{IconFile()} {HtmlEscape(filePath)}</h1>");
        sb.Append("<pre class=\"code\"><code id=\"src\">" + HtmlEscape(content) + "</code></pre>");
        sb.Append("<button onclick=\"copySource()\">Скопировать</button>");
        sb.Append("<script>function copySource(){navigator.clipboard.writeText(document.getElementById('src').innerText);}</script>");
        sb.Append(PageFooter());
        return sb.ToString();
    }

    private static string RenderTagsPage()
    {
        var tags = Directory.Exists(TagsDir)
            ? Directory.GetFiles(TagsDir).Select(Path.GetFileName).OrderBy(t => t, StringComparer.Ordinal).ToList()
            : new List<string>();

        var sb = new StringBuilder();
        sb.Append(PageHeader("Теги"));
        sb.Append("<p><a href=\"/\" class=\"back-link\">" + IconBack() + " ко всем веткам</a></p>");
        sb.Append("<h1>" + IconTag() + " Теги</h1>");

        if (tags.Count == 0)
        {
            sb.Append("<p class=\"muted\">Тегов пока нет.</p>");
        }
        else
        {
            sb.Append("<ul>");
            foreach (var tag in tags)
            {
                string commitHash = File.ReadAllText(Path.Combine(TagsDir, tag)).Trim();
                string shortHash = commitHash.Substring(0, Math.Min(7, commitHash.Length));
                sb.Append($"<li>{IconTag()} <strong>{HtmlEscape(tag)}</strong> → <code>{shortHash}</code></li>");
            }
            sb.Append("</ul>");
        }

        sb.Append(PageFooter());
        return sb.ToString();
    }

    private static string RenderLogPage(string branchName)
    {
        string branchFile = Path.Combine(HeadsDir, branchName);
        string commitHash = File.Exists(branchFile) ? File.ReadAllText(branchFile).Trim() : "";

        var sb = new StringBuilder();
        sb.Append(PageHeader($"История: {branchName}"));
        sb.Append($"<p><a href=\"/branch/{Uri.EscapeDataString(branchName)}\" class=\"back-link\">{IconBack()} назад к файлам</a></p>");
        sb.Append($"<h1>{IconHistory()} История коммитов — {HtmlEscape(branchName)}</h1>");

        if (string.IsNullOrEmpty(commitHash))
        {
            sb.Append("<p class=\"muted\">Коммитов нет.</p>");
            sb.Append(PageFooter());
            return sb.ToString();
        }

        sb.Append("<ul class=\"log\">");
        var visited = new HashSet<string>();

        while (!string.IsNullOrEmpty(commitHash) && commitHash != "none" && visited.Add(commitHash))
        {
            string commitPath = Path.Combine(ObjectsDir, commitHash);
            if (!File.Exists(commitPath)) break;

            string parent = "none", date = "", message = "", author = "";
            foreach (var line in File.ReadAllLines(commitPath))
            {
                if (line.StartsWith("parent: ")) parent = line.Substring("parent: ".Length).Trim();
                else if (line.StartsWith("date: ")) date = line.Substring("date: ".Length).Trim();
                else if (line.StartsWith("message: ")) message = line.Substring("message: ".Length).Trim();
                else if (line.StartsWith("author: ")) author = line.Substring("author: ".Length).Trim();
            }

            sb.Append("<li><code>" + commitHash.Substring(0, 7) + "</code> <span class=\"muted\">" + HtmlEscape(date) + "</span> — <span class=\"author\">" + HtmlEscape(author) + "</span><br>" + HtmlEscape(message) + "</li>");
            commitHash = parent;
        }

        sb.Append("</ul>");
        sb.Append(PageFooter());
        return sb.ToString();
    }

    private static string RenderMessagePage(string title, string message)
    {
        var sb = new StringBuilder();
        sb.Append(PageHeader(title));
        sb.Append($"<h1>{HtmlEscape(title)}</h1>");
        sb.Append($"<p>{HtmlEscape(message)}</p>");
        sb.Append("<p><a href=\"/\" class=\"back-link\">" + IconBack() + " на главную</a></p>");
        sb.Append(PageFooter());
        return sb.ToString();
    }

    private static string PageHeader(string title)
    {
        return "<!DOCTYPE html>\n<html lang=\"ru\">\n<head>\n<meta charset=\"UTF-8\">\n" +
               $"<title>{HtmlEscape(title)} — ClassGit</title>\n" +
               "<link rel=\"stylesheet\" href=\"/style.css\">\n" +
               "<script>(function(){var t=localStorage.getItem('classgit-theme')||'dark';document.documentElement.setAttribute('data-theme',t);})();</script>\n" +
               "</head>\n<body>\n" +
               "<nav><a href=\"/\" class=\"logo\">" + IconBox() + " ClassGit</a>" +
               "<div class=\"nav-right\">" +
               "<a href=\"/workspace\" class=\"nav-link\">" + IconGear() + " Рабочая область</a>" +
               "<button id=\"themeToggle\" class=\"theme-toggle\" onclick=\"toggleTheme()\" title=\"Сменить тему\" aria-label=\"Сменить тему\">" +
               "<span class=\"icon-sun\">" + IconSun() + "</span><span class=\"icon-moon\">" + IconMoon() + "</span>" +
               "</button></div></nav>\n<main>\n";
    }

    private static string PageFooter()
    {
        return "\n</main>\n<script>\n" +
               "function toggleTheme(){var c=document.documentElement.getAttribute('data-theme')||'dark';var n=c==='dark'?'light':'dark';document.documentElement.setAttribute('data-theme',n);localStorage.setItem('classgit-theme',n);}\n" +
               "function filterFiles(q){q=q.toLowerCase();document.querySelectorAll('table.file-table tbody tr').forEach(function(row){var name=row.getAttribute('data-name')||'';row.style.display=name.indexOf(q)>-1?'':'none';});}\n" +
               "function copyText(text){navigator.clipboard.writeText(text);}\n" +
               "</script>\n</body>\n</html>";
    }

    private static string HtmlEscape(string text)
    {
        return WebUtility.HtmlEncode(text ?? "");
    }

    private static string IconBox()
    {
        return "<svg class=\"icon\" viewBox=\"0 0 16 16\" width=\"18\" height=\"18\" fill=\"none\" aria-hidden=\"true\"><path d=\"M2 5.2 8 2l6 3.2v6L8 14.4l-6-3.2v-6Z\" stroke=\"currentColor\" stroke-width=\"1.3\" stroke-linejoin=\"round\"/><path d=\"M2 5.2 8 8.4l6-3.2M8 8.4v6\" stroke=\"currentColor\" stroke-width=\"1.3\"/></svg>";
    }

    private static string IconBranch()
    {
        return "<svg class=\"icon\" viewBox=\"0 0 16 16\" width=\"16\" height=\"16\" fill=\"none\" aria-hidden=\"true\"><circle cx=\"4\" cy=\"3.2\" r=\"1.6\" stroke=\"currentColor\" stroke-width=\"1.3\"/><circle cx=\"4\" cy=\"12.8\" r=\"1.6\" stroke=\"currentColor\" stroke-width=\"1.3\"/><circle cx=\"12\" cy=\"7\" r=\"1.6\" stroke=\"currentColor\" stroke-width=\"1.3\"/><path d=\"M4 4.8V11.2\" stroke=\"currentColor\" stroke-width=\"1.3\"/><path d=\"M4 8.2c0-2 2-3 6.6-3.6\" stroke=\"currentColor\" stroke-width=\"1.3\" fill=\"none\"/></svg>";
    }

    private static string IconTag()
    {
        return "<svg class=\"icon\" viewBox=\"0 0 16 16\" width=\"16\" height=\"16\" fill=\"none\" aria-hidden=\"true\"><path d=\"M2 2h5.2l6.8 6.8-5.2 5.2L2 7.2V2Z\" stroke=\"currentColor\" stroke-width=\"1.3\" stroke-linejoin=\"round\"/><circle cx=\"5\" cy=\"5\" r=\"1\" fill=\"currentColor\"/></svg>";
    }

    private static string IconFolder()
    {
        return "<svg class=\"icon\" viewBox=\"0 0 20 16\" width=\"16\" height=\"16\" fill=\"none\" aria-hidden=\"true\"><path d=\"M1 4a1 1 0 0 1 1-1h4.5l1.6 2H18a1 1 0 0 1 1 1v8a1 1 0 0 1-1 1H2a1 1 0 0 1-1-1V4Z\" fill=\"currentColor\" opacity=\"0.85\"/></svg>";
    }

    private static string IconFile()
    {
        return "<svg class=\"icon\" viewBox=\"0 0 16 18\" width=\"14\" height=\"16\" fill=\"none\" aria-hidden=\"true\"><path d=\"M4 1h6l4 4v11a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V2a1 1 0 0 1 1-1Z\" stroke=\"currentColor\" stroke-width=\"1.2\"/><path d=\"M10 1v4h4\" stroke=\"currentColor\" stroke-width=\"1.2\"/></svg>";
    }

    private static string IconHistory()
    {
        return "<svg class=\"icon\" viewBox=\"0 0 16 16\" width=\"16\" height=\"16\" fill=\"none\" aria-hidden=\"true\"><circle cx=\"8\" cy=\"8\" r=\"6.3\" stroke=\"currentColor\" stroke-width=\"1.3\"/><path d=\"M8 4.5V8l3 2\" stroke=\"currentColor\" stroke-width=\"1.3\" stroke-linecap=\"round\"/></svg>";
    }

    private static string IconBack()
    {
        return "<svg class=\"icon\" viewBox=\"0 0 16 16\" width=\"16\" height=\"16\" fill=\"none\" aria-hidden=\"true\"><path d=\"M9.5 3 4 8l5.5 5\" stroke=\"currentColor\" stroke-width=\"1.6\" stroke-linecap=\"round\" stroke-linejoin=\"round\"/></svg>";
    }

    private static string IconCopy()
    {
        return "<svg class=\"icon\" viewBox=\"0 0 16 16\" width=\"14\" height=\"14\" fill=\"none\" aria-hidden=\"true\"><rect x=\"5\" y=\"5\" width=\"8\" height=\"9\" rx=\"1.3\" stroke=\"currentColor\" stroke-width=\"1.2\"/><path d=\"M3.5 10.5V3a1 1 0 0 1 1-1H11\" stroke=\"currentColor\" stroke-width=\"1.2\"/></svg>";
    }

    private static string IconSun()
    {
        return "<svg class=\"icon\" viewBox=\"0 0 16 16\" width=\"16\" height=\"16\" fill=\"none\" aria-hidden=\"true\"><circle cx=\"8\" cy=\"8\" r=\"3.2\" stroke=\"currentColor\" stroke-width=\"1.3\"/><path d=\"M8 1v1.6M8 13.4V15M1 8h1.6M13.4 8H15M3 3l1.2 1.2M11.8 11.8 13 13M13 3l-1.2 1.2M4.2 11.8 3 13\" stroke=\"currentColor\" stroke-width=\"1.3\" stroke-linecap=\"round\"/></svg>";
    }

    private static string IconMoon()
    {
        return "<svg class=\"icon\" viewBox=\"0 0 16 16\" width=\"16\" height=\"16\" fill=\"none\" aria-hidden=\"true\"><path d=\"M13 9.5A5.5 5.5 0 1 1 6.5 3a4.3 4.3 0 0 0 6.5 6.5Z\" stroke=\"currentColor\" stroke-width=\"1.3\" stroke-linejoin=\"round\"/></svg>";
    }

    private static string IconGear()
    {
        return "<svg class=\"icon\" viewBox=\"0 0 16 16\" width=\"16\" height=\"16\" fill=\"none\" aria-hidden=\"true\"><circle cx=\"8\" cy=\"8\" r=\"2.3\" stroke=\"currentColor\" stroke-width=\"1.3\"/><path d=\"M8 2v1.6M8 12.4V14M2 8h1.6M12.4 8H14M3.5 3.5l1.2 1.2M11.3 11.3l1.2 1.2M12.5 3.5l-1.2 1.2M4.7 11.3l-1.2 1.2\" stroke=\"currentColor\" stroke-width=\"1.3\" stroke-linecap=\"round\"/></svg>";
    }

    private static string IconWarning()
    {
        return "<svg class=\"icon\" viewBox=\"0 0 16 16\" width=\"16\" height=\"16\" fill=\"none\" aria-hidden=\"true\"><path d=\"M8 2 14.5 13.5H1.5Z\" stroke=\"currentColor\" stroke-width=\"1.3\" stroke-linejoin=\"round\"/><path d=\"M8 6.5V9.5\" stroke=\"currentColor\" stroke-width=\"1.3\" stroke-linecap=\"round\"/><circle cx=\"8\" cy=\"11.5\" r=\"0.8\" fill=\"currentColor\"/></svg>";
    }

    private static string GetCss()
    {
        return @"
:root,html[data-theme=""dark""]{--bg:#0d1117;--panel:#161b22;--border:#30363d;--text:#c9d1d9;--muted:#8b949e;--accent:#58a6ff;--green:#3fb950;--yellow:#d29922;--red:#f85149;}
html[data-theme=""light""]{--bg:#ffffff;--panel:#f6f8fa;--border:#d0d7de;--text:#1f2328;--muted:#59636e;--accent:#0969da;--green:#1a7f37;--yellow:#9a6700;--red:#cf222e;}
*{box-sizing:border-box;}
body{margin:0;background:var(--bg);color:var(--text);font-family:-apple-system,Segoe UI,Roboto,sans-serif;transition:background .15s,color .15s;}
nav{padding:14px 24px;border-bottom:1px solid var(--border);display:flex;align-items:center;justify-content:space-between;}
nav .logo{color:var(--accent);text-decoration:none;font-weight:700;font-size:18px;display:inline-flex;align-items:center;gap:8px;}
.nav-right{display:flex;align-items:center;gap:14px;}
.nav-link{color:var(--text);font-size:13px;display:inline-flex;align-items:center;gap:6px;}
.nav-link:hover{color:var(--accent);text-decoration:none;}
main{max-width:900px;margin:0 auto;padding:24px;}
a{color:var(--accent);text-decoration:none;}
a:hover{text-decoration:underline;}
h1{font-size:22px;display:flex;align-items:center;gap:8px;flex-wrap:wrap;}
.icon{vertical-align:middle;color:currentColor;flex-shrink:0;}
.muted{color:var(--muted);font-size:13px;}
.badge{background:var(--accent);color:#fff;border-radius:12px;padding:2px 8px;font-size:11px;}
.back-link{display:inline-flex;align-items:center;gap:4px;font-size:13px;}
.theme-toggle{background:transparent;border:1px solid var(--border);border-radius:6px;padding:6px;cursor:pointer;color:var(--text);display:flex;margin:0;}
.theme-toggle:hover{border-color:var(--accent);}
html[data-theme=""dark""] .icon-moon{display:none;}
html[data-theme=""light""] .icon-sun{display:none;}
ul.branch-list{list-style:none;padding:0;}
ul.branch-list li{padding:10px;border:1px solid var(--border);border-radius:6px;margin-bottom:6px;background:var(--panel);display:flex;align-items:center;gap:8px;}
.filter-input{width:100%;padding:8px 12px;margin-bottom:10px;border:1px solid var(--border);border-radius:6px;background:var(--panel);color:var(--text);font-size:13px;}
.filter-input:focus{outline:none;border-color:var(--accent);}
table.file-table{width:100%;border-collapse:collapse;border:1px solid var(--border);border-radius:6px;overflow:hidden;font-size:13px;}
table.file-table th{text-align:left;background:var(--panel);padding:8px 12px;border-bottom:1px solid var(--border);color:var(--muted);font-weight:600;}
table.file-table td{padding:8px 12px;border-bottom:1px solid var(--border);vertical-align:middle;}
table.file-table td:first-child{display:flex;align-items:center;gap:6px;}
table.file-table tr:last-child td{border-bottom:none;}
table.file-table tr:hover{background:var(--panel);}
td.msg{color:var(--muted);max-width:320px;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;}
td.author{color:var(--muted);}
td.size{color:var(--muted);text-align:right;}
.age-green{color:var(--green);font-weight:600;}
.age-yellow{color:var(--yellow);font-weight:600;}
.age-red{color:var(--red);font-weight:600;}
.readme{margin-top:20px;border:1px solid var(--border);border-radius:6px;overflow:hidden;}
.readme-header{background:var(--panel);padding:10px 14px;border-bottom:1px solid var(--border);font-weight:600;display:flex;align-items:center;gap:6px;font-size:13px;}
.readme-body{margin:0;padding:16px;white-space:pre-wrap;font-family:-apple-system,Segoe UI,Roboto,sans-serif;font-size:14px;line-height:1.6;background:var(--bg);}
details summary{cursor:pointer;}
pre.code{background:var(--panel);border:1px solid var(--border);border-radius:6px;padding:16px;overflow:auto;font-family:Consolas,monospace;font-size:13px;line-height:1.5;}
button{background:var(--panel);border:1px solid var(--border);color:var(--text);padding:6px 14px;border-radius:6px;cursor:pointer;margin-top:8px;display:inline-flex;align-items:center;gap:6px;}
button:hover{border-color:var(--accent);}
.copy-btn{padding:3px 8px;margin:0;font-size:12px;}
ul.log{list-style:none;padding:0;}
ul.log li{border-left:2px solid var(--border);padding:8px 0 8px 14px;margin-bottom:4px;display:flex;flex-direction:column;gap:4px;}
.log-head{display:flex;align-items:center;gap:8px;}
code{font-family:Consolas,monospace;}
.flash{background:var(--panel);border:1px solid var(--accent);border-radius:6px;padding:10px 14px;margin-bottom:16px;font-size:13px;}
.panel{border:1px solid var(--border);border-radius:8px;padding:16px;margin-bottom:16px;background:var(--panel);}
.panel h2{margin-top:0;margin-bottom:12px;font-size:15px;display:flex;align-items:center;gap:6px;}
.panel-danger{border-color:var(--red);}
.btn-danger{background:var(--red);color:#fff;border:none;}
.btn-danger:hover{opacity:0.85;border-color:transparent;}
.change-list,.staged-list,.branch-manage-list{list-style:none;padding:0;margin:0 0 10px;}
.change-list li,.staged-list li{padding:6px 0;border-bottom:1px solid var(--border);font-size:13px;}
.change-list li:last-child,.staged-list li:last-child{border-bottom:none;}
.change-list label{display:flex;align-items:center;gap:8px;cursor:pointer;}
.branch-manage-list li{padding:8px 0;border-bottom:1px solid var(--border);display:flex;align-items:center;gap:10px;flex-wrap:wrap;font-size:13px;}
.branch-manage-list li:last-child{border-bottom:none;}
.st-new{color:var(--green);font-weight:600;font-size:11px;text-transform:uppercase;}
.st-mod{color:var(--yellow);font-weight:600;font-size:11px;text-transform:uppercase;}
.st-del{color:var(--red);font-weight:600;font-size:11px;text-transform:uppercase;}
textarea{width:100%;padding:10px;border:1px solid var(--border);border-radius:6px;background:var(--bg);color:var(--text);font-family:inherit;font-size:13px;resize:vertical;margin-bottom:8px;}
textarea:focus,input[type=text]:focus{outline:none;border-color:var(--accent);}
input[type=text]{padding:7px 10px;border:1px solid var(--border);border-radius:6px;background:var(--bg);color:var(--text);font-size:13px;margin-right:8px;}
.inline-form{display:inline-flex;align-items:center;gap:0;margin:0;}
";
    }
}