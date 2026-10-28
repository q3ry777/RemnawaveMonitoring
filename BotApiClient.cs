using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace RemnawaveMonitor;

// Забирает оплаты у бота на сервере: запрос подписан ключом, ответ расшифровывается (SecureBox)
public static class BotApiClient
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static async Task<PaymentsReport> GetPaymentsAsync(string host, int port, string key)
    {
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var url = $"http://{host.Trim().TrimEnd('/')}:{port}/api/payments?ts={ts}&sig={SecureBox.Sign(key, "payments|" + ts)}";
        using var res = await Http.GetAsync(url);
        var body = await res.Content.ReadAsStringAsync();
        if ((int)res.StatusCode == 401) throw new Exception("бот отклонил запрос: ключ не совпадает или часы компьютера сильно расходятся с сервером — выполните команду установки ещё раз");
        if (!res.IsSuccessStatusCode) throw new Exception($"бот ответил {(int)res.StatusCode}");
        var json = SecureBox.Open(key, body, "payments|" + ts);
        return JsonSerializer.Deserialize<PaymentsReport>(json) ?? throw new Exception("пустой ответ");
    }
}
