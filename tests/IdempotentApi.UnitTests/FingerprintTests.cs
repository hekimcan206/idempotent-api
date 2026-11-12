using System.Text;
using Microsoft.AspNetCore.Http;

namespace IdempotentApi.UnitTests;

public sealed class FingerprintTests
{
    private static readonly Sha256RequestFingerprinter Fingerprinter = new();

    private static string Fp(string method = "POST", string path = "/payments", string query = "", string? contentType = "application/json", string body = "{\"a\":1}")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(query);
        context.Request.ContentType = contentType;
        return Fingerprinter.Compute(context.Request, Encoding.UTF8.GetBytes(body));
    }

    [Fact]
    public void Same_request_gives_the_same_fingerprint() => Fp().Should().Be(Fp()).And.HaveLength(64);

    [Theory]
    [InlineData("PATCH", "/payments", "", "{\"a\":1}")]
    [InlineData("POST", "/refunds", "", "{\"a\":1}")]
    [InlineData("POST", "/payments", "?dryRun=true", "{\"a\":1}")]
    [InlineData("POST", "/payments", "", "{\"a\":2}")]
    [InlineData("POST", "/payments", "", "{\"a\": 1}")]
    public void Any_change_to_method_path_query_or_body_changes_it(string method, string path, string query, string body) =>
        Fp(method, path, query, body: body).Should().NotBe(Fp());

    [Theory]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("Application/JSON")]
    [InlineData(" application/json ")]
    public void Media_type_parameters_and_case_do_not(string contentType) =>
        Fp(contentType: contentType).Should().Be(Fp());

    [Fact]
    public void Different_media_type_does() => Fp(contentType: "application/x-www-form-urlencoded").Should().NotBe(Fp());

    [Fact]
    public void Field_boundaries_are_unambiguous() =>
        Fp(path: "/a", query: "?bc").Should().NotBe(Fp(path: "/a?", query: "?c"));
}
