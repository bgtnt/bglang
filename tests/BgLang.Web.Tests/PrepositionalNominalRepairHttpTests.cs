using System.Net;
using System.Text;
using System.Text.Json;
using BgLang.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BgLang.Web.Tests.TextCompositionHttpFixture;

namespace BgLang.Web.Tests;

[TestClass, DoNotParallelize]
public sealed class PrepositionalNominalRepairHttpTests
{
    [TestMethod, TestCategory("FullPackage")]
    [DataRow("1")][DataRow("2")]
    public async Task Prepositional_review_context_order_and_original_refs_round_trip_http(string version)
    {
        await ContextCompositionHttpTests.WithPackage(async (app, session, client) =>
        {
            foreach (string text in new[] { "Спирам пред сипя къща.", "🙂 Спирам пред сипя къща. Спирам пред сипя село." })
            {
                using JsonDocument raw = await Post(text, HttpContract.RawFormat, true);
                JsonElement reviews = raw.RootElement.GetProperty("data").GetProperty("nominalConstructions").GetProperty("reviews");
                Assert.AreEqual(1, reviews.GetArrayLength()); JsonElement review = reviews[0];
                Assert.AreEqual("preposition+NP", review.GetProperty("pattern").GetString());
                Assert.AreEqual(text.IndexOf("сипя", StringComparison.Ordinal), review.GetProperty("start").GetInt32());
                Assert.AreEqual(4, review.GetProperty("length").GetInt32());
                Assert.IsTrue(review.GetProperty("noChangeRetained").GetBoolean());
                Assert.IsFalse(review.GetProperty("allPackageAlternativesExamined").GetBoolean());
                JsonElement alternatives = review.GetProperty("alternatives");
                string[] forms = alternatives.EnumerateArray().Select(a => a.GetProperty("alternative").GetProperty("form").GetString()!).ToArray();
                CollectionAssert.AreEqual(new[] { "сипя", "кипя", "сивя", "силя", "синя", "сип", "сипа", "сипе", "сипея", "сипи" }, forms);
                int[] order = review.GetProperty("contextualOrder").EnumerateArray().Select(x => x.GetInt32()).ToArray();
                CollectionAssert.AreEqual(Enumerable.Range(0, forms.Length).ToArray(), order.Order().ToArray());
                Assert.AreEqual(0, order[0]); Assert.IsTrue(Array.IndexOf(order, 4) < Array.IndexOf(order, 1));
                Assert.AreEqual("Compatible", alternatives[4].GetProperty("phrase").GetProperty("assessment").GetString());
                Assert.IsTrue(review.GetProperty("contextReadings").GetArrayLength() > 0);
                foreach (string? format in new string?[] { null, SharedDonorResponse.Format, SharedDonorResponse.AnalysisFormat })
                {
                    using JsonDocument encoded = await Post(text, format, true);
                    using JsonDocument restored = Restore(encoded);
                    Assert.IsTrue(SharedDonorResponseTests.SameJson(raw.RootElement, restored.RootElement),
                        "Context order and source references must survive every HTTP representation without replacing the raw alternatives.");
                }
                foreach (string? format in new string?[] { HttpContract.RawFormat, null, SharedDonorResponse.Format, SharedDonorResponse.AnalysisFormat })
                {
                    using JsonDocument disabled = await Post(text, format, false);
                    using JsonDocument restored = Restore(disabled);
                    Assert.IsFalse(restored.RootElement.GetProperty("data").TryGetProperty("nominalConstructions", out _));
                }
            }
            async Task<JsonDocument> Post(string text, string? format, bool enabled)
            {
                using var body = new StringContent(JsonSerializer.Serialize(new
                {
                    text, evidenceFormat = format, context = new { includeNominalConstructions = enabled }
                }), Encoding.UTF8, "application/json");
                using HttpResponseMessage response = await client.PostAsync("/api/v" + version + "/analyze-text", body);
                string json = await response.Content.ReadAsStringAsync();
                Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, json);
                return JsonDocument.Parse(json);
            }
        });
    }
}
