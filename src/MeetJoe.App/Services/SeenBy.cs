namespace MeetJoe.App.Services;

/// <summary>Formats the “Seen” / “Seen by everyone” line under the latest outgoing message.</summary>
public static class SeenBy
{
    public static string? Format(
        DateTimeOffset messageAt,
        Guid myId,
        IEnumerable<(Guid UserId, string DisplayName)> peers,
        IReadOnlyDictionary<Guid, DateTimeOffset> readAt)
    {
        var others = peers
            .Where(p => p.UserId != myId)
            .GroupBy(p => p.UserId)
            .Select(g => g.First())
            .ToList();
        if (others.Count == 0) return null;

        var seen = others
            .Where(p => readAt.TryGetValue(p.UserId, out var at) && at >= messageAt)
            .ToList();
        if (seen.Count == 0) return null;
        if (seen.Count >= others.Count)
            return others.Count == 1 ? "Seen" : "Seen by everyone";

        var names = seen.Select(p => FirstName(p.DisplayName)).ToList();
        return names.Count switch
        {
            1 => $"Seen by {names[0]}",
            2 => $"Seen by {names[0]} and {names[1]}",
            _ => $"Seen by {names[0]}, {names[1]}, and {names.Count - 2} others",
        };
    }

    private static string FirstName(string displayName)
    {
        var space = displayName.IndexOf(' ');
        return space > 0 ? displayName[..space] : displayName;
    }
}
