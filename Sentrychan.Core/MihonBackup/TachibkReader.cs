using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Sentrychan.Core.MihonBackup;

/// <summary>One chapter as Mihon backed it up. <see cref="Url"/> is the source's own key for it.</summary>
public sealed record BackupChapter(string Url, string Name, string? Scanlator, bool Read, bool Bookmark,
    long LastPageRead, long DateUpload, float ChapterNumber, long SourceOrder);

/// <summary>When a chapter (by URL) was last read, in Unix milliseconds.</summary>
public sealed record BackupHistory(string Url, long LastRead);

/// <summary>A category. Titles refer to categories by <see cref="Order"/>, not by id.</summary>
public sealed record BackupCategory(string Name, long Order);

public sealed record BackupSource(string Name, long Id);

/// <summary>One title. <see cref="Source"/> is the Mihon source id and <see cref="Url"/> the source's key for it.</summary>
public sealed record BackupManga(
    long Source, string Url, string Title, string? Author, string? Description, IReadOnlyList<string> Genres,
    int Status, string? ThumbnailUrl, long DateAdded, IReadOnlyList<BackupChapter> Chapters,
    IReadOnlyList<long> Categories, bool Favorite, IReadOnlyList<BackupHistory> History);

public sealed record MihonBackupData(IReadOnlyList<BackupManga> Manga, IReadOnlyList<BackupCategory> Categories,
    IReadOnlyList<BackupSource> Sources)
{
    /// <summary>The name the backup gives a source id, or null when it doesn't list it.</summary>
    public string? SourceName(long id) => Sources.FirstOrDefault(s => s.Id == id)?.Name;
}

/// <summary>
/// Reads a Mihon backup (<c>.tachibk</c>): gzip-compressed protobuf. Field numbers are Mihon's
/// (<c>data/backup/models</c> in its source): Backup 1 manga, 2 categories, 101 sources;
/// BackupManga 1 source, 2 url, 3 title, 5 author, 6 description, 7 genre, 8 status,
/// 9 thumbnailUrl, 13 dateAdded, 16 chapters, 17 categories, 100 favorite, 104 history;
/// BackupChapter 1 url, 2 name, 3 scanlator, 4 read, 5 bookmark, 6 lastPageRead,
/// 8 dateUpload, 9 chapterNumber (float), 10 sourceOrder; BackupCategory 1 name, 2 order;
/// BackupHistory 1 url, 2 lastRead; BackupSource 1 name, 2 sourceId.
/// Everything else (tracking, preferences, extension stores…) is skipped.
/// </summary>
public static class TachibkReader
{
    public static MihonBackupData Read(Stream stream)
    {
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return Read(buffer.ToArray());
    }

