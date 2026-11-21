using BgLang.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace BgLang.Web;

/// <summary>Interactive tree requests use the same bounded session lane as the HTTP API.</summary>
internal sealed class SyntaxHub(SessionHost host) : Hub
{
    // Per-connection invocations are serialized by SignalR; the editor sends one active revision at a time.
    public async Task<object> Analyze(long revision, string text)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Context.ConnectionAborted);
        deadline.CancelAfter(HttpContract.ResponseTimeout);
        try
        {
            var input = new WebInput(text, IncludeUnknownContextProperNames: true, Context: new TextContextOptions
            {
                IncludeAdditionalRoleHeuristics = true,
                IncludeSubordinateCommaDiagnostics = true,
                IncludeFragmentDiagnostics = true,
                IncludeSpacingDiagnostics = true,
                IncludeNominalConstructions = true,
                IncludeGrammarEvidence = true
            });
            ApiOperations.Validate("analyze-text", input);
            return await host.AnalyzeSyntaxAsync(revision, input, deadline.Token);
        }
        catch (ApiRequestException error) { throw new HubException($"{error.Code}: {error.Message}"); }
        catch (OperationCanceledException) when (Context.ConnectionAborted.IsCancellationRequested)
        { throw new HubException("request-cancelled: Връзката е прекратена."); }
        catch (OperationCanceledException)
        { throw new HubException("response-timeout: Времето за анализ изтече. Опитай с по-кратък текст."); }
        catch (ArgumentException)
        { throw new HubException("invalid-input: Входът не отговаря на договора на библиотеката."); }
    }

    internal static bool HasSameOrigin(HttpRequest request)
    {
        // Non-browser clients may omit Origin. Browsers must name this request's actual origin;
        // forwarded headers remain untrusted just as they are for the HTTP API.
        var origins = request.Headers.Origin;
        if (origins.Count == 0) return true;
        return origins.Count == 1 && Uri.TryCreate(origins[0], UriKind.Absolute, out Uri? origin) &&
            origin.Scheme is "http" or "https" && origin.UserInfo.Length == 0 &&
            origin.AbsolutePath == "/" && origin.Query.Length == 0 && origin.Fragment.Length == 0 &&
            Uri.TryCreate($"{request.Scheme}://{request.Host}", UriKind.Absolute, out Uri? expected) &&
            Uri.Compare(origin, expected, UriComponents.SchemeAndServer, UriFormat.UriEscaped,
                StringComparison.OrdinalIgnoreCase) == 0;
    }
}
