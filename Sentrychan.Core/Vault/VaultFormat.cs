using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Sentrychan.Core.Vault;

/// <summary>
/// On-disk format of a vault file: a header, then the content in independently
/// authenticated AES-256-GCM chunks.
///
/// Chunking is what makes this usable for video: a player seeks to minute 20 by
/// decrypting only the chunk that holds it, never the whole file. Each chunk's nonce is
/// the file's random nonce with its index mixed in, and the header is authenticated with
/// every chunk, so chunks can't be reordered, swapped between files, or truncated
/// without the read failing.
///
///   header  "SCV1" | chunkSize:int32 | length:int64 | nonce:12 | reserved:4   (32 bytes)
///   chunk i ciphertext (chunkSize, or the remainder for the last) | tag:16
/// </summary>
public static class VaultFormat
{
    public const int HeaderSize = 32;
    public const int TagSize = 16;
    public const int DefaultChunkSize = 1 << 20;
    private static ReadOnlySpan<byte> Magic => "SCV1"u8;

    public static async Task EncryptAsync(
        Stream input, long length, Stream output, byte[] key,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var header = new byte[HeaderSize];
        Magic.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), DefaultChunkSize);
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(8), length);
        RandomNumberGenerator.Fill(header.AsSpan(16, 12));
        await output.WriteAsync(header, ct);

        using var gcm = new AesGcm(key, TagSize);
        var plain  = new byte[DefaultChunkSize];
        var cipher = new byte[DefaultChunkSize];
        var tag    = new byte[TagSize];
        var nonce  = new byte[12];
        long done = 0;

        for (int index = 0; done < length; index++)
        {
            ct.ThrowIfCancellationRequested();
            var want = (int)Math.Min(DefaultChunkSize, length - done);
            await input.ReadExactlyAsync(plain.AsMemory(0, want), ct);

            NonceFor(header, index, nonce);
            gcm.Encrypt(nonce, plain.AsSpan(0, want), cipher.AsSpan(0, want), tag, header);
            await output.WriteAsync(cipher.AsMemory(0, want), ct);
            await output.WriteAsync(tag, ct);

            done += want;
            progress?.Report(length == 0 ? 1 : (double)done / length);
        }
    }

    public static byte[] Encrypt(byte[] data, byte[] key)
    {
        using var input = new MemoryStream(data, writable: false);
        using var output = new MemoryStream(data.Length + HeaderSize + TagSize * (data.Length / DefaultChunkSize + 1));
        EncryptAsync(input, data.Length, output, key).GetAwaiter().GetResult();
        return output.ToArray();
    }

    public static byte[] Decrypt(byte[] blob, byte[] key)
    {
        using var stream = new VaultReadStream(new MemoryStream(blob, writable: false), key);
        var result = new byte[stream.Length];
        stream.ReadExactly(result);
        return result;
    }

    internal static void NonceFor(ReadOnlySpan<byte> header, int index, Span<byte> nonce)
    {
        header.Slice(16, 12).CopyTo(nonce);
        var counter = BinaryPrimitives.ReadUInt32BigEndian(nonce[8..]) ^ (uint)index;
        BinaryPrimitives.WriteUInt32BigEndian(nonce[8..], counter);
    }

    internal static bool HasMagic(ReadOnlySpan<byte> header) => header.Length >= 4 && header[..4].SequenceEqual(Magic);
}

/// <summary>
/// Read-only, seekable view of a vault file's plaintext. Decrypts one chunk at a time on
/// demand, so memory stays at about one chunk however large the file is.
/// </summary>
public sealed class VaultReadStream : Stream
{
    private readonly Stream _inner;
    private readonly AesGcm _gcm;
    private readonly byte[] _header = new byte[VaultFormat.HeaderSize];
    private readonly int _chunkSize;
    private readonly long _length;
    private readonly byte[] _plain;
    private readonly byte[] _cipher;
    private readonly byte[] _nonce = new byte[12];
    private readonly byte[] _tag = new byte[VaultFormat.TagSize];
    private int _loadedChunk = -1;
    private int _loadedLength;
    private long _position;

    public VaultReadStream(Stream inner, byte[] key)
    {
        _inner = inner;
        _inner.Position = 0;
        _inner.ReadExactly(_header);
        if (!VaultFormat.HasMagic(_header)) throw new CryptographicException("Not a vault file.");

        _chunkSize = BinaryPrimitives.ReadInt32LittleEndian(_header.AsSpan(4));
        _length    = BinaryPrimitives.ReadInt64LittleEndian(_header.AsSpan(8));
        if (_chunkSize <= 0 || _chunkSize > 64 << 20 || _length < 0)
            throw new CryptographicException("Corrupt vault header.");

        _plain  = new byte[_chunkSize];
        _cipher = new byte[_chunkSize];
        _gcm    = new AesGcm(key, VaultFormat.TagSize);
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, _length);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var total = 0;
        while (buffer.Length > 0 && _position < _length)
        {
            var index = (int)(_position / _chunkSize);
            LoadChunk(index);

            var within = (int)(_position - (long)index * _chunkSize);
            var n = Math.Min(buffer.Length, _loadedLength - within);
            _plain.AsSpan(within, n).CopyTo(buffer);

            buffer = buffer[n..];
            _position += n;
            total += n;
        }
        return total;
    }

    private void LoadChunk(int index)
    {
        if (index == _loadedChunk) return;

        var start = (long)index * _chunkSize;
        var size = (int)Math.Min(_chunkSize, _length - start);
        _inner.Position = VaultFormat.HeaderSize + (long)index * (_chunkSize + VaultFormat.TagSize);
        _inner.ReadExactly(_cipher.AsSpan(0, size));
        _inner.ReadExactly(_tag);

        VaultFormat.NonceFor(_header, index, _nonce);
        _gcm.Decrypt(_nonce, _cipher.AsSpan(0, size), _tag, _plain.AsSpan(0, size), _header);

        _loadedChunk = index;
        _loadedLength = size;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin   => offset,
            SeekOrigin.Current => _position + offset,
            _                  => _length + offset,
        };
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _gcm.Dispose();
            _inner.Dispose();
            CryptographicOperations.ZeroMemory(_plain);
        }
        base.Dispose(disposing);
    }
}