    public static MihonBackupData Read(byte[] file)
    {
        var data = IsGzip(file) ? Gunzip(file) : file;
        try { return ParseBackup(data); }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException or InvalidDataException)
        {
            throw new InvalidDataException("This isn't a Mihon backup this version can read.", ex);
        }
    }

    private static bool IsGzip(byte[] b) => b.Length > 2 && b[0] == 0x1f && b[1] == 0x8b;

    private static byte[] Gunzip(byte[] file)
    {
        using var gz = new GZipStream(new MemoryStream(file), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        return ms.ToArray();
    }

    private static MihonBackupData ParseBackup(ReadOnlySpan<byte> data)
    {
        var manga = new List<BackupManga>();
        var categories = new List<BackupCategory>();
        var sources = new List<BackupSource>();
        var r = new Reader(data);
        while (r.Next(out var field, out var wire))
        {
            switch (field)
            {
                case 1 when wire == 2: manga.Add(ParseManga(r.Bytes())); break;
                case 2 when wire == 2: categories.Add(ParseCategory(r.Bytes())); break;
                case 101 when wire == 2: sources.Add(ParseSource(r.Bytes())); break;
                default: r.Skip(wire); break;
            }
        }
        return new(manga, categories, sources);
    }

    private static BackupManga ParseManga(ReadOnlySpan<byte> data)
    {
        long source = 0, dateAdded = 0;
        string url = "", title = "";
        string? author = null, description = null, thumbnail = null;
        var genres = new List<string>();
        var chapters = new List<BackupChapter>();
        var categories = new List<long>();
        var history = new List<BackupHistory>();
        var status = 0;
        var favorite = true; // proto default in Mihon's model
        var r = new Reader(data);
        while (r.Next(out var field, out var wire))
        {
            switch (field)
            {
                case 1 when wire == 0: source = (long)r.Varint(); break;
                case 2 when wire == 2: url = r.String(); break;
                case 3 when wire == 2: title = r.String(); break;
                case 5 when wire == 2: author = r.String(); break;
                case 6 when wire == 2: description = r.String(); break;
                case 7 when wire == 2: genres.Add(r.String()); break;
                case 8 when wire == 0: status = (int)r.Varint(); break;
                case 9 when wire == 2: thumbnail = r.String(); break;
                case 13 when wire == 0: dateAdded = (long)r.Varint(); break;
                case 16 when wire == 2: chapters.Add(ParseChapter(r.Bytes())); break;
                case 17 when wire == 0: categories.Add((long)r.Varint()); break;
                case 17 when wire == 2: // packed
                    var packed = new Reader(r.Bytes());
                    while (!packed.End) categories.Add((long)packed.Varint());
                    break;
                case 100 when wire == 0: favorite = r.Varint() != 0; break;
                case 104 when wire == 2: history.Add(ParseHistory(r.Bytes())); break;
                default: r.Skip(wire); break;
            }
        }
        return new(source, url, title, author, description, genres, status, thumbnail, dateAdded, chapters, categories, favorite, history);
    }

    private static BackupChapter ParseChapter(ReadOnlySpan<byte> data)
    {
        string url = "", name = "";
        string? scanlator = null;
        bool read = false, bookmark = false;
        long lastPage = 0, upload = 0, order = 0;
        var number = 0f;
        var r = new Reader(data);
        while (r.Next(out var field, out var wire))
        {
            switch (field)
            {
                case 1 when wire == 2: url = r.String(); break;
                case 2 when wire == 2: name = r.String(); break;
                case 3 when wire == 2: scanlator = r.String(); break;
                case 4 when wire == 0: read = r.Varint() != 0; break;
                case 5 when wire == 0: bookmark = r.Varint() != 0; break;
                case 6 when wire == 0: lastPage = (long)r.Varint(); break;
                case 8 when wire == 0: upload = (long)r.Varint(); break;
                case 9 when wire == 5: number = r.Float(); break;
                case 10 when wire == 0: order = (long)r.Varint(); break;
                default: r.Skip(wire); break;
            }
        }
        return new(url, name, scanlator, read, bookmark, lastPage, upload, number, order);
    }

    private static BackupCategory ParseCategory(ReadOnlySpan<byte> data)
    {
        string name = "";
        long order = 0;
        var r = new Reader(data);
        while (r.Next(out var field, out var wire))
        {
            if (field == 1 && wire == 2) name = r.String();
            else if (field == 2 && wire == 0) order = (long)r.Varint();
            else r.Skip(wire);
        }
        return new(name, order);
    }

    private static BackupHistory ParseHistory(ReadOnlySpan<byte> data)
    {
        string url = "";
        long lastRead = 0;
        var r = new Reader(data);
        while (r.Next(out var field, out var wire))
        {
            if (field == 1 && wire == 2) url = r.String();
            else if (field == 2 && wire == 0) lastRead = (long)r.Varint();
            else r.Skip(wire);
        }
        return new(url, lastRead);
    }

    private static BackupSource ParseSource(ReadOnlySpan<byte> data)
    {
        string name = "";
        long id = 0;
        var r = new Reader(data);
        while (r.Next(out var field, out var wire))
        {
            if (field == 1 && wire == 2) name = r.String();
            else if (field == 2 && wire == 0) id = (long)r.Varint();
            else r.Skip(wire);
        }
        return new(name, id);
    }

    /// <summary>The protobuf wire format, as much of it as a backup uses.</summary>
    private ref struct Reader(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _pos;

        public readonly bool End => _pos >= _data.Length;

        public bool Next(out int field, out int wire)
        {
            if (End) { field = wire = 0; return false; }
            var key = Varint();
            field = (int)(key >> 3);
            wire = (int)(key & 7);
            return true;
        }

        public ulong Varint()
        {
            ulong result = 0;
            for (var shift = 0; shift < 64; shift += 7)
            {
                var b = _data[_pos++];
                result |= (ulong)(b & 0x7f) << shift;
                if ((b & 0x80) == 0) return result;
            }
            throw new InvalidDataException("Malformed varint.");
        }

        public ReadOnlySpan<byte> Bytes()
        {
            var len = checked((int)Varint());
            var slice = _data.Slice(_pos, len);
            _pos += len;
            return slice;
        }

        public string String() => Encoding.UTF8.GetString(Bytes());

        public float Float()
        {
            var v = BinaryPrimitives.ReadSingleLittleEndian(_data.Slice(_pos, 4));
            _pos += 4;
            return v;
        }

        public void Skip(int wire)
        {
            switch (wire)
            {
                case 0: Varint(); break;
                case 1: _pos += 8; break;
                case 2: Bytes(); break;
                case 5: _pos += 4; break;
                default: throw new InvalidDataException($"Unsupported protobuf wire type {wire}.");
            }
            if (_pos > _data.Length) throw new InvalidDataException("Truncated backup.");
        }
    }
}
