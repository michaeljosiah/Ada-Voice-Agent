using System.Text.RegularExpressions;

namespace Ada.Voice;

/// <summary>
/// Strips text-chat furniture the TTS would otherwise read aloud — markdown emphasis, inline code,
/// code fences, headings, list bullets, bare URLs' angle brackets. The engine writes for a chat
/// bubble; this makes it speakable without changing the words.
/// </summary>
internal static partial class VoiceTextSanitizer
{
    [GeneratedRegex(@"```[\s\S]*?```|`([^`]*)`", RegexOptions.Compiled)]
    private static partial Regex CodeSpans();

    [GeneratedRegex(@"(\*\*|__|\*|_)(?=\S)([\s\S]*?)(?<=\S)\1", RegexOptions.Compiled)]
    private static partial Regex Emphasis();

    [GeneratedRegex(@"^\s{0,3}(#{1,6}\s+|[-*+]\s+|\d+\.\s+)", RegexOptions.Compiled | RegexOptions.Multiline)]
    private static partial Regex LinePrefixes();

    public static string Sanitize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var s = CodeSpans().Replace(text, "$1");     // fences vanish; inline code keeps its content
        s = Emphasis().Replace(s, "$2");             // **bold**/_italics_ keep their words
        s = LinePrefixes().Replace(s, string.Empty); // headings and bullets speak as plain sentences
        return s.Replace("<", string.Empty).Replace(">", string.Empty);
    }
}
