using Ada.Voice;

namespace Ada.Voice.Tests;

/// <summary>
/// The speech sanitizer converts streamed assistant text into speech-safe plain text before TTS —
/// stripping markdown and leaked JSON/tool punctuation, while preserving leading whitespace so
/// per-chunk streaming doesn't glue adjacent words together. (Moved here when M10 retired the old
/// ProcessorTests, whose custom stages UseDefaults() replaced.)
/// </summary>
public class VoiceTextSanitizerTests
{
    [Fact]
    public void Strips_markdown_and_json_punctuation()
    {
        var text = VoiceTextSanitizer.Sanitize("**Done** — `{\"ok\":true}`\n- next");

        Assert.DoesNotContain("*", text);
        Assert.DoesNotContain("`", text);
        Assert.DoesNotContain("{", text);
        Assert.DoesNotContain("}", text);
        Assert.Contains("Done", text);
    }

    [Fact]
    public void Preserves_leading_space_for_streamed_chunks()
    {
        Assert.Equal(" next", VoiceTextSanitizer.Sanitize(" next"));
    }
}
