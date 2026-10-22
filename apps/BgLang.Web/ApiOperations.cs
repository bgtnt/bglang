using BgLang.Api;
using BgLang.Contracts;

namespace BgLang.Web;

internal static class ApiOperations
{
    internal static void Validate(string operation, WebInput input)
    {
        if (input.IncludeUnknownContextProperNames is not null && operation != "analyze-text")
            throw new ApiRequestException(400, "option-not-applicable", "Контекстните кандидати за собствени имена се включват само за analyze-text.");
        if (input.Context is not null && operation != "analyze-text")
            throw new ApiRequestException(400, "option-not-applicable", "Контекстните опции се използват само за analyze-text.");
        if (input.PrototypeEntryId is not null && operation != "generate-stress-forms")
            throw new ApiRequestException(400, "option-not-applicable", "Лексемният прототип се използва само за generate-stress-forms.");
        if (operation == "generate-stress-forms" && string.IsNullOrWhiteSpace(input.PrototypeEntryId))
            throw new ApiRequestException(400, "prototype-required", "Избери точен лексемен прототип чрез prototypeEntryId.");
        if (input.StressSelection is not null)
        {
            if (operation != "stress")
                throw new ApiRequestException(400, "option-not-applicable", "Изборът на прочит за ударение се използва само за stress.");
            StressReadingSelection selection = input.StressSelection;
            // The existing body-byte bound also bounds opaque selection strings.
            if (selection.EntryId is null || selection.Form is null || selection.TokenIndex < 0)
                throw new ApiRequestException(400, "invalid-stress-selection", "Изборът изисква индекс на токен, точен собственик, форма и fullTag.");
        }
        if (input.EvidenceFormat is not (null or HttpContract.RawFormat or SharedDonorResponse.Format
            or SharedDonorResponse.AnalysisFormat))
            throw new ApiRequestException(400, "invalid-evidence-format", "Непознат формат за evidence.");
        bool textOperation = operation is "analyze-text" or "find-forms-in-text" or "stress";
        int maximum = textOperation ? HttpContract.TextLength : HttpContract.WordLength;
        if (input.Text is null || string.IsNullOrWhiteSpace(input.Text))
            throw new ApiRequestException(400, "text-required", "Въведи непразен текст.");
        if (input.Text.Length > maximum)
            throw new ApiRequestException(400, "text-too-long", $"Лимитът е {maximum} UTF-16 символа.");
        if (input.Text.Any(character => char.IsControl(character) &&
            (!textOperation || character is not ('\r' or '\n' or '\t'))))
            throw new ApiRequestException(400, "invalid-control-character", "Текстът съдържа недопустим контролен символ.");
        if (input.Limit is < 1 or > HttpContract.MaximumResultLimit)
            throw new ApiRequestException(400, "invalid-limit", $"Поисканият лимит трябва да е между 1 и {HttpContract.MaximumResultLimit}.");
        if (input.Limit is not null && operation is "analyze-text" or "decompose-word" or "find-forms-in-text" or "lexical-entry" or "word-constructions" or "stress" or "generate-stress-forms" or "derivational-stress")
            throw new ApiRequestException(400, "limit-not-applicable", "Тази операция няма входен лимит за резултатите.");
        if (operation == "find-forms-in-text")
        {
            if (string.IsNullOrWhiteSpace(input.Query) || input.Query.Length > HttpContract.WordLength ||
                input.Query.Any(character => char.IsWhiteSpace(character) || char.IsControl(character)))
                throw new ApiRequestException(400, "invalid-query", "Заявката трябва да е дума или лема до 256 UTF-16 символа, без интервали и контролни символи.");
        }
        else if (input.Query is not null)
            throw new ApiRequestException(400, "query-not-applicable", "Отделно поле query се използва само за find-forms-in-text.");
    }

