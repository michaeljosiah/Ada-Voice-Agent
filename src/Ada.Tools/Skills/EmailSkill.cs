using Ada.Core;
using Microsoft.Extensions.AI;

namespace Ada.Tools;

/// <summary>
/// The email triage skill: it contributes the read-only mail tools and the triage playbook. Off by
/// default — the user turns it on once they connect an account (Settings → Email). The connectors run
/// host-side (they need the vaulted OAuth tokens and network egress), so this is a code skill, not a
/// sandbox script. Phase 1 is read-only; archive/move/send arrive later behind the approval gate.
/// </summary>
public sealed class EmailSkill(EmailTools tools) : ISkill
{
    public string Name => "email";
    public bool EnabledByDefault => false; // turned on once an account is connected

    public string? InstructionFragment =>
        "You can triage the user's email across all their connected accounts (Outlook today; more providers " +
        "later) with email_accounts, list_email and read_email. These are READ-ONLY: you can read and summarise, but " +
        "you cannot archive, delete, reply, mark-read or move anything — never claim you did. " +
        "The user finds a full inbox hard to face, so your job is to surface the few things that matter, not " +
        "to list everything. When they ask about email (or to 'triage', 'catch me up', 'what's important'): " +
        "pull recent unread across all accounts, then sort into — (1) Important: a real person, a deadline, a " +
        "reply owed, an account/security notice; (2) FYI: informational but not urgent; (3) Noise: newsletters, " +
        "promotions, automated notifications. Lead with Important — for each, give the sender, a one-line why-it-" +
        "matters, and which account it's in. Keep the FYI and Noise to short counts/groupings unless asked to expand. " +
        "Only open a message body (read_email) when you need it to judge importance or to summarise. Never invent " +
        "senders, subjects or content; if nothing is connected, tell them to add an account in Settings → Email.";

    public IReadOnlyList<AITool> Tools =>
    [
        AIFunctionFactory.Create(tools.EmailAccounts, "email_accounts"),
        AIFunctionFactory.Create(tools.ListEmail, "list_email"),
        AIFunctionFactory.Create(tools.ReadEmail, "read_email"),
    ];

    public McpMount? Mcp => null;
}
