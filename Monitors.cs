using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace RemnawaveMonitor;

public static class ReleaseCheck
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static async Task<string?> LatestAsync(string repo)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repo}/releases/latest");
            req.Headers.UserAgent.ParseAdd("RemnawaveMonitor");
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var res = await Http.SendAsync(req);
            if (!res.IsSuccessStatusCode) return null;
            using var d = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            return d.RootElement.GetProperty("tag_name").GetString()?.Trim().TrimStart('v');
        }
        catch { return null; }
    }

    static bool TryV(string? s, out Version v)
    {
        if (Version.TryParse(s?.Trim().TrimStart('v'), out var x)) { v = x; return true; }
        v = new Version(0, 0);
        return false;
    }

    public static bool Newer(string? latest, string? current) =>
        TryV(latest, out var a) && TryV(current, out var b) && a > b;
}
