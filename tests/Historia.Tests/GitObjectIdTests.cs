namespace Historia.Tests;

public class GitObjectIdTests
{
    private class TestEntity
    {
        public GitObjectId NonNullableId { get; set; }
        public GitObjectId? NullableId { get; set; }
    }

    [Fact]
    public void RoundTrip_Hex_Sha1AndSha256()
    {
        const string sha1Hex = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";
        const string sha256Hex = "473a0f4c3be8a93681a267e3b1e9a7dcda1185436fe141f7749120a303721813";

        var id1 = GitObjectId.Parse(sha1Hex);
        Assert.Equal(20, id1.HashSize);
        Assert.False(id1.IsEmpty);
        Assert.Equal(sha1Hex, id1.ToString());

        var id2 = GitObjectId.Parse(sha256Hex);
        Assert.Equal(32, id2.HashSize);
        Assert.False(id2.IsEmpty);
        Assert.Equal(sha256Hex, id2.ToString());

        // Case-insensitivity test
        var upperSha1 = sha1Hex.ToUpperInvariant();
        var idUpper = GitObjectId.Parse(upperSha1);
        Assert.Equal(sha1Hex, idUpper.ToString());
    }

    [Fact]
    public void RoundTrip_Bytes_Sha1AndSha256()
    {
        byte[] bytes20 = Enumerable.Range(1, 20).Select(b => (byte)b).ToArray();
        byte[] bytes32 = Enumerable.Range(1, 32).Select(b => (byte)b).ToArray();

        var id20 = GitObjectId.FromBytes(bytes20);
        Assert.Equal(20, id20.HashSize);
        byte[] dest20 = new byte[20];
        id20.CopyTo(dest20);
        Assert.Equal(bytes20, dest20);

        var id32 = GitObjectId.FromBytes(bytes32);
        Assert.Equal(32, id32.HashSize);
        byte[] dest32 = new byte[32];
        id32.CopyTo(dest32);
        Assert.Equal(bytes32, dest32);
    }

    [Fact]
    public void Rejects_InvalidLength_And_NonHex()
    {
        int[] invalidLengths = [39, 41, 63, 65, 0, 10];
        foreach (int len in invalidLengths)
        {
            string s = new string('a', len);
            Assert.False(GitObjectId.TryParse(s, out _));
            Assert.Throws<FormatException>(() => GitObjectId.Parse(s));
        }

        string invalidHex40 = new string('a', 39) + "z";
        Assert.False(GitObjectId.TryParse(invalidHex40, out _));
        Assert.Throws<FormatException>(() => GitObjectId.Parse(invalidHex40));

        string invalidHex64 = new string('a', 63) + "!";
        Assert.False(GitObjectId.TryParse(invalidHex64, out _));
        Assert.Throws<FormatException>(() => GitObjectId.Parse(invalidHex64));

        Assert.Throws<ArgumentException>(() => GitObjectId.FromBytes(new byte[19]));
        Assert.Throws<ArgumentException>(() => GitObjectId.FromBytes(new byte[21]));
        Assert.Throws<ArgumentException>(() => GitObjectId.FromBytes(new byte[31]));
        Assert.Throws<ArgumentException>(() => GitObjectId.FromBytes(new byte[33]));
    }

    [Fact]
    public void Equality_And_HashCode()
    {
        const string hex = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";
        var id1 = GitObjectId.Parse(hex);
        var id2 = GitObjectId.Parse(hex);
        var id3 = GitObjectId.Parse("0123456789abcdef0123456789abcdef01234567");

        Assert.True(id1 == id2);
        Assert.False(id1 != id2);
        Assert.Equal(id1, id2);
        Assert.Equal(id1.GetHashCode(), id2.GetHashCode());

        Assert.True(id1 != id3);
        Assert.False(id1 == id3);
        Assert.NotEqual(id1, id3);

        var def = default(GitObjectId);
        Assert.True(def.IsEmpty);
        Assert.False(def.IsZero);
        Assert.True(def != GitObjectId.Zero);
        Assert.NotEqual(def, GitObjectId.Zero);

        Assert.False(GitObjectId.Zero.IsEmpty);
        Assert.True(GitObjectId.Zero.IsZero);
        Assert.Equal(20, GitObjectId.Zero.HashSize);
    }

