using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace Emby.Server.Implementations.CloudTwin;

/// <summary>
/// Publishes to an S3-compatible store, addressing the bucket by path.
/// </summary>
public class S3ObjectStore : IObjectStore
{
    // The most a single upload may hold; larger files need a multipart upload.
    private const long MaxSingleUpload = 5L * 1000 * 1000 * 1000;

    private readonly HttpClient _httpClient;
    private readonly TwinOptions _options;

    /// <summary>
    /// Initializes a new instance of the <see cref="S3ObjectStore"/> class.
    /// </summary>
    /// <param name="httpClient">The HTTP client.</param>
    /// <param name="options">The cloud twin options, with the S3 settings filled in.</param>
    public S3ObjectStore(HttpClient httpClient, TwinOptions options)
    {
        _httpClient = httpClient;
        _options = options;
    }

    /// <inheritdoc />
    public async Task PutFileAsync(string key, string filePath, string contentType, CancellationToken cancellationToken)
    {
        var length = new FileInfo(filePath).Length;
        if (length > MaxSingleUpload)
        {
            throw new NotSupportedException($"{filePath} is larger than a single upload can hold.");
        }

        // The body is hashed first so the signature covers it; every S3-compatible store accepts that.
        string hash;
        var hashing = File.OpenRead(filePath);
        await using (hashing.ConfigureAwait(false))
        {
            hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(hashing, cancellationToken).ConfigureAwait(false));
        }

        var body = File.OpenRead(filePath);
        await using (body.ConfigureAwait(false))
        {
            using var content = new StreamContent(body);
            content.Headers.ContentLength = length;
            await SendAsync(HttpMethod.Put, key, content, contentType, hash, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async Task PutBytesAsync(string key, ReadOnlyMemory<byte> content, string contentType, CancellationToken cancellationToken)
    {
        using var body = new ReadOnlyMemoryContent(content);
        var hash = Convert.ToHexStringLower(SHA256.HashData(content.Span));
        await SendAsync(HttpMethod.Put, key, body, contentType, hash, cancellationToken).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task DeleteAsync(string key, CancellationToken cancellationToken)
        => SendAsync(HttpMethod.Delete, key, null, null, S3Signer.EmptyPayloadHash, cancellationToken);

    private async Task SendAsync(HttpMethod method, string key, HttpContent? content, string? contentType, string payloadHash, CancellationToken cancellationToken)
    {
        var url = $"{_options.S3Endpoint!.TrimEnd('/')}/{_options.S3Bucket}/{S3Signer.EncodeKey(key)}";
        using var request = new HttpRequestMessage(method, url) { Content = content };
        if (content is not null && contentType is not null)
        {
            content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        }

        // Temporary credentials come with a token, which travels in a signed header.
        if (!string.IsNullOrEmpty(_options.S3SessionToken))
        {
            request.Headers.TryAddWithoutValidation("x-amz-security-token", _options.S3SessionToken);
        }

        S3Signer.Sign(request, payloadHash, _options.S3Region, _options.S3AccessKeyId!, _options.S3SecretAccessKey!, DateTime.UtcNow);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.IsSuccessStatusCode || (method == HttpMethod.Delete && response.StatusCode == HttpStatusCode.NotFound))
        {
            return;
        }

        var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        throw new HttpRequestException(
            $"The store answered {(int)response.StatusCode} to {method} {key}: {(detail.Length > 300 ? detail[..300] : detail)}");
    }
}
