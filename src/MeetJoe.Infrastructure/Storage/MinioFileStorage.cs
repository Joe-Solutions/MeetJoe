using MeetJoe.Application.Common;
using Microsoft.Extensions.Options;
using Minio;
using Minio.DataModel.Args;

namespace MeetJoe.Infrastructure.Storage;

public sealed class StorageOptions
{
    public const string Section = "Storage";

    /// <summary>Host and port of the S3-compatible endpoint, e.g. <c>localhost:9000</c>.</summary>
    public string Endpoint { get; set; } = string.Empty;

    public string AccessKey { get; set; } = string.Empty;
    public string SecretKey { get; set; } = string.Empty;
    public string Bucket { get; set; } = "meetjoe-files";
    public bool UseSsl { get; set; }
}

/// <summary>Object storage over the S3 API (MinIO locally).</summary>
public sealed class MinioFileStorage : IFileStorage
{
    private readonly IMinioClient _client;
    private readonly string _bucket;
    private readonly SemaphoreSlim _bucketGate = new(1, 1);
    private bool _bucketReady;

    public MinioFileStorage(IOptions<StorageOptions> options)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.Endpoint))
            throw new InvalidOperationException("Storage:Endpoint is not configured.");

        _bucket = settings.Bucket;
        _client = new MinioClient()
            .WithEndpoint(settings.Endpoint)
            .WithCredentials(settings.AccessKey, settings.SecretKey)
            .WithSSL(settings.UseSsl)
            .Build();
    }

    public async Task PutAsync(string key, Stream content, long size, string contentType, CancellationToken ct = default)
    {
        await EnsureBucketAsync(ct);
        // S3/MinIO SigV4 multipart signing breaks on Content-Type values that contain commas
        // (e.g. MediaRecorder's "video/webm;codecs=vp9,opus"). Keep only the type/subtype.
        var safeType = SanitizeContentType(contentType);
        await _client.PutObjectAsync(
            new PutObjectArgs()
                .WithBucket(_bucket)
                .WithObject(key)
                .WithStreamData(content)
                .WithObjectSize(size)
                .WithContentType(safeType),
            ct);
    }

    /// <summary>Strip MIME parameters so object storage signing stays valid.</summary>
    internal static string SanitizeContentType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
            return "application/octet-stream";
        var trimmed = contentType.Trim();
        var semi = trimmed.IndexOf(';');
        if (semi >= 0) trimmed = trimmed[..semi].Trim();
        return string.IsNullOrWhiteSpace(trimmed) ? "application/octet-stream" : trimmed;
    }

    public async Task CopyToAsync(string key, Stream destination, CancellationToken ct = default)
    {
        await _client.GetObjectAsync(
            new GetObjectArgs()
                .WithBucket(_bucket)
                .WithObject(key)
                .WithCallbackStream((stream, token) => stream.CopyToAsync(destination, token)),
            ct);
    }

    public Task DeleteAsync(string key, CancellationToken ct = default) =>
        _client.RemoveObjectAsync(new RemoveObjectArgs().WithBucket(_bucket).WithObject(key), ct);

    private async Task EnsureBucketAsync(CancellationToken ct)
    {
        if (_bucketReady) return;
        await _bucketGate.WaitAsync(ct);
        try
        {
            if (_bucketReady) return;
            if (!await _client.BucketExistsAsync(new BucketExistsArgs().WithBucket(_bucket), ct))
                await _client.MakeBucketAsync(new MakeBucketArgs().WithBucket(_bucket), ct);
            _bucketReady = true;
        }
        finally
        {
            _bucketGate.Release();
        }
    }
}
