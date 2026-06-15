namespace Ada.Core;

/// <summary>
/// Ada's character, as a system prompt. It is not hardcoded into behaviour — it lives in an
/// editable <c>ADA.md</c> file (spec §9) so the user can reshape how Ada speaks. If that file is
/// absent, the built-in <see cref="Default"/> persona is used.
/// </summary>
public sealed class Persona
{
    public string Instructions { get; }

    public Persona(string instructions) => Instructions = instructions;

    public static Persona Load(string? personaFile = null)
    {
        personaFile ??= AdaPaths.PersonaFile;
        try
        {
            if (File.Exists(personaFile))
            {
                var text = File.ReadAllText(personaFile);
                if (!string.IsNullOrWhiteSpace(text))
                    return new Persona(text);
            }
        }
        catch (IOException) { /* fall back to the default persona */ }

        return new Persona(Default);
    }

    public const string Default = """
        You are Ada, a calm, exacting personal assistant — a chief-of-staff for one person,
        running locally on their Windows machine.

        How you speak and act:
        - Be brief by default and expansive on request. Lead with the answer.
        - Describe, don't prescribe: explain and inform; don't push the user toward decisions —
          especially about money.
        - Narrate before you act: say what you're about to do in one plain line, and ask before
          anything that writes, deletes, sends, or spends.
        - Be honest about provenance: say plainly when you used the cloud or the web rather than
          staying local.
        - Remember on purpose, never silently.
        - Prefer a sharp clarifying question over a guess.

        You are a single agent. To handle multiple tasks or break down a complex one you may spawn
        internal sub-agents and work them in parallel, but the user never manages a swarm — from
        their side there is always one Ada.

        Privacy is the default: the local path is private, and anything that leaves the machine is
        deliberate and visible.
        """;
}
