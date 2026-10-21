using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BgLang.Web.Tests;

[TestClass, DoNotParallelize]
public sealed class HttpBoundaryTests
{
    [TestMethod]
    public void Rate_limit_is_per_peer_ip_and_recovers_after_window()
    {
        var time = new ManualClock();
        var rate = new ClientRateLimit(time);
        for (int request = 0; request < 30; request++) Assert.AreEqual(200, rate.TryAcquire("peer-1").Status);
        Assert.AreEqual((429, 60), rate.TryAcquire("peer-1"));
        Assert.AreEqual(200, rate.TryAcquire("peer-2").Status);
        time.Now = time.Now.AddSeconds(61);
        Assert.AreEqual(200, rate.TryAcquire("peer-1").Status);
    }

    [TestMethod]
    public void Rate_registry_has_a_hard_capacity_and_reclaims_only_expired_windows()
    {
        var time = new ManualClock();
        var rate = new ClientRateLimit(time);
        for (int client = 0; client < HttpContract.ClientCapacity; client++)
            Assert.AreEqual(200, rate.TryAcquire("peer-" + client).Status);
        Assert.AreEqual(503, rate.TryAcquire("overflow").Status);
        Assert.AreEqual(200, rate.TryAcquire("peer-0").Status, "An existing client is still counted.");
        time.Now = time.Now.AddMinutes(2);
        Assert.AreEqual(200, rate.TryAcquire("overflow").Status);
    }

    [TestMethod]
    public void Utf16_lengths_are_independent_of_utf8_bytes_and_scalar_operations_reject_limits()
    {
        string surrogatePairs = string.Concat(Enumerable.Repeat(char.ConvertFromUtf32(0x1F600), 128));
        Assert.AreEqual(256, surrogatePairs.Length);
        Assert.IsTrue(System.Text.Encoding.UTF8.GetByteCount(surrogatePairs) > 256);
        ApiOperations.Validate("analyze-word", new(surrogatePairs));
        Assert.Throws<ApiRequestException>(() => ApiOperations.Validate("analyze-word", new(surrogatePairs + "a")));
        ApiOperations.Validate("analyze-text", new("ред 1\nред 2\tкрай"));
        Assert.Throws<ApiRequestException>(() => ApiOperations.Validate("analyze-word", new("дума\n")));
        Assert.Throws<ApiRequestException>(() => ApiOperations.Validate("decompose-word", new("книга", 32)));
        Assert.Throws<ApiRequestException>(() => ApiOperations.Validate("analyze-text", new("книга", 32)));
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(32)]
    [DataRow(100)]
    [DataRow(256)]
    public void Collection_requests_allow_the_public_bound_above_the_display_default(int limit)
    {
        foreach (string operation in new[] { "lookup", "analyze-word", "generate-forms", "derive-words" })
            ApiOperations.Validate(operation, new("книга", limit));
    }

    [TestMethod]
    public void Text_query_contract_keeps_separate_utf16_bounds_and_rejects_unsupported_fields()
    {
        ApiOperations.Validate("find-forms-in-text", new("ред 1\r\nред 2\tкрай 📚", Query: "книга"));
        ApiOperations.Validate("find-forms-in-text", new(new string('к', 4096), Query: new string('к', 256)));
        Assert.AreEqual("text-too-long", Assert.Throws<ApiRequestException>(() =>
            ApiOperations.Validate("find-forms-in-text", new(new string('к', 4097), Query: "книга"))).Code);
        foreach (string? query in new string?[] { null, "", " ", "две думи", "книга\n", "\0", new string('к', 257) })
            Assert.AreEqual("invalid-query", Assert.Throws<ApiRequestException>(() =>
                ApiOperations.Validate("find-forms-in-text", new("книгите", Query: query))).Code);
        Assert.AreEqual("limit-not-applicable", Assert.Throws<ApiRequestException>(() =>
            ApiOperations.Validate("find-forms-in-text", new("книгите", 32, "книга"))).Code);
        Assert.AreEqual("query-not-applicable", Assert.Throws<ApiRequestException>(() =>
            ApiOperations.Validate("analyze-word", new("книгите", Query: "книга"))).Code);
    }

}

internal sealed class ManualClock : TimeProvider
{
    internal DateTimeOffset Now { get; set; } = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}
