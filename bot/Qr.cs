using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace RemnawaveMonitor;

// Небольшой генератор QR-кода (байтовый режим, уровень коррекции M, версии 1–15 — до 412 байт) и PNG без сторонних библиотек.
// Нужен, чтобы бот мог прислать пользователю QR-код его подписки картинкой.
public static class Qr
{
    // для уровня M: (байт коррекции на блок, блоков в группе 1, данных в блоке группы 1, блоков в группе 2, данных в блоке группы 2)
    static readonly (int ec, int b1, int d1, int b2, int d2)[] BlocksM =
    {
        default,
        (10, 1, 16, 0, 0), (16, 1, 28, 0, 0), (26, 1, 44, 0, 0), (18, 2, 32, 0, 0), (24, 2, 43, 0, 0),
        (16, 4, 27, 0, 0), (18, 4, 31, 0, 0), (22, 2, 38, 2, 39), (22, 3, 36, 2, 37), (26, 4, 43, 1, 44),
        (30, 1, 50, 4, 51), (22, 6, 36, 2, 37), (22, 8, 37, 1, 38), (24, 4, 40, 5, 41), (24, 5, 41, 5, 42),
    };

    static readonly int[][] Align =
    {
        Array.Empty<int>(), Array.Empty<int>(),
        new[] { 6, 18 }, new[] { 6, 22 }, new[] { 6, 26 }, new[] { 6, 30 }, new[] { 6, 34 },
        new[] { 6, 22, 38 }, new[] { 6, 24, 42 }, new[] { 6, 26, 46 }, new[] { 6, 28, 50 },
        new[] { 6, 30, 54 }, new[] { 6, 32, 58 }, new[] { 6, 34, 62 }, new[] { 6, 26, 46, 66 }, new[] { 6, 26, 48, 70 },
    };

