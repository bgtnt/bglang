using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BgLang.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace BgLang.Web.Tests;

[TestClass, DoNotParallelize]
public sealed class SyntaxHubHttpTests
{
    [TestMethod]
    [DataRow(null, true)]
    [DataRow("https://example.test", true)]
    [DataRow("https://EXAMPLE.test:443", true)]
    [DataRow("http://example.test", false)]
    [DataRow("https://example.test:8443", false)]
    [DataRow("https://foreign.test", false)]
    [DataRow("https://user@example.test", false)]
    [DataRow("https://example.test/path", false)]
    [DataRow("null", false)]
    public void Browser_origin_uses_actual_scheme_host_and_port(string? origin, bool allowed)
    {
        var context = new DefaultHttpContext();
        context.Request.Scheme = "https";
        context.Request.Host = new("example.test");
        if (origin is not null) context.Request.Headers.Origin = origin;
        context.Request.Headers["X-Forwarded-Host"] = "foreign.test";
        context.Request.Headers["X-Forwarded-Proto"] = "http";
        Assert.AreEqual(allowed, SyntaxHub.HasSameOrigin(context.Request));
        context.Request.Headers.Origin = new(["https://example.test", "https://example.test"]);
        Assert.IsFalse(SyntaxHub.HasSameOrigin(context.Request));
    }