    internal static ApiEnvelope Invoke(string operation, WebInput input, BulgarianLanguageSession session,
        PackageProvenance provenance, RuntimeVersions versions, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // The facade limit can affect discovery and ranking. Never oversample and trim its result.
        int effective = input.Limit ?? (operation == "generate-forms"
            ? HttpContract.GenerateFormsDefaultLimit : HttpContract.DefaultResultLimit);
        object data = operation switch
        {
            "lookup" => session.Lookup(input.Text!, LookupMode.Exact, effective),
            "lookup-lexical-entries" => session.LookupLexicalEntries(input.Text!, effective, cancellationToken),
            "lexical-entry" => session.FindLexicalEntry(input.Text!, cancellationToken),
            "word-constructions" => session.GetWordConstructionCandidates(input.Text!, cancellationToken),
            "stress" => session.GetStressEvidence(input.Text!, input.StressSelection, cancellationToken),
            "generate-stress-forms" => session.GenerateStressForms(input.PrototypeEntryId!, input.Text!, cancellationToken),
            "derivational-stress" => session.GetDerivationalStressEvidence(input.Text!, cancellationToken),
            "analyze-word" => session.AnalyzeWord(input.Text!, effective, cancellationToken),
            "analyze-text" => session.AnalyzeText(input.Text!, input.IncludeUnknownContextProperNames ?? false, input.Context ?? default, cancellationToken),
            "generate-forms" => session.GenerateForms(input.Text!, effective),
            "derive-words" => session.DeriveWords(input.Text!, effective),
            "decompose-word" => session.DecomposeWord(input.Text!),
            "find-forms-in-text" => TextMatchProjection.Find(
                session.AnalyzeWord(input.Query!, HttpContract.QueryAnalysisLimit, cancellationToken),
                session.AnalyzeText(input.Text!, cancellationToken)),
            _ => throw new ApiRequestException(404, "unknown-operation", "Непозната операция.")
        };
        cancellationToken.ThrowIfCancellationRequested();
        bool capped = data switch
        {
            WordAnalysis word => ReachedLimit(word, effective),
            LexicalEntryLookup owners => owners.Truncated,
            TextAnalysis text => LexicalTruncated(text) || text.Tokens.Any(token => token.Analysis is { } word &&
                ReachedLimit(word, HttpContract.TextWordAnalysisLimit)),
            TextMatchResult result => LexicalTruncated(result.TextAnalysis) || ReachedLimit(result.QueryAnalysis, HttpContract.QueryAnalysisLimit) ||
                result.TextAnalysis.Tokens.Any(token => token.Analysis is { } word && ReachedLimit(word, HttpContract.TextWordAnalysisLimit)),
            IReadOnlyList<WordReading> readings => readings.Count >= effective,
            IReadOnlyList<WordForm> forms => forms.Count >= effective,
            IReadOnlyList<WordDerivation> paths => paths.Count >= effective,
            _ => false
        };
        bool incomplete = data switch
        {
            WordAnalysis word => HasLimitFlag(word),
            LexicalEntryLookup owners => owners.Truncated || owners.PackageHasUnresolvedRelations,
            LexicalEntryResult entry => entry.PackageHasUnresolvedRelations,
            TextAnalysis text => text.Lexical.PackageHasUnresolvedRelations || LexicalTruncated(text) || text.Tokens.Any(token => token.Analysis is { } word && HasLimitFlag(word)),
            TextMatchResult result => result.TextAnalysis.Lexical.PackageHasUnresolvedRelations || LexicalTruncated(result.TextAnalysis) || HasLimitFlag(result.QueryAnalysis) ||
                result.TextAnalysis.Tokens.Any(token => token.Analysis is { } word && HasLimitFlag(word)),
            _ => false
        };
        ResultLimits limits = operation switch
        {
            "analyze-text" => new(null, HttpContract.TextWordAnalysisLimit, HttpContract.TextWordAnalysisLimit, false, "per-word-analysis"),
            "decompose-word" or "lexical-entry" or "word-constructions" => new(null, null, null, false, "scalar-evidence"),
            "stress" => new(null, null, null, false, "complete-exact-stress-inventory; lexical-evidence-not-prosody"),
            "generate-stress-forms" => new(null, null, null, false, "complete-guarded-paradigm; explicit-lexical-class-inference"),
            "derivational-stress" => new(null, null, null, false, "reviewed-target-decomposition; qualified-accent-transition"),
            "find-forms-in-text" => new(null, HttpContract.QueryAnalysisLimit, HttpContract.QueryAnalysisLimit, false,
                "per-query-and-word-analysis; all-text-matches-retained"),
            _ => new(input.Limit, effective, HttpContract.MaximumResultLimit, false, "per-result-collection")
        };
        // No rows are removed by the host. Capped discovery is only a possible omission.
        // A facade without an exhaustive-result proof cannot justify Complete=true.
        return new("1", versions, provenance, limits, new(false, capped, incomplete ? false : null), data);
    }

    private static bool ReachedLimit(WordAnalysis word, int limit) =>
        word.Readings.Count >= limit || word.GraphCandidates.Count >= limit ||
        word.Candidates.Count >= limit || word.KnownBaseCandidates.Count >= limit ||
        word.BaseTypeCandidates.Count >= limit;

    private static bool LexicalTruncated(TextAnalysis text) => text.Lexical.Tokens.Any(token => token.Owners.Truncated);

    internal static bool HasLimitFlag(WordAnalysis word) =>
        word.GraphSearchIncomplete || word.GraphCandidatesTruncated || word.CandidateSearchLimited ||
        word.CandidateEvidenceLimited || word.KnownBaseSearchLimited ||
        word.BaseTypeSearchLimited || word.BaseTypeEvidenceLimited;
}