    // Матрица модулей: true — тёмный
    public static bool[,] Encode(string text)
    {
        var data = Encoding.UTF8.GetBytes(text);
        int ver = 0;
        for (int v = 1; v < BlocksM.Length; v++)
        {
            var b = BlocksM[v];
            int capBits = (b.b1 * b.d1 + b.b2 * b.d2) * 8;
            int need = 4 + (v < 10 ? 8 : 16) + data.Length * 8;
            if (need <= capBits) { ver = v; break; }
        }
        if (ver == 0) throw new ArgumentException("слишком длинный текст для QR-кода");
        var blk = BlocksM[ver];
        int dataCw = blk.b1 * blk.d1 + blk.b2 * blk.d2;

        // поток бит: режим «байты», длина, данные, терминатор, выравнивание, заполнители
        var bits = new List<bool>();
        void Put(int value, int len) { for (int i = len - 1; i >= 0; i--) bits.Add(((value >> i) & 1) == 1); }
        Put(0b0100, 4);
        Put(data.Length, ver < 10 ? 8 : 16);
        foreach (var x in data) Put(x, 8);
        Put(0, Math.Min(4, dataCw * 8 - bits.Count));
        while (bits.Count % 8 != 0) bits.Add(false);
        var cw = new List<byte>();
        for (int i = 0; i < bits.Count; i += 8)
        {
            int v = 0;
            for (int j = 0; j < 8; j++) v = (v << 1) | (bits[i + j] ? 1 : 0);
            cw.Add((byte)v);
        }
        for (int pad = 0; cw.Count < dataCw; pad++) cw.Add(pad % 2 == 0 ? (byte)0xEC : (byte)0x11);

        // блоки и коррекция ошибок (Рида — Соломона), затем чередование
        var dataBlocks = new List<byte[]>();
        var ecBlocks = new List<byte[]>();
        int pos = 0;
        for (int g = 0; g < 2; g++)
        {
            int count = g == 0 ? blk.b1 : blk.b2, len = g == 0 ? blk.d1 : blk.d2;
            for (int i = 0; i < count; i++)
            {
                var d = cw.GetRange(pos, len).ToArray();
                pos += len;
                dataBlocks.Add(d);
                ecBlocks.Add(ReedSolomon(d, blk.ec));
            }
        }
        var final = new List<byte>();
        int maxD = Math.Max(blk.d1, blk.d2);
        for (int i = 0; i < maxD; i++) foreach (var d in dataBlocks) if (i < d.Length) final.Add(d[i]);
        for (int i = 0; i < blk.ec; i++) foreach (var e in ecBlocks) final.Add(e[i]);

        int size = ver * 4 + 17;
        var m = new bool[size, size];
        var reserved = new bool[size, size];
        void Set(int r, int c, bool dark) { m[r, c] = dark; reserved[r, c] = true; }

        // поисковые узоры с разделителями
        foreach (var (r0, c0) in new[] { (0, 0), (0, size - 7), (size - 7, 0) })
            for (int r = -1; r <= 7; r++)
                for (int c = -1; c <= 7; c++)
                {
                    int rr = r0 + r, cc = c0 + c;
                    if (rr < 0 || cc < 0 || rr >= size || cc >= size) continue;
                    bool dark = r >= 0 && r <= 6 && c >= 0 && c <= 6 &&
                                (r == 0 || r == 6 || c == 0 || c == 6 || (r >= 2 && r <= 4 && c >= 2 && c <= 4));
                    Set(rr, cc, dark);
                }
        // выравнивающие узоры
        var al = Align[ver];
        foreach (var r in al)
            foreach (var c in al)
            {
                if (reserved[r, c]) continue;   // пересечение с поисковым узором
                for (int dr = -2; dr <= 2; dr++)
                    for (int dc = -2; dc <= 2; dc++)
                        Set(r + dr, c + dc, Math.Max(Math.Abs(dr), Math.Abs(dc)) != 1);
            }
        // синхронизирующие линии
        for (int i = 8; i < size - 8; i++)
        {
            if (!reserved[6, i]) Set(6, i, i % 2 == 0);
            if (!reserved[i, 6]) Set(i, 6, i % 2 == 0);
        }
        Set(size - 8, 8, true);   // тёмный модуль
        // место под формат и версию
        for (int i = 0; i < 9; i++)
        {
            if (!reserved[8, i]) Set(8, i, false);
            if (!reserved[i, 8]) Set(i, 8, false);
        }
        for (int i = 0; i < 8; i++)
        {
            if (!reserved[8, size - 1 - i]) Set(8, size - 1 - i, false);
            if (!reserved[size - 1 - i, 8]) Set(size - 1 - i, 8, false);
        }
        if (ver >= 7)
            for (int i = 0; i < 6; i++)
                for (int j = 0; j < 3; j++) { Set(i, size - 11 + j, false); Set(size - 11 + j, i, false); }

        // данные «змейкой» снизу вверх по парам столбцов
        int bit = 0, total = final.Count * 8;
        bool up = true;
        for (int col = size - 1; col > 0; col -= 2)
        {
            if (col == 6) col--;
            for (int k = 0; k < size; k++)
            {
                int r = up ? size - 1 - k : k;
                for (int dc = 0; dc < 2; dc++)
                {
                    int c = col - dc;
                    if (reserved[r, c]) continue;
                    bool dark = bit < total && ((final[bit / 8] >> (7 - bit % 8)) & 1) == 1;
                    m[r, c] = dark;
                    bit++;
                }
            }
            up = !up;
        }

        // выбор маски по штрафам
        bool[,]? best = null;
        int bestScore = int.MaxValue;
        for (int mask = 0; mask < 8; mask++)
        {
            var t = (bool[,])m.Clone();
            for (int r = 0; r < size; r++)
                for (int c = 0; c < size; c++)
                    if (!reserved[r, c] && MaskBit(mask, r, c)) t[r, c] = !t[r, c];
            PlaceFormat(t, size, mask);
            if (ver >= 7) PlaceVersion(t, size, ver);
            int score = Penalty(t, size);
            if (score < bestScore) { bestScore = score; best = t; }
        }
        return best!;
    }

