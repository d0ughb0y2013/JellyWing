using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace Emby.Server.Implementations.CloudTwin;

/// <summary>
/// Signs requests the way S3 and S3-compatible stores expect (AWS Signature Version 4).
/// </summary>
public static class S3Signer
{
    private const string Algorithm = "AWS4-HMAC-SHA256";
    private const string Service = "s3";

    /// <summary>
    /// Gets the hash that stands for an empty request body.
    /// </summary>
    public static string EmptyPayloadHash { get; } = Convert.ToHexStringLower(SHA256.HashData([]));

    /// <summary>
    /// Adds the date, content hash and authorization headers that sign a request.
    /// The request must have no query string. The host header, every <c>x-amz-</c> header
    /// and a range header, if present, are covered by the signature.
    /// </summary>
    /// <param name="request">The request, with its final URL.</param>
    /// <param name="payloadHash">The lowercase hex SHA-256 of the body.</param>
    /// <param name="region">The region, or <c>auto</c> for stores without regions.</param>
    /// <param name="accessKeyId">The access key id.</param>
    /// <param name="secretAccessKey">The secret access key.</param>
    /// <param name="now">The current time in UTC.</param>
    public static void Sign(HttpRequestMessage request, string payloadHash, string region, string accessKeyId, string secretAccessKey, DateTime now)
    {
        ArgumentNullException.ThrowIfNull(request);
        var uri = request.RequestUri ?? throw new ArgumentException("The request has no URL.", nameof(request));

        var timestamp = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var date = timestamp[..8];
        request.Headers.Remove("x-amz-date");
        request.Headers.Remove("x-amz-content-sha256");
        request.Headers.TryAddWithoutValidation("x-amz-date", timestamp);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);

        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = uri.IsDefaultPort ? uri.Host : uri.Host + ":" + uri.Port.ToString(CultureInfo.InvariantCulture)
        };
        foreach (var header in request.Headers)
        {
            var name = header.Key.ToLowerInvariant();
            if (name.StartsWith("x-amz-", StringComparison.Ordinal) || name == "range")
            {
                headers[name] = string.Join(',', header.Value.Select(v => v.Trim()));
            }
        }

        var signedHeaders = string.Join(';', headers.Keys);
        var canonicalRequest = string.Join(
            '\n',
            request.Method.Method,
            uri.AbsolutePath,
            string.Empty,
            string.Concat(headers.Select(h => h.Key + ":" + h.Value + "\n")),
            signedHeaders,
            payloadHash);

        var scope = $"{date}/{region}/{Service}/aws4_request";
        var stringToSign = string.Join(
            '\n',
            Algorithm,
            timestamp,
            scope,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalRequest))));

        var key = Hmac(Encoding.UTF8.GetBytes("AWS4" + secretAccessKey), date);
        key = Hmac(key, region);
        key = Hmac(key, Service);
        key = Hmac(key, "aws4_request");
        var signature = Convert.ToHexStringLower(Hmac(key, stringToSign));

        request.Headers.Remove("Authorization");
        request.Headers.TryAddWithoutValidation(
            "Authorization",
            $"{Algorithm} Credential={accessKeyId}/{scope},SignedHeaders={signedHeaders},Signature={signature}");
    }

    /// <summary>
    /// Encodes a key for use as a URL path, keeping the slashes between its parts.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <returns>The encoded path, without a leading slash.</returns>
    public static string EncodeKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return string.Join('/', key.Split('/').Select(Uri.EscapeDataString));
    }

    private static byte[] Hmac(byte[] key, string data) => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(data));
}
