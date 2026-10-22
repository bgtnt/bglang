using System.Text.Json;
using BgLang.Api;

namespace BgLang.Web;

internal static class WebHostFactory
{
    internal static WebApplication Create(string[] args, Func<BulgarianLanguageSession>? open = null,
        TimeProvider? rateTime = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environments.Production
        });
        // No request logging provider or retained log file, including in local runs.
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = HttpContract.BodyBytes;
            options.Limits.MaxConcurrentConnections = 64;
            options.Limits.MaxConcurrentUpgradedConnections = 0;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(30);
        });
        if (string.IsNullOrEmpty(builder.Configuration["urls"])) builder.WebHost.UseUrls("http://127.0.0.1:5070");
        builder.Services.AddSingleton(_ => new SessionHost(
            builder.Configuration["BgLang:ManifestPath"] ?? "",
            builder.Configuration["BgLang:ManifestSha256"] ?? "", open));
        builder.Services.AddHostedService(provider => provider.GetRequiredService<SessionHost>());
        builder.Services.AddSingleton(new ClientRateLimit(rateTime));
        builder.Services.AddSingleton(new ResponseWriter(
            builder.Configuration.GetValue<long?>("BgLang:ResponseByteLimit") ?? HttpContract.DefaultResponseBytes));

        WebApplication app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Content-Security-Policy"] =
                "default-src 'self'; script-src 'self'; style-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'self'";
            context.Response.Headers.CacheControl = "no-store";
            await next(context);
        });
        // Forwarded IP/host/scheme headers are deliberately ignored. See README for deployment trust.
        app.UseStaticFiles();
        app.MapGet("/", () => Results.Redirect("/demo"));
        app.MapGet("/demo", (IWebHostEnvironment environment) =>
            Results.File(Path.Combine(environment.WebRootPath, "demo.html"), "text/html; charset=utf-8"));
        app.MapGet("/docs", (IWebHostEnvironment environment) =>
            Results.File(Path.Combine(environment.WebRootPath, "docs.html"), "text/html; charset=utf-8"));
        app.MapGet("/health/live", () => Results.Json(new { status = "live" }));
        app.MapGet("/health/ready", (SessionHost host) =>
            Results.Json(new { status = host.Ready ? "ready" : "stopping" }, statusCode: host.Ready ? 200 : 503));
        foreach (string version in new[] { "1", "2" })
        {
            app.MapGet($"/api/v{version}/", (SessionHost host, ResponseWriter writer) => Results.Json(new
            {
                apiVersion = version, versions = host.Versions, package = host.Provenance,
                defaultEvidenceFormat = version == "2" ? SharedDonorResponse.AnalysisFormat : HttpContract.RawFormat,
                evidenceFormats = new[] { HttpContract.RawFormat, SharedDonorResponse.Format, SharedDonorResponse.AnalysisFormat },
                limits = new { wordUtf16 = HttpContract.WordLength, textUtf16 = HttpContract.TextLength,
                    resultLimit = HttpContract.MaximumResultLimit,
                    defaultResultLimit = HttpContract.DefaultResultLimit,
                    generateFormsDefaultLimit = HttpContract.GenerateFormsDefaultLimit,
                    textWordAnalysisLimit = HttpContract.TextWordAnalysisLimit,
                    queryAnalysisLimit = HttpContract.QueryAnalysisLimit, bodyBytes = HttpContract.BodyBytes,
                    responseBodyBytes = writer.MaximumBytes, responseAdmission = "count-before-headers; no clipping",
                    concurrency = 1, queueCapacity = HttpContract.QueueCapacity,
                    queueWaitMilliseconds = HttpContract.QueueWait.TotalMilliseconds,
                    responseTimeoutMilliseconds = HttpContract.ResponseTimeout.TotalMilliseconds,
                    responseDeadline = "cooperative language success; includes body, kernel, projection, serialization and transfer; protocol errors use request cancellation",
                    requestsPerMinutePerIp = HttpContract.RequestsPerMinute,
                    retainedIpMaximum = HttpContract.ClientCapacity },
                forwardedHeadersTrusted = false
            }, HttpContract.Json));
            foreach (string operation in new[] { "lookup", "lookup-lexical-entries", "lexical-entry", "word-constructions", "stress", "generate-stress-forms", "derivational-stress", "analyze-word", "analyze-text", "generate-forms", "derive-words", "decompose-word", "find-forms-in-text" })
                app.MapPost($"/api/v{version}/" + operation,
                    (HttpContext context, SessionHost host, ClientRateLimit rate, ResponseWriter writer) =>
                        HandleAsync(context, host, rate, writer, operation, version));
        }
        return app;
    }

    private static async Task HandleAsync(HttpContext context, SessionHost host, ClientRateLimit rate,
        ResponseWriter writer, string operation, string version)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);
        deadline.CancelAfter(HttpContract.ResponseTimeout);
        try
        {
            string client = context.Connection.RemoteIpAddress?.MapToIPv6().ToString() ?? "unknown";
            (int status, int retry) = rate.TryAcquire(client);
            if (status != 200)
            {
                context.Response.Headers.RetryAfter = retry.ToString(System.Globalization.CultureInfo.InvariantCulture);
                throw new ApiRequestException(status, "rate-limited", "Услугата е достигнала лимита за заявки. Опитай по-късно.");
            }
            if (!context.Request.HasJsonContentType())
                throw new ApiRequestException(415, "json-required", "Използвай Content-Type: application/json.");
            if (context.Request.ContentLength > HttpContract.BodyBytes)
                throw new ApiRequestException(413, "body-too-large", "HTTP тялото надвишава 32 KiB.");
            WebInput? input = await JsonSerializer.DeserializeAsync<WebInput>(context.Request.Body, HttpContract.Json, deadline.Token);
            if (input is null) throw new ApiRequestException(400, "invalid-json", "Нужен е JSON обект с поле text.");
            ApiOperations.Validate(operation, input);
            await host.ExecuteResponseAsync(operation, input,
                (result, cancellation) => writer.WriteAsync(context, result with { ApiVersion = version },
                    input.EvidenceFormat, cancellation), deadline.Token);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { }
        catch (OperationCanceledException)
        {
            await ErrorAsync(context, 504, "response-timeout", "Времето за HTTP отговора изтече. Изчаква се прекратяването на започнатата работа преди освобождаване на слота.");
        }
        catch (ApiRequestException error)
        {
            if (error.Status == 503) context.Response.Headers.RetryAfter = "1";
            await ErrorAsync(context, error.Status, error.Code, error.Message);
        }
        catch (JsonException) { await ErrorAsync(context, 400, "invalid-json", "Невалиден JSON или непознати полета."); }
        catch (BadHttpRequestException error)
        {
            await ErrorAsync(context, error.StatusCode, "invalid-http-request", "Невалидна или прекалено голяма HTTP заявка.");
        }
        catch (ArgumentException) { await ErrorAsync(context, 400, "invalid-input", "Входът не отговаря на договора на библиотеката."); }
        catch (Exception) { await ErrorAsync(context, 500, "execution-failed", "Езиковата операция не успя."); }
    }

    internal static Task ErrorAsync(HttpContext context, int status, string code, string message)
    {
        // Fixed small protocol errors run outside the language worker/deadline, under request cancellation.
        if (context.Response.HasStarted)
        {
            // A partial success body cannot be followed by an error JSON or be completed as valid 200.
            context.Abort();
            return Task.CompletedTask;
        }
        if (context.RequestAborted.IsCancellationRequested) return Task.CompletedTask;
        context.Response.StatusCode = status;
        // Oversized bodies leave an unread or faulted stream; HTTP/1 must not reuse that connection.
        if (status == StatusCodes.Status413PayloadTooLarge && context.Request.Protocol is "HTTP/1.0" or "HTTP/1.1")
            context.Response.Headers.Connection = "close";
        // Frame the small error explicitly so receiving it does not depend on a final chunk suffix.
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(new ApiError(code, message)
        {
            Coverage = code == "response-too-large" ? new(true, true, false) : null
        }, HttpContract.Json);
        context.Response.ContentType = "application/json; charset=utf-8";
        context.Response.ContentLength = body.Length;
        return context.Response.Body.WriteAsync(body, context.RequestAborted).AsTask();
    }
}
