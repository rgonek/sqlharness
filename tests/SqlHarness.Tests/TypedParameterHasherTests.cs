using System.Data;
using System.Globalization;

using SqlHarness.Core;

namespace SqlHarness.Tests;

public sealed class TypedParameterHasherTests
{
    // Layout v1, little-endian: version, count, then each parameter sorted by
    // lower-invariant name. A parameter is name, SQL type, size, precision,
    // scale, null marker, and the invariant typed value.
    private const string IntOneHash = "A6330D71600A451E8003815C030C67346624D341FCB34F720E333847CDE6B8CE";
    private const string NullIntHash = "12DFA01D2820CCE61500F7F33A92D37A8CFF80F07CD59732FCD77C58C30713A8";
    private const string StringHash = "3BD21E36AA3A172469E0B65DC08DCE897A9652782443B43389A24446E9B99EEC";
    private const string DecimalHash = "0E42839AC2F78AAEBF3538BDFBF3FCBFAA073D83FCA1E0033704C19E6C1BA16F";
    private const string EmptyHash = "957B88B12730E646E0F33D3618B77DFA579E8231E3C59C7104BE7165611C8027";

    [Fact]
    public void Hash_returns_uppercase_sha256_of_the_canonical_int_layout()
    {
        var hash = TypedParameterHasher.Hash(SqlParameterParser.Parse(["id:int=1"]));

        Assert.Equal(IntOneHash, hash);
        Assert.Equal(64, hash.Length);
        Assert.Equal(hash, hash.ToUpperInvariant());
        Assert.DoesNotContain("0x", hash, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Hash_locks_null_string_decimal_and_empty_layouts()
    {
        Assert.Equal(NullIntHash, TypedParameterHasher.Hash(SqlParameterParser.Parse(["id:int:null"])));
        Assert.Equal(StringHash, TypedParameterHasher.Hash(SqlParameterParser.Parse(["name:nvarchar=ab"])));
        Assert.Equal(DecimalHash, TypedParameterHasher.Hash(SqlParameterParser.Parse(["amount:decimal(10,2)=1.50"])));
        Assert.Equal(DecimalHash, TypedParameterHasher.Hash(SqlParameterParser.Parse(["amount:decimal(10,2)=1.5"])));
        Assert.Equal(EmptyHash, TypedParameterHasher.Hash([]));
    }

    [Fact]
    public void Hash_is_independent_of_input_order_and_name_case()
    {
        var forward = TypedParameterHasher.Hash(SqlParameterParser.Parse([
            "b:int=2",
            "a:nvarchar(10)=acme",
            "c:decimal(19,4)=1.5000",
        ]));
        var reversed = TypedParameterHasher.Hash(SqlParameterParser.Parse([
            "C:decimal(19,4)=1.5",
            "A:nvarchar(10)=acme",
            "B:int=2",
        ]));

        Assert.Equal(forward, reversed);
    }

    [Fact]
    public void Hash_is_independent_of_culture()
    {
        var inputs = new[]
        {
            "ItemId:int=7",
            "amount:decimal(19,4)=1234.5600",
            "at:datetime2=2026-07-29T12:00:00.1234567",
            "ratio:float=1234.5",
            "approx:real=2.5",
            "label:nvarchar(20)=zażółć",
            "path:hierarchyid=/1/2/",
            "loc:geography=4326;POINT(-122.3 47.6)",
            "shape:geometry=POINT(1 2)",
            "blob:varbinary=AQID",
        };

        var baseline = TypedParameterHasher.Hash(SqlParameterParser.Parse(inputs));
        foreach (var culture in new[] { "pl-PL", "tr-TR", "de-DE" })
        {
            using var _ = new TemporaryCulture(culture);
            Assert.Equal(baseline, TypedParameterHasher.Hash(SqlParameterParser.Parse(inputs)));
        }
    }

    [Fact]
    public void Hash_changes_when_the_typed_value_changes()
    {
        Assert.NotEqual(Hash("id:int=1"), Hash("id:int=2"));
        Assert.NotEqual(Hash("label:nvarchar(20)=alpha"), Hash("label:nvarchar(20)=beta"));
        Assert.NotEqual(
            Hash("at:datetime2=2026-07-29T12:00:00.0000001"),
            Hash("at:datetime2=2026-07-29T12:00:00.0000002"));
        Assert.NotEqual(Hash("path:hierarchyid=/1/"), Hash("path:hierarchyid=/2/"));
        Assert.NotEqual(
            Hash("loc:geography=4326;POINT(-122.3 47.6)"),
            Hash("loc:geography=4269;POINT(-122.3 47.6)"));
    }

    [Fact]
    public void Hash_changes_when_type_size_precision_scale_or_null_changes()
    {
        Assert.NotEqual(Hash("id:int=1"), Hash("id:bigint=1"));
        Assert.NotEqual(Hash("label:nvarchar(10)=alpha"), Hash("label:nvarchar(20)=alpha"));
        Assert.NotEqual(Hash("amount:decimal(10,2)=1.50"), Hash("amount:decimal(12,2)=1.50"));
        Assert.NotEqual(Hash("amount:decimal(10,2)=1.50"), Hash("amount:decimal(10,4)=1.5000"));
        Assert.NotEqual(Hash("id:int:null"), Hash("id:int=0"));
        Assert.NotEqual(Hash("id:int:null"), Hash("id:bigint:null"));
        Assert.NotEqual(Hash("a:int=1"), Hash("b:int=1"));
    }

    [Theory]
    [MemberData(nameof(EquivalentSpellings))]
    public void Hash_uses_the_typed_value_not_the_source_spelling(string left, string right)
    {
        Assert.Equal(Hash(left), Hash(right));
    }

    [Fact]
    public void Hash_distinguishes_every_shared_parameter_type()
    {
        foreach (var (left, right) in DistinctTypeValues())
            Assert.NotEqual(Hash(left), Hash(right));
    }

    [Fact]
    public void Hash_rejects_an_unsupported_value_without_echoing_it()
    {
        const string secret = "unsupported-secret-value";
        var parameter = new SqlHarnessParameter("@id", SqlDbType.Variant, new Echo(secret), null);

        var exception = Assert.Throws<SqlHarnessSafetyException>(() => TypedParameterHasher.Hash([parameter]));

        Assert.Equal("SQL parameter value cannot be hashed.", exception.Message);
        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
    }

    public static TheoryData<string, string> EquivalentSpellings() => new()
    {
        { "id:int=01", "id:int=1" },
        { "id:uniqueidentifier=0f8fad5b-d9cb-469f-a165-70867728950e", "id:uniqueidentifier=0f8fad5bd9cb469fa16570867728950e" },
        { "amount:decimal(19,4)=1.5000", "amount:decimal(19,4)=1.5" },
        { "amount:decimal=-1.20", "amount:decimal=-1.2" },
        { "amount:decimal=0.00", "amount:decimal=0" },
        { "flag:bit=true", "flag:bit=1" },
        { "flag:bit=false", "flag:bit=0" },
        { "ratio:float=1.50", "ratio:float=1.5" },
        { "at:datetime=2026-07-29T12:00:00", "at:datetime=2026-07-29 12:00:00" },
        { "price:money=1.25", "price:money=1.2500" },
        { "at:datetimeoffset=2026-07-29T12:00:00Z", "at:datetimeoffset=2026-07-29T12:00:00+00:00" },
    };

    private static IEnumerable<(string Left, string Right)> DistinctTypeValues()
    {
        yield return ("n:nvarchar=alpha", "n:nvarchar=beta");
        yield return ("n:nvarchar(max)=alpha", "n:nvarchar(max)=alphabet");
        yield return ("n:varchar=alpha", "n:varchar=beta");
        yield return ("n:varchar(max)=alpha", "n:varchar(max)=alphabet");
        yield return ("n:char(4)=abcd", "n:char(4)=abce");
        yield return ("n:nchar(4)=abcd", "n:nchar(4)=abce");
        yield return ("n:int=1", "n:int=2");
        yield return ("n:bigint=1", "n:bigint=2");
        yield return ("n:smallint=1", "n:smallint=2");
        yield return ("n:tinyint=1", "n:tinyint=2");
        yield return ("n:bit=0", "n:bit=1");
        yield return ("n:decimal=1.5", "n:decimal=2.5");
        yield return ("n:decimal(19,4)=1.5000", "n:decimal(19,4)=2.5000");
        yield return ("n:numeric=1.5", "n:numeric=2.5");
        yield return ("n:numeric(10,2)=1.50", "n:numeric(10,2)=2.50");
        yield return ("n:float=1.5", "n:float=2.5");
        yield return ("n:real=1.5", "n:real=2.5");
        yield return ("n:money=1.25", "n:money=2.25");
        yield return ("n:smallmoney=1.25", "n:smallmoney=2.25");
        yield return ("n:date=2026-07-29", "n:date=2026-07-30");
        yield return ("n:time=14:30:00", "n:time=14:31:00");
        yield return ("n:datetime=2026-07-29T12:00:00", "n:datetime=2026-07-29T12:00:01");
        yield return ("n:datetime2=2026-07-29T12:00:00.0000001", "n:datetime2=2026-07-29T12:00:00.0000002");
        yield return ("n:smalldatetime=2026-07-29T12:00:00", "n:smalldatetime=2026-07-29T12:01:00");
        yield return ("n:datetimeoffset=2026-07-29T12:00:00+02:00", "n:datetimeoffset=2026-07-29T12:00:00+00:00");
        yield return ("n:uniqueidentifier=0f8fad5b-d9cb-469f-a165-70867728950e", "n:uniqueidentifier=11111111-1111-1111-1111-111111111111");
        yield return ("n:varbinary=AQID", "n:varbinary=BAUG");
        yield return ("n:varbinary(max)=AQID", "n:varbinary(max)=BAUGBA==");
        yield return ("n:binary(2)=AQI=", "n:binary(2)=AgM=");
        yield return ("n:hierarchyid=/1/", "n:hierarchyid=/2/");
        yield return ("n:geography=POINT(-122.3 47.6)", "n:geography=POINT(-122.4 47.6)");
        yield return ("n:geometry=POINT(1 2)", "n:geometry=POINT(3 4)");
        yield return ("n:int:null", "n:int=0");
        yield return ("n:nvarchar:null", "n:nvarchar=x");
        yield return ("n:decimal(19,4):null", "n:decimal(19,4)=0");
        yield return ("n:varbinary:null", "n:varbinary=AQID");
        yield return ("n:datetime2:null", "n:datetime2=2026-07-29T12:00:00");
        yield return ("n:uniqueidentifier:null", "n:uniqueidentifier=0f8fad5b-d9cb-469f-a165-70867728950e");
        yield return ("n:hierarchyid:null", "n:hierarchyid=/1/");
        yield return ("n:geography:null", "n:geography=POINT(0 0)");
        yield return ("n:bit:null", "n:bit=0");
    }

    private static string Hash(string input) =>
        TypedParameterHasher.Hash(SqlParameterParser.Parse([input]));

    private sealed class Echo(string text)
    {
        public override string ToString() => text;
    }

    private sealed class TemporaryCulture : IDisposable
    {
        private readonly CultureInfo _originalCulture = CultureInfo.CurrentCulture;
        private readonly CultureInfo _originalUiCulture = CultureInfo.CurrentUICulture;

        public TemporaryCulture(string name)
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(name);
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(name);
        }

        public void Dispose()
        {
            CultureInfo.CurrentCulture = _originalCulture;
            CultureInfo.CurrentUICulture = _originalUiCulture;
        }
    }
}