    static bool MaskBit(int mask, int r, int c) => mask switch
    {
        0 => (r + c) % 2 == 0,
        1 => r % 2 == 0,
        2 => c % 3 == 0,
        3 => (r + c) % 3 == 0,
        4 => (r / 2 + c / 3) % 2 == 0,
        5 => r * c % 2 + r * c % 3 == 0,
        6 => (r * c % 2 + r * c % 3) % 2 == 0,
        _ => ((r + c) % 2 + r * c % 3) % 2 == 0,
    };

    // 15 бит формата: уровень M (00) + маска, BCH(15,5), XOR 101010000010010
    static void PlaceFormat(bool[,] t, int size, int mask)
    {
        int data = (0b00 << 3) | mask;
        int v = data << 10;
        for (int i = 14; i >= 10; i--) if (((v >> i) & 1) == 1) v ^= 0x537 << (i - 10);
        int bits = ((data << 10) | v) ^ 0x5412;
        bool B(int i) => ((bits >> i) & 1) == 1;
        // у левого верхнего поискового узора
        for (int i = 0; i <= 5; i++) t[i, 8] = B(i);
        t[7, 8] = B(6); t[8, 8] = B(7); t[8, 7] = B(8);
        for (int i = 9; i <= 14; i++) t[8, 14 - i] = B(i);
        // копия: правый верхний и левый нижний
        for (int i = 0; i <= 7; i++) t[8, size - 1 - i] = B(i);
        for (int i = 8; i <= 14; i++) t[size - 15 + i, 8] = B(i);
    }

    // 18 бит версии (для версий 7+), BCH(18,6)
    static void PlaceVersion(bool[,] t, int size, int ver)
    {
        int v = ver << 12;
        for (int i = 17; i >= 12; i--) if (((v >> i) & 1) == 1) v ^= 0x1F25 << (i - 12);
        int bits = (ver << 12) | v;
        for (int i = 0; i < 18; i++)
        {
            bool b = ((bits >> i) & 1) == 1;
            int a = i / 3, c = size - 11 + i % 3;
            t[a, c] = b;
            t[c, a] = b;
        }
    }

    static int Penalty(bool[,] t, int n)
    {
        int score = 0;
        // 1: серии одного цвета длиной 5+
        for (int pass = 0; pass < 2; pass++)
            for (int i = 0; i < n; i++)
            {
                int run = 1;
                for (int j = 1; j < n; j++)
                {
                    bool a = pass == 0 ? t[i, j] : t[j, i], b = pass == 0 ? t[i, j - 1] : t[j - 1, i];
                    if (a == b) run++;
                    else { if (run >= 5) score += run - 2; run = 1; }
                }
                if (run >= 5) score += run - 2;
            }
        // 2: квадраты 2×2
        for (int r = 0; r < n - 1; r++)
            for (int c = 0; c < n - 1; c++)
                if (t[r, c] == t[r, c + 1] && t[r, c] == t[r + 1, c] && t[r, c] == t[r + 1, c + 1]) score += 3;
        // 3: узоры, похожие на поисковые
        bool[] p1 = { true, false, true, true, true, false, true, false, false, false, false };
        bool[] p2 = { false, false, false, false, true, false, true, true, true, false, true };
        for (int pass = 0; pass < 2; pass++)
            for (int i = 0; i < n; i++)
                for (int j = 0; j + 11 <= n; j++)
                {
                    bool m1 = true, m2 = true;
                    for (int k = 0; k < 11; k++)
                    {
                        bool x = pass == 0 ? t[i, j + k] : t[j + k, i];
                        if (x != p1[k]) m1 = false;
                        if (x != p2[k]) m2 = false;
                    }
                    if (m1) score += 40;
                    if (m2) score += 40;
                }
        // 4: доля тёмных
        int dark = 0;
        foreach (var x in t) if (x) dark++;
        int pct = dark * 100 / (n * n);
        score += Math.Abs(pct - 50) / 5 * 10;
        return score;
    }

