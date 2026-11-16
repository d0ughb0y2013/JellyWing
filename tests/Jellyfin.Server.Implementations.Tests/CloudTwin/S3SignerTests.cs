using System;
using System.Linq;
using System.Net.Http;
using Emby.Server.Implementations.CloudTwin;
using Xunit;

namespace Jellyfin.Server.Implementations.Tests.CloudTwin;

public class S3SignerTests
{
    // The "GET Object" example from Amazon's Signature Version 4 documentation for S3.
    [Fact]
    public void Sign_AmazonsPublishedExample_ProducesItsSignature()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://examplebucket.s3.amazonaws.com/test.txt");
        request.Headers.TryAddWithoutValidation("Range", "bytes=0-9");

        S3Signer.Sign(
            request,
            S3Signer.EmptyPayloadHash,
            "us-east-1",
            "AKIAIOSFODNN7EXAMPLE",
            "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY",
            new DateTime(2013, 5, 24, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", S3Signer.EmptyPayloadHash);
        Assert.Equal("20130524T000000Z", request.Headers.GetValues("x-amz-date").Single());
        Assert.Equal(
            "AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request,"
            + "SignedHeaders=host;range;x-amz-content-sha256;x-amz-date,"
            + "Signature=f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41",
            request.Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public void Sign_DifferentBody_ProducesADifferentSignature()
    {
        string SignWith(string payloadHash)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, "https://store.example/bucket/catalog/a.json");
            S3Signer.Sign(request, payloadHash, "auto", "key", "secret", new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            return request.Headers.GetValues("Authorization").Single();
        }

        Assert.NotEqual(SignWith(S3Signer.EmptyPayloadHash), SignWith(new string('a', 64)));
    }

    [Theory]
    [InlineData("media/abc.mp4", "media/abc.mp4")]
    [InlineData("images/a b+c.jpg", "images/a%20b%2Bc.jpg")]
    public void EncodeKey_EscapesEachPartAndKeepsTheSlashes(string key, string expected)
    {
        Assert.Equal(expected, S3Signer.EncodeKey(key));
    }
}
