namespace Historia.Models;

[InlineArray(32)]
internal struct GitObjectIdBuffer
{
    private byte _element0;
}

[StructLayout(LayoutKind.Sequential, Pack = 1)]
public readonly struct GitObjectId : IEquatable<GitObjectId>, IComparable<GitObjectId>, ISpanFormattable
{
    public static readonly GitObjectId Zero = new(default, 20);

    private readonly GitObjectIdBuffer _buffer;
    private readonly byte _length;

    private GitObjectId(GitObjectIdBuffer buffer, byte length)
    {
        _buffer = buffer;
        _length = length;
    }

    public int HashSize => _length;

    public bool IsEmpty => _length == 0;

    public bool IsZero => !IsEmpty && AsSpan().IndexOfAnyExcept((byte)0) < 0;

    public ReadOnlySpan<byte> AsSpan()
    {
        if (_length == 0)
            return ReadOnlySpan<byte>.Empty;

        return MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in _buffer[0]), _length);
    }

    public void CopyTo(Span<byte> destination)
    {
        if (destination.Length < _length)
            throw new ArgumentException($"Destination span length ({destination.Length}) is less than object id length ({_length}).", nameof(destination));

        AsSpan().CopyTo(destination);
    }

    public GitObjectId(ReadOnlySpan<byte> bytes)
    {
        this = FromBytes(bytes);
    }

    public GitObjectId(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        this = FromBytes(bytes.AsSpan());
    }

    public GitObjectId(string sha)
    {
        this = Parse(sha);
    }

    public GitObjectId(ReadOnlySpan<char> chars)
    {
        this = Parse(chars);
    }

    public static GitObjectId FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 20 && bytes.Length != 32)
            throw new ArgumentException($"Byte span must be 20 or 32 bytes (got {bytes.Length}).", nameof(bytes));

        GitObjectIdBuffer buffer = default;
        Span<byte> dest = MemoryMarshal.CreateSpan(ref buffer[0], bytes.Length);
        bytes.CopyTo(dest);
        return new GitObjectId(buffer, (byte)bytes.Length);
    }

    public static GitObjectId Parse(ReadOnlySpan<char> s)
    {
        if (TryParse(s, out var result))
            return result;

        throw new FormatException($"Invalid git object id: '{s}'.");
    }

    public static GitObjectId Parse(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return Parse(s.AsSpan());
    }

    public static bool TryParse(ReadOnlySpan<char> s, out GitObjectId result)
    {
        if (s.Length != 40 && s.Length != 64)
        {
            result = default;
            return false;
        }

        GitObjectIdBuffer buffer = default;
        Span<byte> dest = MemoryMarshal.CreateSpan(ref buffer[0], s.Length / 2);
        for (int i = 0; i < dest.Length; i++)
        {
            int h1 = FromHexDigit(s[i * 2]);
            int h2 = FromHexDigit(s[i * 2 + 1]);
            if ((h1 | h2) < 0)
            {
                result = default;
                return false;
            }
            dest[i] = (byte)((h1 << 4) | h2);
        }

        result = new GitObjectId(buffer, (byte)dest.Length);
        return true;
    }

    private static int FromHexDigit(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1
    };

    public static bool TryParse(string? s, out GitObjectId result)
    {
        if (s is null)
        {
            result = default;
            return false;
        }

        return TryParse(s.AsSpan(), out result);
    }

    public override string ToString()
    {
        if (_length == 0)
            return string.Empty;

        return Convert.ToHexStringLower(AsSpan());
    }

    public string ToShortString(int length = 7)
    {
        if (_length == 0 || length <= 0)
            return string.Empty;

        int totalChars = _length * 2;
        int charsToFormat = Math.Min(length, totalChars);

        return string.Create(charsToFormat, this, static (span, id) =>
        {
            const string HexDigits = "0123456789abcdef";
            var bytes = id.AsSpan();
            for (int i = 0; i < span.Length; i++)
            {
                int byteIndex = i >> 1;
                byte b = bytes[byteIndex];
                span[i] = (i & 1) == 0 ? HexDigits[b >> 4] : HexDigits[b & 0xF];
            }
        });
    }

    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
    {
        if (_length == 0)
        {
            charsWritten = 0;
            return true;
        }

        int requiredChars = _length * 2;
        if (destination.Length < requiredChars)
        {
            charsWritten = 0;
            return false;
        }

        const string HexDigits = "0123456789abcdef";
        var bytes = AsSpan();
        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = bytes[i];
            destination[i * 2] = HexDigits[b >> 4];
            destination[i * 2 + 1] = HexDigits[b & 0xF];
        }

        charsWritten = requiredChars;
        return true;
    }

    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();

    public int CompareTo(GitObjectId other)
    {
        return AsSpan().SequenceCompareTo(other.AsSpan());
    }

    public bool Equals(GitObjectId other)
    {
        return _length == other._length && AsSpan().SequenceEqual(other.AsSpan());
    }

    public override bool Equals(object? obj) => obj is GitObjectId other && Equals(other);

    public override int GetHashCode()
    {
        // Hash every byte, not a prefix: synthetic ids (zero-padded counters in tests, and any
        // id family sharing a prefix) would otherwise collide and degrade dictionaries to O(n).
        var hash = new HashCode();
        hash.AddBytes(AsSpan());
        return hash.ToHashCode();
    }

    public static bool operator ==(GitObjectId left, GitObjectId right) => left.Equals(right);
    public static bool operator !=(GitObjectId left, GitObjectId right) => !left.Equals(right);
    public static bool operator <(GitObjectId left, GitObjectId right) => left.CompareTo(right) < 0;
    public static bool operator <=(GitObjectId left, GitObjectId right) => left.CompareTo(right) <= 0;
    public static bool operator >(GitObjectId left, GitObjectId right) => left.CompareTo(right) > 0;
    public static bool operator >=(GitObjectId left, GitObjectId right) => left.CompareTo(right) >= 0;
}
