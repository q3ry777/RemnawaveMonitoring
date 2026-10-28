using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace RemnawaveMonitor;

// Обмен программы на ПК с ботом через открытый порт без TLS: запрос подписан (HMAC-SHA256 со временем — от повтора),
// ответ зашифрован AES-256-GCM общим ключом. Ключ знают только программа и .env бота.
public static class SecureBox
{
    static byte[] EncKey(string secret) => SHA256.HashData(Encoding.UTF8.GetBytes("rwbot-enc|" + secret));

    public static string Sign(string secret, string message) =>
        Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(message))).ToLowerInvariant();

    public static bool Verify(string secret, string message, string sig) =>
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Sign(secret, message)), Encoding.ASCII.GetBytes(sig.Trim().ToLowerInvariant()));

    public static string Seal(string secret, string text, string aad)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var plain = Encoding.UTF8.GetBytes(text);
        var cipher = new byte[plain.Length];
        var tag = new byte[16];
        using (var g = new AesGcm(EncKey(secret), 16)) g.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(aad));
        var all = new byte[12 + cipher.Length + 16];
        nonce.CopyTo(all, 0); cipher.CopyTo(all, 12); tag.CopyTo(all, 12 + cipher.Length);
        return Convert.ToBase64String(all);
    }

    public static string Open(string secret, string b64, string aad)
    {
        var all = Convert.FromBase64String(b64.Trim());
        if (all.Length < 28) throw new CryptographicException("короткий ответ");
        var cipher = new byte[all.Length - 28];
        var plain = new byte[cipher.Length];
        Array.Copy(all, 12, cipher, 0, cipher.Length);
        using (var g = new AesGcm(EncKey(secret), 16))
            g.Decrypt(all.AsSpan(0, 12), cipher, all.AsSpan(all.Length - 16, 16), plain, Encoding.UTF8.GetBytes(aad));
        return Encoding.UTF8.GetString(plain);
    }
}

// Оплаты из бота — для вкладки «оплаты» в программе
public record PaymentInfo(string Id, DateTime Created, DateTime? Done, string Status, string Username, int Months, int Amount, string NewExpire, string Chat);
public record PaymentsReport(DateTime Generated, string Currency, List<PaymentInfo> Payments);
