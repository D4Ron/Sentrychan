using System.IO.Compression;
using System.Text;
using Sentrychan.Core.MihonBackup;

namespace Sentrychan.Tests.MihonBackup;

/// <summary>
/// Writes a Mihon backup the way Mihon does (kotlinx protobuf: int64 as plain varints, lists
/// unpacked, gzip around it), so the reader is tested against the wire format, not against itself.
/// Can add fields the reader must skip.
/// </summary>
internal static class TachibkWriter
{
    public static byte[] Write(MihonBackupData backup, bool gzip = true, bool withNoise = false, bool packCategories = false)
    {
        var b = new Proto();
        foreach (var m in backup.Manga) b.Message(1, Manga(m, withNoise, packCategories));
        foreach (var c in backup.Categories) b.Message(2, new Proto().Str(1, c.Name).Varint(2, c.Order).Varint(100, 0));
        foreach (var s in backup.Sources) b.Message(101, new Proto().Str(1, s.Name).Varint(2, s.Id));
        if (withNoise) b.Message(104, new Proto().Str(1, "pref_key").Message(2, new Proto().Varint(1, 1)));
        var raw = b.ToArray();
        if (!gzip) return raw;
        using var ms = new MemoryStream();
        using (var gz = new GZipStream(ms, CompressionLevel.Fastest, leaveOpen: true)) gz.Write(raw);
        return ms.ToArray();
    }

    private static Proto Manga(BackupManga m, bool noise, bool packCategories)
    {
        var p = new Proto().Varint(1, m.Source).Str(2, m.Url).Str(3, m.Title);
        if (m.Author != null) p.Str(5, m.Author);
        if (m.Description != null) p.Str(6, m.Description);
        foreach (var g in m.Genres) p.Str(7, g);
        p.Varint(8, m.Status);
        if (m.ThumbnailUrl != null) p.Str(9, m.ThumbnailUrl);
        p.Varint(13, m.DateAdded);
        foreach (var c in m.Chapters)
            p.Message(16, new Proto().Str(1, c.Url).Str(2, c.Name).StrOpt(3, c.Scanlator).Varint(4, c.Read ? 1 : 0)
                .Varint(5, c.Bookmark ? 1 : 0).Varint(6, c.LastPageRead).Varint(7, 1).Varint(8, c.DateUpload)
                .Float(9, c.ChapterNumber).Varint(10, c.SourceOrder));
        if (packCategories)
        {
            var packed = new Proto();
            foreach (var c in m.Categories) packed.RawVarint((ulong)c);
            p.Bytes(17, packed.ToArray());
        }
        else foreach (var c in m.Categories) p.Varint(17, c);
        if (noise) p.Message(18, new Proto().Varint(1, 2).Str(3, "tracker"));
        p.Varint(100, m.Favorite ? 1 : 0);
        foreach (var h in m.History) p.Message(104, new Proto().Str(1, h.Url).Varint(2, h.LastRead));
        if (noise) p.Varint(105, 0).Str(110, "notes").Bytes(112, "{}"u8.ToArray());
        return p;
    }

    private sealed class Proto
    {
        private readonly MemoryStream _ms = new();

        private Proto Key(int field, int wire) { RawVarint((ulong)((field << 3) | wire)); return this; }

        public Proto RawVarint(ulong v)
        {
            do
            {
                var b = (byte)(v & 0x7f);
                v >>= 7;
                _ms.WriteByte(v != 0 ? (byte)(b | 0x80) : b);
            } while (v != 0);
            return this;
        }

        public Proto Varint(int field, long v) { Key(field, 0); return RawVarint((ulong)v); }
        public Proto Bytes(int field, byte[] bytes) { Key(field, 2); RawVarint((ulong)bytes.Length); _ms.Write(bytes); return this; }
        public Proto Str(int field, string s) => Bytes(field, Encoding.UTF8.GetBytes(s));
        public Proto StrOpt(int field, string? s) => s == null ? this : Str(field, s);
        public Proto Message(int field, Proto inner) => Bytes(field, inner.ToArray());
        public Proto Float(int field, float f) { Key(field, 5); _ms.Write(BitConverter.GetBytes(f)); return this; }
        public byte[] ToArray() => _ms.ToArray();
    }
}
