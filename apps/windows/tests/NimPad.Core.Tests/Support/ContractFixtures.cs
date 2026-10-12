using System.Text.Json;

namespace NimPad.Core.Tests.Support;

/// <summary>
/// The shared contract fixtures from packages/contracts/fixtures, copied next to the test assembly by the project
/// file. A missing file fails the test: the fixtures are part of the repository.
/// </summary>
internal static class ContractFixtures
{
    public static JsonElement Load(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", fileName);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.Clone();
    }

    public static string? GetStringOrNull(this JsonElement element, string property) =>
        element.GetProperty(property).ValueKind == JsonValueKind.Null ? null : element.GetProperty(property).GetString();
}