    [Fact]
    public void CompareTo_AgreesWith_StringCompareOrdinal()
    {
        var rng = new Random(42);

        void CheckRandom(int byteCount, int count)
        {
            var ids = new GitObjectId[count];
            byte[] buf = new byte[byteCount];
            for (int i = 0; i < count; i++)
            {
                rng.NextBytes(buf);
                ids[i] = GitObjectId.FromBytes(buf);
            }

            for (int i = 0; i < count - 1; i++)
            {
                var a = ids[i];
                var b = ids[i + 1];

                int structCmp = a.CompareTo(b);
                int stringCmp = string.CompareOrdinal(a.ToString(), b.ToString());

                Assert.Equal(Math.Sign(stringCmp), Math.Sign(structCmp));
            }
        }

        CheckRandom(20, 1000);
        CheckRandom(32, 1000);
    }

    [Fact]
    public void ToShortString_FormatsOnlyRequestedPrefix()
    {
        const string hex = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";
        var id = GitObjectId.Parse(hex);

        Assert.Equal("4b825dc", id.ToShortString(7));
        Assert.Equal("4b82", id.ToShortString(4));
        Assert.Equal(string.Empty, id.ToShortString(0));
        Assert.Equal(hex, id.ToShortString(50));

        Span<char> span = stackalloc char[40];
        Assert.True(id.TryFormat(span, out int charsWritten, default, null));
        Assert.Equal(40, charsWritten);
        Assert.Equal(hex, span.ToString());
    }

    [Fact]
    public void MemoryLayout_NoReferences_And_SizeUnder40()
    {
        Assert.False(System.Runtime.CompilerServices.RuntimeHelpers.IsReferenceOrContainsReferences<GitObjectId>());
        int size = System.Runtime.CompilerServices.Unsafe.SizeOf<GitObjectId>();
        Assert.True(size <= 40, $"Expected size <= 40, was {size}");
    }

    [Fact]
    public void JsonConverters_RoundTrip_And_ErrorHandling()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new NullableGitObjectIdJsonConverter());
        options.Converters.Add(new GitObjectIdJsonConverter());

        const string hex = "4b825dc642cb6eb9a060e54bf8d69288fbee4904";
        var entity = new TestEntity
        {
            NonNullableId = GitObjectId.Parse(hex),
            NullableId = GitObjectId.Parse(hex)
        };

        string json = JsonSerializer.Serialize(entity, options);
        Assert.Contains(hex, json);

        var deserialized = JsonSerializer.Deserialize<TestEntity>(json, options);
        Assert.NotNull(deserialized);
        Assert.Equal(entity.NonNullableId, deserialized.NonNullableId);
        Assert.Equal(entity.NullableId, deserialized.NullableId);

        // Nullable property with malformed string -> deserializes as null without throwing
        string jsonWithBadNullable = $$"""{"NonNullableId":"{{hex}}","NullableId":"malformed-sha"}""";
        var badNullableResult = JsonSerializer.Deserialize<TestEntity>(jsonWithBadNullable, options);
        Assert.NotNull(badNullableResult);
        Assert.Null(badNullableResult.NullableId);
        Assert.Equal(entity.NonNullableId, badNullableResult.NonNullableId);

        // Nullable property with null -> null
        string jsonWithNullNullable = $$"""{"NonNullableId":"{{hex}}","NullableId":null}""";
        var nullNullableResult = JsonSerializer.Deserialize<TestEntity>(jsonWithNullNullable, options);
        Assert.NotNull(nullNullableResult);
        Assert.Null(nullNullableResult.NullableId);

        // NonNullable property with malformed string -> throws JsonException
        string jsonWithBadNonNullable = """{"NonNullableId":"bad-sha","NullableId":null}""";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<TestEntity>(jsonWithBadNonNullable, options));
    }
}