    // ---- Рид — Соломон в GF(256), полином 0x11D ----
    static readonly byte[] Exp = new byte[512], Log = new byte[256];
    static Qr()
    {
        int x = 1;
        for (int i = 0; i < 255; i++)
        {
            Exp[i] = (byte)x;
            Log[x] = (byte)i;
            x <<= 1;
            if (x >= 256) x ^= 0x11D;
        }
        for (int i = 255; i < 512; i++) Exp[i] = Exp[i - 255];
    }

    static byte Mul(byte a, byte b) => a == 0 || b == 0 ? (byte)0 : Exp[Log[a] + Log[b]];

    static byte[] ReedSolomon(byte[] data, int ecLen)
    {
        // порождающий многочлен (x - a^0)(x - a^1)...(x - a^(ecLen-1))
        var gen = new byte[ecLen + 1];
        gen[0] = 1;
        for (int i = 0; i < ecLen; i++)
        {
            var next = new byte[ecLen + 1];
            for (int j = 0; j <= i; j++)
            {
                next[j] ^= gen[j];
                next[j + 1] ^= Mul(gen[j], Exp[i]);
            }
            gen = next;
        }
        var rem = new byte[ecLen];
        foreach (var d in data)
        {
            byte factor = (byte)(d ^ rem[0]);
            Array.Copy(rem, 1, rem, 0, ecLen - 1);
            rem[ecLen - 1] = 0;
            for (int j = 0; j < ecLen; j++) rem[j] ^= Mul(gen[j + 1], factor);
        }
        return rem;
    }

    // ---- PNG: оттенки серого, 8 бит, scale пикселей на модуль, поле 4 модуля ----
    public static byte[] Png(bool[,] m, int scale = 8)
    {
        int n = m.GetLength(0), border = 4, size = (n + border * 2) * scale;
        var raw = new byte[size * (size + 1)];
        for (int y = 0; y < size; y++)
        {
            int row = y * (size + 1);
            raw[row] = 0;   // фильтр строки: нет
            int my = y / scale - border;
            for (int x = 0; x < size; x++)
            {
                int mx = x / scale - border;
                bool dark = my >= 0 && mx >= 0 && my < n && mx < n && m[my, mx];
                raw[row + 1 + x] = dark ? (byte)0 : (byte)255;
            }
        }
        byte[] idat;
        using (var ms = new MemoryStream())
        {
            using (var z = new ZLibStream(ms, CompressionLevel.Optimal, true)) z.Write(raw, 0, raw.Length);
            idat = ms.ToArray();
        }
        using var png = new MemoryStream();
        png.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        var ihdr = new byte[13];
        WriteBE(ihdr, 0, size); WriteBE(ihdr, 4, size);
        ihdr[8] = 8; ihdr[9] = 0; ihdr[10] = 0; ihdr[11] = 0; ihdr[12] = 0;
        Chunk(png, "IHDR", ihdr);
        Chunk(png, "IDAT", idat);
        Chunk(png, "IEND", Array.Empty<byte>());
        return png.ToArray();
    }

    static void WriteBE(byte[] b, int o, int v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }

    static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteBE(len, 0, data.Length);
        s.Write(len);
        var t = Encoding.ASCII.GetBytes(type);
        s.Write(t);
        s.Write(data);
        uint crc = Crc(t, 0xFFFFFFFF);
        crc = Crc(data, crc) ^ 0xFFFFFFFF;
        var c = new byte[4];
        WriteBE(c, 0, (int)crc);
        s.Write(c);
    }

    static uint[]? _crcTable;
    static uint Crc(byte[] data, uint crc)
    {
        if (_crcTable == null)
        {
            var tbl = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
                tbl[n] = c;
            }
            _crcTable = tbl;
        }
        foreach (var b in data) crc = _crcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        return crc;
    }
}
