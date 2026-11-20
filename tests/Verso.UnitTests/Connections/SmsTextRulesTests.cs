using Verso.Application.Connections;

namespace Verso.UnitTests.Connections;

/// <summary>The SMS text rules: which characters need Unicode (GSM 03.38 basic set plus its extension table), the text and tag rules.</summary>
public sealed class SmsTextRulesTests
{
    [Theory]
    [InlineData("")]
    [InlineData("Dear Customer, your statement 2026-11 is ready.")]
    [InlineData("ABCXYZ abcxyz 0123456789")]
    [InlineData("!\"#%&'()*+,-./:;<=>?_")]
    [InlineData("é è ù ì ò Ç Ø ø Å å")] // é è ù ì ò Ç Ø ø Å å
    [InlineData("Δ Φ Γ Λ Ω Π Ψ Σ Θ Ξ")] // Δ Φ Γ Λ Ω Π Ψ Σ Θ Ξ
    [InlineData("ñ Ñ ü Ü ö Ö ä Ä ß Æ æ É à")] // ñ Ñ ü Ü ö Ö ä Ä ß Æ æ É à
    [InlineData("£ ¥ ¤ ¡ ¿ § @ $")] // £ ¥ ¤ ¡ ¿ § @ $
    [InlineData("€ [ \\ ] ^ { | } ~ \f")] // the extension table: € [ \ ] ^ { | } ~ and form feed
    [InlineData("line one\nline two\r\nline three")]
    public void Gsm_characters_do_not_need_unicode(string text) => Assert.False(SmsRules.NeedsUnicode(text));

    [Theory]
    [InlineData("α")]        // Greek lowercase alpha
    [InlineData("Банк")] // Cyrillic
    [InlineData("\U0001F600")]    // an emoji (a surrogate pair)
    [InlineData("á")]        // á
    [InlineData("ç")]        // ç: only the capital Ç is in the GSM set
    [InlineData("“quoted”")] // curly quotes
    [InlineData("a — b")]    // em dash
    [InlineData("`")]             // the backtick is not in the GSM set
    [InlineData("\t")]            // nor is a tab
    public void Characters_outside_the_gsm_set_need_unicode(string text) => Assert.True(SmsRules.NeedsUnicode(text));

    [Theory]
    [InlineData("Hello", "Hello")]
    [InlineData("a\nb", "a\nb")]
    [InlineData("a\r\nb\r\n", "a\nb\n")] // a CR LF pair is accepted as a line break
    [InlineData(" padded ", " padded ")]
    [InlineData("\U0001F600", "\U0001F600")] // a complete surrogate pair is fine
    public void Valid_texts_are_accepted_and_crlf_becomes_lf(string text, string expected)
    {
        Assert.True(SmsRules.TryNormaliseText(text, out var normalised));
        Assert.Equal(expected, normalised);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\n")]
    [InlineData("a\rb")]          // a lone CR
    [InlineData("a\tb")]          // a tab is a control character
    [InlineData("a\u0000b")]
    [InlineData("a\u001bb")]
    [InlineData("a\u007fb")]
    [InlineData("a\u0085b")]      // a C1 control character
    public void Invalid_texts_are_refused(string? text) => Assert.False(SmsRules.TryNormaliseText(text, out _));

    // Lone surrogates can't be stored in attribute metadata (they turn into U+FFFD), so they come from code, not enumerated at discovery.
    public static TheoryData<string> LoneSurrogates => ["a\ud800b", "a\udc00", "\ud83d", "\ude00\ud83d"];

    [Theory]
    [MemberData(nameof(LoneSurrogates), DisableDiscoveryEnumeration = true)]
    public void Lone_surrogates_are_refused(string text)
    {
        Assert.Contains(text, char.IsSurrogate); // the data really holds surrogate halves (not U+FFFD)
        Assert.False(SmsRules.TryNormaliseText(text, out _));
    }

    [Theory]
    [InlineData("‪")] // LRE
    [InlineData("‫")] // RLE
    [InlineData("‬")] // PDF
    [InlineData("‭")] // LRO
    [InlineData("‮")] // RLO
    [InlineData("⁦")] // LRI
    [InlineData("⁧")] // RLI
    [InlineData("⁨")] // FSI
    [InlineData("⁩")] // PDI
    [InlineData("‎")] // LRM
    [InlineData("‏")] // RLM
    [InlineData("​")] // zero-width space
    [InlineData("⁠")] // word joiner
    [InlineData("﻿")] // byte order mark / zero-width no-break space
    [InlineData("­")] // soft hyphen
    [InlineData("؜")] // Arabic letter mark
    public void Invisible_format_characters_are_refused(string character)
    {
        Assert.False(SmsRules.TryNormaliseText("Visit bank" + character + ".example 35799000000", out var normalised));
        Assert.Equal("", normalised); // nothing of the text (or the number in it) comes back
    }

    public static TheoryData<string> AcceptedUnicode =>
    [
        "\U0001F468‍\U0001F469‍\U0001F467", // a family emoji: joined with ZWJ
        "❤️",                               // a heart with the emoji variation selector
        "Γεια σου",  // Greek
        "\U0001F44D\U0001F3FD",                        // a skin-tone modifier
    ];

    [Theory]
    [MemberData(nameof(AcceptedUnicode), DisableDiscoveryEnumeration = true)]
    public void Emoji_sequences_and_other_scripts_are_accepted(string text)
    {
        Assert.True(SmsRules.TryNormaliseText(text, out var normalised));
        Assert.Equal(text, normalised);
    }

    [Fact]
    public void A_format_character_outside_the_basic_plane_is_refused()
    {
        Assert.False(SmsRules.TryNormaliseText("a\U000E0041b", out _)); // a TAG character: invisible
    }

    [Fact]
    public void The_text_may_be_at_most_480_characters()
    {
        Assert.True(SmsRules.TryNormaliseText(new string('a', SmsRules.MaxTextLength), out _));
        Assert.False(SmsRules.TryNormaliseText(new string('a', SmsRules.MaxTextLength + 1), out _));
        Assert.Equal(480, SmsRules.MaxTextLength);
    }

    [Theory]
    [InlineData("verso-0123456789ab-1", true)]
    [InlineData("A", true)]
    [InlineData("abcdefghijklmnopqrstuvwx", true)]   // 24
    [InlineData("abcdefghijklmnopqrstuvwxy", false)] // 25
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("has space", false)]
    [InlineData("under_score", false)]
    [InlineData("verso-1\n", false)]
    [InlineData("é", false)]
    public void Tags_are_1_to_24_letters_digits_and_dashes(string? tag, bool valid) => Assert.Equal(valid, SmsRules.IsValidTag(tag));
}
