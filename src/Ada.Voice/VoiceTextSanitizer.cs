using System.Text.RegularExpressions;

namespace Ada.Voice;

/// <summary>Converts streamed assistant text into speech-safe plain text before it reaches TTS —
/// strips markdown furniture, JSON/bracket punctuation from leaked tool output, and normalizes
/// dashes/ellipses. Sanitizes per streamed chunk, so leading whitespace is preserved (dropping it
/// would glue adjacent words together as the reply streams).</summary>
internal static partial class VoiceTextSanitizer
{
    public static string Sanitize(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var s = text.ReplaceLineEndings("\n");
        s = MarkdownLinePrefix().Replace(s, string.Empty);
        s = s.Replace("```", " ")
             .Replace("`", string.Empty)
             .Replace("**", string.Empty)
             .Replace("*", string.Empty)
             .Replace("__", string.Empty)
             .Replace("_", string.Empty)
             .Replace("•", string.Empty)
             .Replace("…", "...")
             .Replace("—", " - ")
             .Replace("–", " - ");
        s = JsonPunctuation().Replace(s, " ");
        s = Whitespace().Replace(s, " ");
        return s;
    }

    [GeneratedRegex(@"(?m)^\s*(?:#{1,6}|[-+*]|\d+[.)])\s+")]
    private static partial Regex MarkdownLinePrefix();

    [GeneratedRegex(@"[{}\[\]<>]")]
    private static partial Regex JsonPunctuation();

    [GeneratedRegex(@"[ \t\r\n]{2,}")]
    private static partial Regex Whitespace();
}
