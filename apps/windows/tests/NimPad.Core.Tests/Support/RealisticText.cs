using System.Globalization;
using System.Text;

namespace NimPad.Core.Tests.Support;

/// <summary>
/// Deterministic note-like text: meeting notes, plans, checklists, recipes, travel and reading notes, with Markdown
/// headings, bullets, the odd code block, dates, names and some non-ASCII words. The same seed gives the same text.
/// </summary>
internal sealed class RealisticText(int seed)
{
    private static readonly string[] People =
        ["Mara", "Jonas", "Tess", "Ilke", "Priya", "Diego", "Amélie", "Kenji", "Fatima", "Oskar", "Lena", "Sam"];

    private static readonly string[] Projects =
        ["Atlas", "Borealis", "NimPad", "Halcyon", "Quill", "Meridian", "Orchard", "Lighthouse", "Kestrel", "Sable"];

    private static readonly string[] Topics =
        ["roadmap", "budget", "release notes", "sprint planning", "customer feedback", "api design", "migration",
         "performance review", "launch checklist", "deploy", "invoice", "onboarding", "retrospective", "hiring",
         "security audit", "kubernetes cluster", "database backup", "password reset", "pricing", "user research"];

    private static readonly string[] Verbs =
        ["reviewed", "drafted", "shipped", "postponed", "estimated", "approved", "questioned", "refactored", "tested",
         "documented", "presented", "prioritized", "merged", "rolled back", "summarized", "scheduled"];

    private static readonly string[] Objects =
        ["the proposal", "the dashboard", "the invoice", "the release", "the onboarding flow", "the schema change",
         "the pricing page", "the bug fix", "the test plan", "the travel budget", "the hiring plan", "the API contract",
         "the backup job", "the search index", "the meeting agenda", "the design tokens", "the support macros"];

    private static readonly string[] Adjectives =
        ["quarterly", "urgent", "revised", "final", "rough", "detailed", "simplified", "risky", "overdue", "promising"];

    private static readonly string[] Cities =
        ["Lisbon", "Kyoto", "Montréal", "Reykjavík", "Oslo", "Porto", "Zürich", "Seoul", "Austin", "Kraków"];

    private static readonly string[] Dishes =
        ["lentil soup", "crème brûlée", "shakshuka", "ramen", "banana bread", "risotto", "pierogi", "tacos al pastor"];

    private static readonly string[] Books =
        ["The Pragmatic Programmer", "Thinking, Fast and Slow", "Designing Data-Intensive Applications",
         "The Design of Everyday Things", "Deep Work", "A Philosophy of Software Design"];

    private static readonly string[] Weekdays = ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"];

    private static readonly string[] Extensions = [".md", ".md", ".md", ".txt", string.Empty, string.Empty, ".json", ".yaml"];

    private readonly Random _random = new(seed);

    public string FolderName() => Pick(
        () => $"{Pick(Projects)}",
        () => $"{Capitalize(Pick(Topics))}",
        () => $"{2024 + _random.Next(3)}",
        () => $"{CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(1 + _random.Next(12))}",
        () => $"{Pick(People)}'s notes",
        () => "Archive",
        () => $"Trips to {Pick(Cities)}");

    public string NoteName()
    {
        var stem = Pick(
            () => $"Meeting notes {Date()}",
            () => $"{Pick(Projects)} {Pick(Topics)}",
            () => $"Weekly sync {1 + _random.Next(52)}",
            () => $"Recipe — {Pick(Dishes)}",
            () => $"Trip to {Pick(Cities)}",
            () => $"Reading: {Pick(Books)}",
            () => $"{Capitalize(Pick(Adjectives))} {Pick(Topics)} ideas",
            () => "Todo");
        return stem + Pick(Extensions);
    }

    public string NoteBody(int minLength = 1000, int maxLength = 2000)
    {
        var target = minLength + _random.Next(maxLength - minLength + 1);
        var builder = new StringBuilder(target + 200);
        builder.Append("# ").Append(Capitalize(Pick(Topics))).Append(" — ").Append(Pick(Projects)).Append('\n').Append('\n');
        while (builder.Length < target)
        {
            switch (_random.Next(6))
            {
                case 0:
                case 1:
                case 2:
                    for (var i = 0; i < 2 + _random.Next(3); i++)
                    {
                        builder.Append(Sentence()).Append(' ');
                    }

                    builder.Length--;
                    builder.Append("\n\n");
                    break;
                case 3:
                    builder.Append("## ").Append(Capitalize(Pick(Adjectives))).Append(' ').Append(Pick(Topics)).Append("\n\n");
                    for (var i = 0; i < 3 + _random.Next(4); i++)
                    {
                        builder.Append(_random.Next(3) == 0 ? "- [ ] " : "- ").Append(Bullet()).Append('\n');
                    }

                    builder.Append('\n');
                    break;
                case 4:
                    builder.Append("Todo: ").Append(Bullet()).Append(". Owner: ").Append(Pick(People)).Append(", due ").Append(Date()).Append(".\n\n");
                    break;
                default:
                    builder.Append("```\n").Append(Code()).Append("\n```\n\n");
                    break;
            }
        }

        return builder.ToString().TrimEnd() + "\n";
    }

    private string Sentence() => Pick(
        () => $"{Pick(People)} {Pick(Verbs)} {Pick(Objects)} for {Pick(Projects)} before {Pick(Weekdays)}.",
        () => $"We agreed that the {Pick(Adjectives)} {Pick(Topics)} needs another pass by {Date()}.",
        () => $"Ship the beta to {10 * (1 + _random.Next(20))} users on {Pick(Weekdays)} if {Pick(Objects)} is ready.",
        () => $"{Pick(People)} asked whether {Pick(Objects)} affects the {Pick(Topics)}; follow up with {Pick(People)}.",
        () => $"The {Pick(Topics)} for {Pick(Projects)} in Q{1 + _random.Next(4)} is {_random.Next(5, 95)}% done and {Pick(Adjectives)}.",
        () => $"Budget impact: {_random.Next(1, 90)}k EUR, mostly travel to {Pick(Cities)} and tooling.",
        () => $"Remember to try the {Pick(Dishes)} recipe from {Pick(People)} on {Pick(Weekdays)}.",
        () => $"Quote from {Pick(Books)}: keep modules deep and interfaces simple.");

    private string Bullet() => Pick(
        () => $"{Capitalize(Pick(Verbs))} {Pick(Objects)}",
        () => $"Check the {Pick(Topics)} with {Pick(People)}",
        () => $"Book the hotel in {Pick(Cities)}",
        () => $"Update {Pick(Projects)} {Pick(Topics)} doc",
        () => $"Buy groceries for {Pick(Dishes)}");

    private string Code() => Pick(
        () => $"kubectl rollout restart deployment/{Pick(Projects).ToLowerInvariant()}-api",
        () => $"SELECT id, name FROM invoices WHERE due < '{Date()}';",
        () => $"{{ \"project\": \"{Pick(Projects)}\", \"budget\": {_random.Next(1000, 99999)} }}",
        () => $"git checkout -b fix/{Pick(Topics).Replace(' ', '-')}");

    private string Date() => new DateOnly(2026, 1 + _random.Next(12), 1 + _random.Next(28)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private T Pick<T>(IReadOnlyList<T> items) => items[_random.Next(items.Count)];

    private string Pick(params Func<string>[] makers) => makers[_random.Next(makers.Length)]();

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
