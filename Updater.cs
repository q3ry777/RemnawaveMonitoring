using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace RemnawaveMonitor;

public record UpdateInfo(string Version, string Notes, long AssetId, string AssetName);

// Самообновление из релизов приватного репозитория GitHub (токен — только на чтение)
public static class Updater
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };

    public static Version Current => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
    public static string CurrentText => $"{Current.Major}.{Current.Minor}.{Math.Max(0, Current.Build)}";

    static HttpRequestMessage Req(Settings s, string url, string accept)
    {
        var r = new HttpRequestMessage(HttpMethod.Get, url);
        r.Headers.UserAgent.ParseAdd("RemnawaveMonitor");
        r.Headers.Accept.ParseAdd(accept);
        r.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        if (!string.IsNullOrEmpty(s.UpdateToken))
            r.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.UpdateToken);
        return r;
    }

    // null — новее ничего нет (или репозиторий не задан); при проблемах доступа кидает исключение с понятным текстом
    public static async Task<UpdateInfo?> CheckAsync(Settings s)
    {
        if (string.IsNullOrWhiteSpace(s.UpdateRepo)) return null;
        using var res = await Http.SendAsync(Req(s, $"https://api.github.com/repos/{s.UpdateRepo.Trim()}/releases/latest", "application/vnd.github+json"));
        if (res.StatusCode == HttpStatusCode.NotFound)
            throw new Exception("репозиторий или релиз не найден (проверьте owner/repo и токен)");
        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new Exception("GitHub отклонил токен (нет прав или истёк срок)");
        res.EnsureSuccessStatusCode();

        using var d = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var root = d.RootElement;
        var tag = J.S(root, "tag_name").Trim().TrimStart('v');
        if (!ReleaseCheck.Newer(tag, CurrentText)) return null;

        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var a in assets.EnumerateArray())
            {
                var name = J.S(a, "name");
                if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    return new UpdateInfo(tag, J.S(root, "body"), (long)J.N(a, "id"), name);
            }
        throw new Exception($"в релизе {tag} нет файла для установки (ещё собирается?)");
    }

    // Скачивает релиз во временную папку и возвращает путь к новому RemnawaveMonitor.exe
    public static async Task<string> DownloadAsync(Settings s, UpdateInfo u)
    {
        var dir = Path.Combine(Path.GetTempPath(), "RemnawaveMonitor-update");
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, u.AssetName);

        using (var res = await Http.SendAsync(
                   Req(s, $"https://api.github.com/repos/{s.UpdateRepo.Trim()}/releases/assets/{u.AssetId}", "application/octet-stream"),
                   HttpCompletionOption.ResponseHeadersRead))
        {
            res.EnsureSuccessStatusCode();
            await using var fs = File.Create(file);
            await res.Content.CopyToAsync(fs);
        }

        if (file.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            var x = Path.Combine(dir, "x");
            ZipFile.ExtractToDirectory(file, x);
            file = Directory.GetFiles(x, "*.exe", SearchOption.AllDirectories).FirstOrDefault()
                   ?? throw new Exception("в архиве нет exe-файла");
        }
        if (new FileInfo(file).Length < 100_000) throw new Exception("скачанный файл слишком мал — загрузка не удалась");
        return file;
    }

    // Работающий exe можно переименовать: старый уходит в .old, новый встаёт на его место и запускается
    public static void ApplyAndRestart(string newExe)
    {
        var exe = Environment.ProcessPath ?? throw new Exception("не удалось определить путь к программе");
        var old = exe + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(exe, old);
        try { File.Copy(newExe, exe); }
        catch { File.Move(old, exe); throw; }
        Process.Start(new ProcessStartInfo(exe, "--updated") { UseShellExecute = true });
    }
}
