using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using BgLang.Api;
using BgLang.Contracts;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BgLang.Web.Tests;

[TestClass, DoNotParallelize]
public sealed class WebAdapterHttpTests
{
    [TestMethod]
    public async Task Factory_uses_default_and_configured_rate_without_opening_a_session()
    {
        int opens = 0;
        BulgarianLanguageSession NeverOpen() { opens++; throw new InvalidOperationException("Session must not open."); }
        await using (var app = WebHostFactory.Create(HostArguments(), NeverOpen))
            Assert.AreEqual(30, app.Services.GetRequiredService<ClientRateLimit>().RequestsPerMinute);
        await using (var app = WebHostFactory.Create(HostArguments("7"), NeverOpen))
            Assert.AreEqual(7, app.Services.GetRequiredService<ClientRateLimit>().RequestsPerMinute);
        Assert.AreEqual(0, opens);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public void Factory_and_rate_reject_nonpositive_configuration(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClientRateLimit(requestsPerMinute: value));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            WebHostFactory.Create(HostArguments(value.ToString(CultureInfo.InvariantCulture))));
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("not-an-integer")]
    [DataRow("1.5")]
    [DataRow("2147483648")]
    public void Factory_rejects_noninteger_configuration(string value)
    {
        Assert.Throws<InvalidOperationException>(() => WebHostFactory.Create(HostArguments(value)));
    }

    [TestMethod]
    public void Configured_rate_keeps_peer_windows_and_rounded_retry_after()
    {
        var time = new ManualClock();
        Assert.AreEqual(30, new ClientRateLimit(time).RequestsPerMinute);
        var rate = new ClientRateLimit(time, 2);
        Assert.AreEqual(2, rate.RequestsPerMinute);
        Assert.AreEqual((200, 0), rate.TryAcquire("peer-a"));
        Assert.AreEqual((200, 0), rate.TryAcquire("peer-a"));
        Assert.AreEqual((429, 60), rate.TryAcquire("peer-a"));
        Assert.AreEqual((200, 0), rate.TryAcquire("peer-b"));
        time.Now = time.Now.AddMilliseconds(59500);
        Assert.AreEqual((429, 1), rate.TryAcquire("peer-a"));
        Assert.AreEqual((200, 0), rate.TryAcquire("peer-b"));
        Assert.AreEqual((429, 1), rate.TryAcquire("peer-b"));
        time.Now = time.Now.AddMilliseconds(500);
        Assert.AreEqual((200, 0), rate.TryAcquire("peer-a"));
        Assert.AreEqual((200, 0), rate.TryAcquire("peer-b"));
    }

    [TestMethod, TestCategory("FullPackage")]
    public async Task Complete_package_prefix_routes_effective_metadata_and_rate_boundary_are_explicit()
    {
        string manifest = Environment.GetEnvironmentVariable("BGLANG_WEB_TEST_MANIFEST") ?? "";
        string hash = Environment.GetEnvironmentVariable("BGLANG_WEB_TEST_MANIFEST_SHA256") ?? "";
        Assert.IsTrue(File.Exists(manifest), "A strictly identified complete package is mandatory; no package-free acceptance.");
        Assert.IsTrue(hash.Length == 64 && hash.All(Uri.IsHexDigit), "Select the exact manifest SHA-256 independently.");
        var time = new ManualClock();
        BulgarianLanguageSession? session = null;
        int opens = 0;
        var app = WebHostFactory.Create(["--urls", "http://127.0.0.1:0", "--BgLang:ManifestPath", manifest,
            "--BgLang:ManifestSha256", hash, "--BgLang:RequestsPerMinute", "2"],
            () => { opens++; return session = BulgarianLanguageSession.Open(manifest); }, time);
        try
        {
            await app.StartAsync();
            string address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(20) };
            foreach (string version in new[] { "1", "2" })
            {
                using JsonDocument metadata = JsonDocument.Parse(await client.GetStringAsync($"/api/v{version}/"));
                JsonElement limits = metadata.RootElement.GetProperty("limits");
                Assert.AreEqual(2, limits.GetProperty("requestsPerMinutePerIp").GetInt32());
                Assert.AreEqual(1, limits.GetProperty("concurrency").GetInt32());
                Assert.AreEqual(4, limits.GetProperty("queueCapacity").GetInt32());
                Assert.AreEqual(4096, limits.GetProperty("textUtf16").GetInt32());
                Assert.AreEqual(10000d, limits.GetProperty("responseTimeoutMilliseconds").GetDouble());
                Assert.AreEqual(1024, limits.GetProperty("retainedIpMaximum").GetInt32());
            }

            const string prefix = "книгат";
            IReadOnlyList<WordReading> expectedPrefix = session!.Lookup(prefix, LookupMode.Prefix, 32);
            Assert.IsTrue(expectedPrefix.Any(row => row.Form == "книгата"),
                "The literal prefix witness must return an actual longer package form.");
            CollectionAssert.AreEqual(expectedPrefix.ToArray(), session.Lookup(prefix, LookupMode.Prefix, 32).ToArray(),
                "Repeated ordinary prefix lookup keeps its deterministic returned order, not a probability score.");
            IReadOnlyList<WordReading> expectedExact = session.Lookup("книга", LookupMode.Exact, 32);
            Assert.IsTrue(expectedExact.Count > 0);
            Assert.IsTrue(expectedExact.All(row => row.Form == "книга"));
            Assert.IsFalse(expectedExact.Any(row => row.Form == "книгата"), "lookup must remain exact.");

            foreach (string version in new[] { "1", "2" })
            {
                foreach (int limit in new[] { 1, 32 })
                {
                    time.Now = time.Now.AddMinutes(1);
                    using HttpResponseMessage response = await client.PostAsJsonAsync($"/api/v{version}/lookup-prefix",
                        new WebInput(prefix, limit), HttpContract.Json);
                    Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
                    using JsonDocument actual = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                    IReadOnlyList<WordReading> expected = limit == 32 ? expectedPrefix : session.Lookup(prefix, LookupMode.Prefix, limit);
                    JsonElement rows = actual.RootElement.GetProperty("data"), limits = actual.RootElement.GetProperty("limits");
                    Assert.AreEqual(JsonSerializer.Serialize(expected, HttpContract.Json), rows.GetRawText());
                    Assert.IsTrue(rows.GetArrayLength() > 0 && rows.GetArrayLength() <= limit,
                        "The public limit bounds returned readings, not a new grouped-suggestion representation.");
                    Assert.AreEqual(limit, limits.GetProperty("requested").GetInt32());
                    Assert.AreEqual(limit, limits.GetProperty("effective").GetInt32());
                    Assert.AreEqual(256, limits.GetProperty("hostMaximum").GetInt32());
                    Assert.AreEqual("per-result-collection", limits.GetProperty("scope").GetString());
                    JsonElement coverage = actual.RootElement.GetProperty("coverage");
                    Assert.IsFalse(coverage.GetProperty("hostTruncated").GetBoolean());
                    Assert.AreEqual(rows.GetArrayLength() >= limit, coverage.GetProperty("possibleTruncation").GetBoolean());
                    Assert.AreEqual(JsonValueKind.Null, coverage.GetProperty("complete").ValueKind);
                }
                time.Now = time.Now.AddMinutes(1);
                using HttpResponseMessage exact = await client.PostAsJsonAsync($"/api/v{version}/lookup",
                    new WebInput("книга", 32), HttpContract.Json);
                Assert.AreEqual(HttpStatusCode.OK, exact.StatusCode);
                using JsonDocument exactJson = JsonDocument.Parse(await exact.Content.ReadAsStringAsync());
                Assert.AreEqual(JsonSerializer.Serialize(expectedExact, HttpContract.Json), exactJson.RootElement.GetProperty("data").GetRawText());
            }

            foreach ((string body, string code) in new[]
            {
                ("{\"text\":\"книгат\",\"mode\":\"Prefix\"}", "invalid-json"),
                ("{\"text\":\"книгат\",\"query\":\"книга\"}", "query-not-applicable"),
                ("{\"text\":\"книгат\",\"context\":{}}", "option-not-applicable"),
                ("{\"text\":\"книгат\",\"includeUnknownContextProperNames\":false}", "option-not-applicable"),
                ("{\"text\":\"книгат\",\"stressSelection\":{\"tokenIndex\":0,\"entryId\":\"owner\",\"form\":\"книгат\",\"fullTag\":null}}", "option-not-applicable"),
                ("{\"text\":\"книгат\",\"prototypeEntryId\":\"owner\"}", "option-not-applicable"),
                ("{\"text\":\"книгат\",\"evidenceFormat\":\"not-a-format\"}", "invalid-evidence-format"),
                ("{\"text\":\"книгат\",\"limit\":0}", "invalid-limit"),
                ("{\"text\":\"книгат\",\"limit\":257}", "invalid-limit"),
                (JsonSerializer.Serialize(new WebInput(new string('к', 257))), "text-too-long")
            })
            {
                time.Now = time.Now.AddMinutes(1);
                using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v2/lookup-prefix")
                    { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                using HttpResponseMessage response = await client.SendAsync(request);
                Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
                using JsonDocument error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                Assert.AreEqual(code, error.RootElement.GetProperty("code").GetString());
            }

            time.Now = time.Now.AddMinutes(1);
            for (int request = 0; request < 2; request++)
            {
                using HttpResponseMessage accepted = await client.PostAsJsonAsync("/api/v2/lookup-prefix", new WebInput(prefix, 1));
                Assert.AreEqual(HttpStatusCode.OK, accepted.StatusCode);
            }
            using var forged = new HttpRequestMessage(HttpMethod.Post, "/api/v2/lookup-prefix")
                { Content = JsonContent.Create(new WebInput(prefix, 1)) };
            forged.Headers.Add("X-Forwarded-For", "203.0.113.123");
            using HttpResponseMessage rejected = await client.SendAsync(forged);
            Assert.AreEqual(HttpStatusCode.TooManyRequests, rejected.StatusCode);
            Assert.AreEqual(TimeSpan.FromSeconds(60), rejected.Headers.RetryAfter?.Delta);
            Assert.AreEqual(1, opens, "No endpoint exemption or additional language session.");
        }
        finally { try { await app.StopAsync(); } finally { await app.DisposeAsync(); } }
        Assert.Throws<ObjectDisposedException>(() => _ = session!.Provenance);
    }

    private static string[] HostArguments(string? requestsPerMinute = null)
    {
        string[] arguments = ["--urls", "http://127.0.0.1:0", "--BgLang:ManifestPath", "not-opened.json",
            "--BgLang:ManifestSha256", new string('a', 64)];
        return requestsPerMinute is null ? arguments : [.. arguments, "--BgLang:RequestsPerMinute=" + requestsPerMinute];
    }
}
