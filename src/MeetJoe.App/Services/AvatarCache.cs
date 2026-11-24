using System.Collections.Concurrent;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace MeetJoe.App.Services;

/// <summary>Downloads and caches profile photos keyed by user id + avatar path.</summary>
public sealed class AvatarCache(IMeetJoeApi api)
{
    private readonly ConcurrentDictionary<string, Bitmap?> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task<Bitmap?>> _inflight = new(StringComparer.Ordinal);

    public Bitmap? GetCached(Guid userId, string? avatarUrl)
    {
        if (string.IsNullOrWhiteSpace(avatarUrl)) return null;
        return _cache.TryGetValue(CacheKey(userId, avatarUrl), out var bitmap) ? bitmap : null;
    }

    public async Task<Bitmap?> LoadAsync(Guid userId, string? avatarUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(avatarUrl)) return null;
        var key = CacheKey(userId, avatarUrl);
        if (_cache.TryGetValue(key, out var cached)) return cached;

        var task = _inflight.GetOrAdd(key, _ => DownloadAsync(userId, key, ct));
        try
        {
            return await task;
        }
        finally
        {
            _inflight.TryRemove(key, out _);
        }
    }

    public void SetLocal(Guid userId, string avatarUrl, Bitmap bitmap)
    {
        _cache[CacheKey(userId, avatarUrl)] = bitmap;
    }

    public void Invalidate(Guid userId)
    {
        foreach (var key in _cache.Keys.Where(k => k.StartsWith(userId.ToString("D") + "|", StringComparison.Ordinal)))
            _cache.TryRemove(key, out _);
    }

    private async Task<Bitmap?> DownloadAsync(Guid userId, string cacheKey, CancellationToken ct)
    {
        try
        {
            await using var stream = await api.DownloadAvatarAsync(userId, ct);
            await using var copy = new MemoryStream();
            await stream.CopyToAsync(copy, ct);
            copy.Position = 0;
            var bitmap = new Bitmap(copy);
            _cache[cacheKey] = bitmap;
            return bitmap;
        }
        catch
        {
            _cache[cacheKey] = null;
            return null;
        }
    }

    private static string CacheKey(Guid userId, string avatarUrl) => $"{userId:D}|{avatarUrl}";
}

/// <summary>Shared accessor so PersonViewModel can load photos without constructor churn.</summary>
public static class AvatarImages
{
    public static AvatarCache? Current { get; set; }

    public static void RequestLoad(Action<Bitmap?> apply, Guid userId, string? avatarUrl)
    {
        if (Current is null || string.IsNullOrWhiteSpace(avatarUrl))
        {
            apply(null);
            return;
        }

        var cached = Current.GetCached(userId, avatarUrl);
        if (cached is not null)
        {
            apply(cached);
            return;
        }

        _ = LoadAsync(apply, userId, avatarUrl);
    }

    private static async Task LoadAsync(Action<Bitmap?> apply, Guid userId, string avatarUrl)
    {
        var bitmap = await Current!.LoadAsync(userId, avatarUrl);
        await Dispatcher.UIThread.InvokeAsync(() => apply(bitmap));
    }
}
