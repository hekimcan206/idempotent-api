using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using static IdempotentApi.IdempotencyKey;

namespace IdempotentApi.UnitTests;

public sealed class IdempotencyKeyTests
{
    private static bool Parse(string? value, out string? key, out ParseError error, bool bare = false, int min = 1, int max = 255) =>
        TryParse(value, bare, min, max, out key, out error);

    [Theory]
    [InlineData("\"8e03978e-40d5-43e8-bc93-6894a57f9324\"", "8e03978e-40d5-43e8-bc93-6894a57f9324")]
    [InlineData("  \"abc\"  ", "abc")]
    [InlineData("\"with space\"", "with space")]
    [InlineData("\"q\\\"uote\"", "q\"uote")]
    [InlineData("\"back\\\\slash\"", "back\\slash")]
    [InlineData("\"~!#$%&'()*+,-./:;<=>?@[]^_`{|}\"", "~!#$%&'()*+,-./:;<=>?@[]^_`{|}")]
    public void Accepts_sf_strings(string field, string expected)
    {
        Parse(field, out var key, out var error).Should().BeTrue();
        key.Should().Be(expected);
        error.Should().Be(ParseError.None);
    }

    [Theory]
    [InlineData(null, ParseError.Empty)]
    [InlineData("", ParseError.Empty)]
    [InlineData("   ", ParseError.Empty)]
    [InlineData("8e03978e-40d5", ParseError.NotAString)]
    [InlineData("\"unterminated", ParseError.Unterminated)]
    [InlineData("\"ends in escape\\", ParseError.Unterminated)]
    [InlineData("\"bad \\n escape\"", ParseError.InvalidEscape)]
    [InlineData("\"tab\there\"", ParseError.InvalidCharacter)]
    [InlineData("\"çok\"", ParseError.InvalidCharacter)]
    [InlineData("\"a\";p=1", ParseError.TrailingCharacters)]
    [InlineData("\"a\", \"b\"", ParseError.TrailingCharacters)]
    [InlineData("\"\"", ParseError.TooShort)]
    public void Rejects_malformed_values(string? field, ParseError expected)
    {
        Parse(field, out var key, out var error).Should().BeFalse();
        key.Should().BeNull();
        error.Should().Be(expected);
    }

    [Fact]
    public void Enforces_length_limits_on_the_decoded_key()
    {
        Parse("\"short\"", out _, out var tooShort, min: 16).Should().BeFalse();
        tooShort.Should().Be(ParseError.TooShort);

        Parse("\"" + new string('k', 33) + "\"", out _, out var tooLong, max: 32).Should().BeFalse();
        tooLong.Should().Be(ParseError.TooLong);

        // Escapes count once: the limit applies to what is stored, not to the wire form.
        Parse("\"" + string.Concat(Enumerable.Repeat("\\\"", 16)) + "\"", out var key, out _, min: 16, max: 16).Should().BeTrue();
        key.Should().HaveLength(16);
    }

    [Theory]
    [InlineData("8e03978e-40d5-43e8-bc93-6894a57f9324", true)]
    [InlineData("has space", false)]
    [InlineData("semi;colon", false)]
    [InlineData("comma,separated", false)]
    [InlineData("quo\"te", false)]
    public void Bare_tokens_are_opt_in(string field, bool acceptedWhenAllowed)
    {
        Parse(field, out _, out var strictError).Should().BeFalse();
        strictError.Should().Be(ParseError.NotAString);

        Parse(field, out var key, out _, bare: true).Should().Be(acceptedWhenAllowed);
        if (acceptedWhenAllowed)
        {
            key.Should().Be(field);
        }
    }

    [Fact]
    public void Format_rejects_non_printable_ascii()
    {
        var act = () => Format("ünicode");
        act.Should().Throw<ArgumentException>();
    }

    private static Arbitrary<string> PrintableAscii() =>
        Gen.Choose(0x20, 0x7E).Select(c => (char)c).ArrayOf().Select(cs => new string(cs)).Where(s => s.Length > 0).ToArbitrary();

    [Property(MaxTest = 500)]
    public Property Format_then_parse_round_trips_any_printable_ascii_key() =>
        Prop.ForAll(PrintableAscii(), key =>
            TryParse(Format(key), allowBareToken: false, 1, int.MaxValue, out var parsed, out _) && parsed == key);

    [Property(MaxTest = 500)]
    public Property Parser_never_throws_on_arbitrary_input() =>
        Prop.ForAll(ArbMap.Default.ArbFor<string>(), raw =>
        {
            TryParse(raw, allowBareToken: true, 1, 255, out var key, out var error);
            return (key is null) == (error != ParseError.None);
        });
}