    [TestMethod, TestCategory("FullPackage")]
    public async Task SignalR_tree_uses_shared_worker_actual_websocket_and_separate_http_rate_admission()
    {
        await ContextCompositionHttpTests.WithPackage(async (app, session, client) =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            using (var foreign = new HttpRequestMessage(HttpMethod.Post, "/hubs/syntax/negotiate?negotiateVersion=1"))
            {
                foreign.Headers.Add("Origin", "https://foreign.test");
                using HttpResponseMessage rejected = await client.SendAsync(foreign, deadline.Token);
                Assert.AreEqual(HttpStatusCode.Forbidden, rejected.StatusCode);
            }
            using (HttpResponseMessage negotiate = await client.PostAsync("/hubs/syntax/negotiate?negotiateVersion=1", null, deadline.Token))
            {
                Assert.AreEqual(HttpStatusCode.OK, negotiate.StatusCode);
                using JsonDocument transports = JsonDocument.Parse(await negotiate.Content.ReadAsStringAsync(deadline.Token));
                JsonElement available = transports.RootElement.GetProperty("availableTransports");
                Assert.AreEqual(1, available.GetArrayLength());
                Assert.AreEqual("WebSockets", available[0].GetProperty("transport").GetString());
            }
            using (var noRedirect = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { BaseAddress = client.BaseAddress })
                foreach (string alias in new[] { "/demo-tree", "/demo-tree/" })
                {
                    using HttpResponseMessage redirect = await noRedirect.GetAsync(alias, deadline.Token);
                    Assert.AreEqual(HttpStatusCode.Redirect, redirect.StatusCode);
                    Assert.AreEqual("/demo-tree/index.html", redirect.Headers.Location?.OriginalString);
                }

            // Exhaust only ordinary HTTP admission; interactive edits must remain usable afterward.
            for (int request = 0; request < HttpContract.RequestsPerMinute; request++)
            {
                using HttpResponseMessage invalid = await client.PostAsJsonAsync("/api/v2/analyze-text", new { text = "" }, deadline.Token);
                Assert.AreEqual(HttpStatusCode.BadRequest, invalid.StatusCode);
            }
            using (HttpResponseMessage limited = await client.PostAsJsonAsync("/api/v2/analyze-text", new { text = "книга" }, deadline.Token))
                Assert.AreEqual(HttpStatusCode.TooManyRequests, limited.StatusCode);

            using var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
            var uri = new UriBuilder(new Uri(client.BaseAddress, "/hubs/syntax")) { Scheme = "ws" };
            await socket.ConnectAsync(uri.Uri, deadline.Token);
            await Send(socket, "{\"protocol\":\"json\",\"version\":1}", deadline.Token);
            Assert.AreEqual("{}", await Read(socket, deadline.Token));
            await Send(socket, JsonSerializer.Serialize(new { type = 1, invocationId = "invalid", target = "Analyze", arguments = new object[] { 1, new string('к', HttpContract.TextLength + 1) } }), deadline.Token);
            using (JsonDocument invalid = await Completion(socket, "invalid", deadline.Token))
                StringAssert.Contains(invalid.RootElement.GetProperty("error").GetString()!, "text-too-long");

            SessionHost host = app.Services.GetRequiredService<SessionHost>();
            var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task held = host.ExecuteResponseAsync("lookup", new("книга"), async (_, cancellation) =>
            { entered.TrySetResult(true); await release.Task.WaitAsync(cancellation); }, deadline.Token);
            await entered.Task.WaitAsync(deadline.Token);
            const string text = "🙂 Малкото дете чете книга.";
            long started = Stopwatch.GetTimestamp();
            Task<JsonDocument>? response = null;
            try
            {
                await Send(socket, JsonSerializer.Serialize(new { type = 1, invocationId = "tree", target = "Analyze", arguments = new object[] { 42, text } }), deadline.Token);
                response = Completion(socket, "tree", deadline.Token);
                await Task.Delay(350, deadline.Token);
                Assert.IsFalse(response.IsCompleted, "A hub analysis cannot bypass the worker held by an HTTP response.");
            }
            finally { release.TrySetResult(true); await held; }
            using JsonDocument completed = await response!;
            double elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            JsonElement result = completed.RootElement.GetProperty("result");
            Assert.AreEqual(42, result.GetProperty("revision").GetInt64());
            double analysisMs = result.GetProperty("analysisMs").GetDouble();
            Assert.IsTrue(analysisMs >= 0 && analysisMs < elapsed - 200,
                "The reported timer excludes the deliberately held queue interval.");
            var options = new TextContextOptions
            {
                IncludeAdditionalRoleHeuristics = true, IncludeSubordinateCommaDiagnostics = true,
                IncludeFragmentDiagnostics = true, IncludeSpacingDiagnostics = true,
                IncludeWordConstructions = true, IncludeNominalConstructions = true, IncludeGrammarEvidence = true
            };
            object expected = SyntaxTreeProjection.Build(session.AnalyzeText(text, true, options, deadline.Token));
            Assert.IsTrue(SharedDonorResponseTests.SameJson(JsonSerializer.SerializeToElement(expected, HttpContract.Json),
                result.GetProperty("tree")), "The wire tree is the existing public analyzer projection with the Lab opt-ins.");
            const string warningText = "Аз пея пътувам.";
            await Send(socket, JsonSerializer.Serialize(new { type = 1, invocationId = "warning", target = "Analyze", arguments = new object[] { 43, warningText } }), deadline.Token);
            using JsonDocument warning = await Completion(socket, "warning", deadline.Token);
            TextAnalysis warningExpected = session.AnalyzeText(warningText, true, options, deadline.Token);
            Assert.IsTrue(warningExpected.Diagnostics.Any(diagnostic => diagnostic.Code == "unseparated_finite_predications"),
                "The independent finite gate witness must deliver a real warning.");
            CollectionAssert.AreEqual(warningExpected.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Code}|{diagnostic.RuleId}|{diagnostic.Start}|{diagnostic.Length}").ToArray(),
                warning.RootElement.GetProperty("result").GetProperty("tree").GetProperty("diagnostics").EnumerateArray()
                    .Select(diagnostic => $"{diagnostic.GetProperty("code").GetString()}|{diagnostic.GetProperty("ruleId").GetString()}|{diagnostic.GetProperty("start").GetInt32()}|{diagnostic.GetProperty("length").GetInt32()}").ToArray(),
                "Hub warnings preserve the direct Lab analyzer's diagnostic codes, rules, order and original UTF-16 spans.");
            const string governed = "С един много добър човек тя разговаряше.";
            await Send(socket, JsonSerializer.Serialize(new { type = 1, invocationId = "subject", target = "Analyze", arguments = new object[] { 44, governed } }), deadline.Token);
            using JsonDocument answer = await Completion(socket, "subject", deadline.Token);
            JsonElement tree = answer.RootElement.GetProperty("result").GetProperty("tree");
            JsonElement subject = tree.GetProperty("tokens").EnumerateArray().Single(token =>
                token.GetProperty("roles").EnumerateArray().Any(role => role.GetProperty("ruleId").GetString() == "BasicSubject"));
            Assert.AreEqual(("тя", 25, 2), (subject.GetProperty("text").GetString(),
                subject.GetProperty("start").GetInt32(), subject.GetProperty("length").GetInt32()));
            JsonElement role = subject.GetProperty("roles").EnumerateArray().Single(r => r.GetProperty("role").GetString() == "Subject");
            Assert.AreEqual("candidate", role.GetProperty("status").GetString());
            int predicate = role.GetProperty("contextTokenIndices").EnumerateArray().Single().GetInt32();
            Assert.AreEqual("разговаряше", tree.GetProperty("tokens")[predicate].GetProperty("text").GetString());
            Assert.IsFalse(tree.GetProperty("edges").EnumerateArray().Any(e => e.GetProperty("label").GetString() == "selected_subject"));
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", deadline.Token);
        });
    }

    [TestMethod, TestCategory("FullPackage")]
    public async Task SignalR_governed_nominal_roles_and_future_agreement_reach_the_wire()
    {
        await ContextCompositionHttpTests.WithPackage(async (_, _, client) =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            using var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
            var uri = new UriBuilder(new Uri(client.BaseAddress, "/hubs/syntax")) { Scheme = "ws" };
            await socket.ConnectAsync(uri.Uri, deadline.Token);
            await Send(socket, "{\"protocol\":\"json\",\"version\":1}", deadline.Token);
            Assert.AreEqual("{}", await Read(socket, deadline.Token));

            int revision = 100;
            foreach (string text in new[]
            {
                "С един много добър човек разговаряше тя.",
                "Разговаряше тя с един много добър човек."
            })
            {
                string invocation = (++revision).ToString(System.Globalization.CultureInfo.InvariantCulture);
                await Send(socket, JsonSerializer.Serialize(new { type = 1, invocationId = invocation, target = "Analyze", arguments = new object[] { revision, text } }), deadline.Token);
                using JsonDocument answer = await Completion(socket, invocation, deadline.Token);
                JsonElement result = answer.RootElement.GetProperty("result");
                Assert.AreEqual(revision, result.GetProperty("revision").GetInt32());
                JsonElement tree = result.GetProperty("tree");
                JsonElement[] subjects = tree.GetProperty("tokens").EnumerateArray().Where(token =>
                    token.GetProperty("roles").EnumerateArray().Any(role => role.GetProperty("role").GetString() == "Subject")).ToArray();
                Assert.HasCount(1, subjects, text);
                JsonElement subject = subjects[0];
                Assert.AreEqual(("тя", text.IndexOf("тя", StringComparison.Ordinal), 2),
                    (subject.GetProperty("text").GetString(), subject.GetProperty("start").GetInt32(), subject.GetProperty("length").GetInt32()), text);
                Assert.IsFalse(subject.GetProperty("roles").EnumerateArray().Any(role =>
                    role.GetProperty("role").GetString() is "Object" or "PrepositionalObject"), text);
                JsonElement subjectRole = subject.GetProperty("roles").EnumerateArray().Single(role =>
                    role.GetProperty("ruleId").GetString() == "BasicSubject");
                Assert.AreEqual("candidate", subjectRole.GetProperty("status").GetString(), text);
                int predicate = subjectRole.GetProperty("contextTokenIndices").EnumerateArray().Single().GetInt32();
                Assert.AreEqual("разговаряше", tree.GetProperty("tokens")[predicate].GetProperty("text").GetString()!.ToLowerInvariant(), text);
                if (text.StartsWith("Разговаряше", StringComparison.Ordinal))
                    CollectionAssert.AreEqual(new[] { "с", "един", "много", "добър", "човек" },
                        tree.GetProperty("tokens").EnumerateArray().Where(token => token.GetProperty("roles").EnumerateArray()
                            .Any(role => role.GetProperty("ruleId").GetString() == "BasicPrepositionalObject"))
                            .Select(token => token.GetProperty("text").GetString()).ToArray(), text);
            }

            const string wrong = "С един много добър човек тя ще разговаряме.";
            const string corrected = "С един много добър човек тя ще разговаря.";
            foreach (string text in new[] { wrong, corrected })
            {
                string invocation = (++revision).ToString(System.Globalization.CultureInfo.InvariantCulture);
                await Send(socket, JsonSerializer.Serialize(new { type = 1, invocationId = invocation, target = "Analyze", arguments = new object[] { revision, text } }), deadline.Token);
                using JsonDocument answer = await Completion(socket, invocation, deadline.Token);
                JsonElement tree = answer.RootElement.GetProperty("result").GetProperty("tree");
                JsonElement[] warnings = tree.GetProperty("diagnostics").EnumerateArray().Where(d =>
                    d.GetProperty("code").GetString() == "productive_morphology_agreement_mismatch").ToArray();
                if (text == wrong)
                {
                    Assert.HasCount(1, warnings, text);
                    JsonElement warning = warnings[0];
                    Assert.AreEqual((wrong.IndexOf("разговаряме", StringComparison.Ordinal), "разговаряме".Length),
                        (warning.GetProperty("start").GetInt32(), warning.GetProperty("length").GetInt32()), text);
                    Assert.AreEqual("clause_fusion_agreement.v1", warning.GetProperty("ruleId").GetString(), text);
                }
                else
                {
                    Assert.IsEmpty(warnings, text);
                    Assert.IsFalse(tree.GetProperty("diagnostics").EnumerateArray().Any(d =>
                        d.GetProperty("code").GetString()!.EndsWith("_mismatch", StringComparison.Ordinal)), text);
                }
            }
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", deadline.Token);
        });
    }

    [TestMethod, TestCategory("FullPackage")]
    public async Task SignalR_coordinated_predicates_and_bound_clitics_reach_the_existing_tree_route()
    {
        await ContextCompositionHttpTests.WithPackage(async (_, _, client) =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(40));
            using var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("Origin", client.BaseAddress!.GetLeftPart(UriPartial.Authority));
            var uri = new UriBuilder(new Uri(client.BaseAddress, "/hubs/syntax")) { Scheme = "ws" };
            await socket.ConnectAsync(uri.Uri, deadline.Token);
            await Send(socket, "{\"protocol\":\"json\",\"version\":1}", deadline.Token);
            Assert.AreEqual("{}", await Read(socket, deadline.Token));

            foreach ((string text, string first, string second) in new[]
            {
                ("Много ви моля, успокойте се и ми се доверете.", "успокойте", "доверете"),
                ("Аз чета и пиша.", "чета", "пиша")
            })
            {
                await Send(socket, JsonSerializer.Serialize(new { type = 1, invocationId = "coordinated",
                    target = "Analyze", arguments = new object[] { 200, text } }), deadline.Token);
                using JsonDocument answer = await Completion(socket, "coordinated", deadline.Token);
                JsonElement tree = answer.RootElement.GetProperty("result").GetProperty("tree");
                JsonElement[] tokens = tree.GetProperty("tokens").EnumerateArray().ToArray();
                JsonElement[] edges = tree.GetProperty("edges").EnumerateArray().ToArray();
                JsonElement Word(string word, bool last = false)
                {
                    MatchCollection matches = Regex.Matches(text, $@"\b{Regex.Escape(word)}\b");
                    int expectedStart = matches[last ? matches.Count - 1 : 0].Index;
                    return tokens.Single(token => token.GetProperty("text").GetString() == word &&
                        token.GetProperty("start").GetInt32() == expectedStart);
                }
                int Index(JsonElement token) => token.GetProperty("index").GetInt32();
                bool HasRole(JsonElement token, string role) => token.GetProperty("roles").EnumerateArray().Any(row =>
                    row.GetProperty("source").GetString() == "syntax" && row.GetProperty("role").GetString() == role);
                void Relation(JsonElement target, JsonElement carrier, string label) => Assert.IsTrue(edges.Any(edge =>
                    edge.GetProperty("source").GetString() == "syntax" && edge.GetProperty("label").GetString() == label &&
                    edge.GetProperty("from").GetInt32() == Index(target) && edge.GetProperty("to").GetInt32() == Index(carrier)),
                    $"{text}: expected {target.GetProperty("text")} → {carrier.GetProperty("text")} ({label}).");

                Assert.IsTrue(HasRole(Word(first), "Predicate"), text);
                Assert.IsTrue(HasRole(Word(second), "Predicate"), text);
                Relation(Word(second), Word(first), "coordinated_predicate");
                Relation(Word("и"), Word(second), "coordinating_conjunction");
                Assert.AreEqual("partial", tree.GetProperty("status").GetString(), text);
                if (first == "успокойте")
                {
                    Assert.IsTrue(HasRole(Word("ми"), "Object"), text);
                    Relation(Word("ми"), Word(second), "object_clitic");
                    Assert.IsTrue(HasRole(Word("се"), "ReflexiveClitic"), text);
                    Assert.IsTrue(HasRole(Word("се", last: true), "ReflexiveClitic"), text);
                    Relation(Word("се"), Word(first), "bound_reflexive_clitic");
                    Relation(Word("се", last: true), Word(second), "bound_reflexive_clitic");
                    Assert.IsFalse(edges.Any(edge => edge.GetProperty("source").GetString() == "syntax" &&
                        edge.GetProperty("from").GetInt32() == Index(Word("ми")) &&
                        edge.GetProperty("to").GetInt32() == Index(Word("се", last: true))), text);
                }
            }
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", deadline.Token);
        });
    }

    private static Task Send(ClientWebSocket socket, string json, CancellationToken cancellation) =>
        socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(json + '\u001e')), WebSocketMessageType.Text, true, cancellation);

    private static async Task<string> Read(ClientWebSocket socket, CancellationToken cancellation)
    {
        using var bytes = new MemoryStream();
        byte[] buffer = new byte[4096];
        WebSocketReceiveResult part;
        do
        {
            part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellation);
            Assert.AreEqual(WebSocketMessageType.Text, part.MessageType);
            bytes.Write(buffer, 0, part.Count);
            Assert.IsTrue(bytes.Length <= HttpContract.DefaultResponseBytes);
        } while (!part.EndOfMessage);
        return Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\u001e');
    }

    private static async Task<JsonDocument> Completion(ClientWebSocket socket, string invocationId, CancellationToken cancellation)
    {
        while (true)
            foreach (string frame in (await Read(socket, cancellation)).Split('\u001e', StringSplitOptions.RemoveEmptyEntries))
            {
                JsonDocument message = JsonDocument.Parse(frame);
                if (message.RootElement.TryGetProperty("invocationId", out JsonElement id) && id.GetString() == invocationId)
                    return message;
                Assert.AreEqual(6, message.RootElement.GetProperty("type").GetInt32(), "Only keep-alive frames may precede this completion.");
                message.Dispose();
            }
    }
}
