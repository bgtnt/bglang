using System.Net;
using System.Text;
using System.Text.Json;
using BgLang.Contracts;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using static BgLang.Web.Tests.TextCompositionHttpFixture;

namespace BgLang.Web.Tests;

[TestClass, DoNotParallelize]
public sealed class NominalConstructionHttpTests
{
    [TestMethod, TestCategory("FullPackage")]
    [DataRow("1")][DataRow("2")]
    public async Task Opt_in_nominal_groups_and_known_word_reviews_round_trip_all_http_formats(string version)
    {
        await ContextCompositionHttpTests.WithPackage(async (app, session, client) =>
        {
            string[] texts = ["Стара, изоставена и порутена къща пустее.", "Рисувам сипя река.",
                "🙂 Рисувам сипя река. Рисувам сипя река."];
            foreach (string text in texts)
            {
                session.AnalyzeText(text, false, new() { IncludeNominalConstructions = true }, default);
                using JsonDocument raw = await Post(text, HttpContract.RawFormat, true);
                JsonElement evidence = raw.RootElement.GetProperty("data").GetProperty("nominalConstructions");
                Assert.AreEqual("nominal_constructions.v1", evidence.GetProperty("version").GetString());
                Assert.IsTrue(evidence.GetProperty("available").GetBoolean());
                if (text.StartsWith("Стара", StringComparison.Ordinal))
                {
                    JsonElement phrase = evidence.GetProperty("phrases")[0];
                    Assert.AreEqual("NP", phrase.GetProperty("constructionKey").GetString());
                    Assert.AreEqual("Compatible", phrase.GetProperty("assessment").GetString());
                    Assert.AreEqual(3, phrase.GetProperty("modifierTokenIndices").GetArrayLength());
                    Assert.AreEqual(2, phrase.GetProperty("connectorTokenIndices").GetArrayLength());
                }
                else
                {
                    JsonElement reviews = evidence.GetProperty("reviews");
                    Assert.AreEqual(text.StartsWith("🙂", StringComparison.Ordinal) ? 2 : 1, reviews.GetArrayLength());
                    JsonElement review = reviews[reviews.GetArrayLength() - 1];
                    Assert.IsTrue(review.GetProperty("noChangeRetained").GetBoolean());
                    Assert.IsFalse(review.GetProperty("allPackageAlternativesExamined").GetBoolean());
                    JsonElement blue = review.GetProperty("alternatives")[4];
                    Assert.AreEqual("синя", blue.GetProperty("alternative").GetProperty("form").GetString());
                    JsonElement member = blue.GetProperty("phrase").GetProperty("headSupports")[0].GetProperty("members")[0];
                    Assert.AreEqual(0, member.GetProperty("readings").GetArrayLength());
                    Assert.AreEqual(0, member.GetProperty("replacementReadings")[0].GetInt32());
                }
                foreach (string? format in new string?[] { null, SharedDonorResponse.Format, SharedDonorResponse.AnalysisFormat })
                {
                    using JsonDocument encoded = await Post(text, format, true);
                    using JsonDocument restored = Restore(encoded);
                    Assert.IsTrue(SharedDonorResponseTests.SameJson(raw.RootElement, restored.RootElement),
                        "All representations must preserve original inventories, candidate-owned references and occurrence ranges.");
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

    [TestMethod]
    public void Nominal_construction_option_is_strict_boolean_and_other_routes_reject_context()
    {
        foreach (string value in new[] { "null", "1", "\"true\"", "[]", "{}" })
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<WebInput>(
                "{\"text\":\"къща\",\"context\":{\"includeNominalConstructions\":" + value + "}}", HttpContract.Json));
        Assert.AreEqual("option-not-applicable", Assert.Throws<ApiRequestException>(() => ApiOperations.Validate("lookup",
            new WebInput("къща", Context: new TextContextOptions { IncludeNominalConstructions = true }))).Code);
    }
}
