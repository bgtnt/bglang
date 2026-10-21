# BgLang

## v0.1.0-preview.5 — подготовка на следващия prerelease

**Текущият source checkout подготвя `v0.1.0-preview.5`. Новата версия, assets и нов CI резултат още не са публикувани или потвърдени.** Публикуваният preview.4 е отделната историческа версия по-долу; неговите source/runtime assets не съдържат автоматично следващите разширения.

Локално са приети Lexical, Text, Context, Syntax, Validation и owner-bound constructions за 20 семейства. Това довежда плана до **10 от 11 квалифицирани области на интеграция**; stress/explicit-accent multigroup authority остава задържана. Броят описва проектния обхват, без обща езикова accuracy или завършен parser. Animacy/Transitivity, общ NER, semantic disambiguation и corpus accuracy не са обещани.

Новият целеви runtime е пълният annotated `bglang-neutral:bec73e12c06e560f`, schema `bglang-neutral-runtime-package.v2`, с точно четири data файла: `manifest.json`, `lexicon.v3.jsonl`, `word-formation.v0.json` и `lexeme-annotations.v1.json` — общо **103 374 440 RAW data bytes**. ZIP размерът, .NET binaries и RAM са отделни величини. API използва готовия runtime; authoring input е нужен само за отделното offline възпроизвеждане.

Използвайте source export и runtime от една и съща бъдеща проверена distribution. [Текущият guide](packaging/RELEASE-CANDIDATE.md) описва four-file package recipe, source-only режима, CLI и optional HTTP consumer. [Input lock](data/dictionary/public-input-lock.v1.json) фиксира authoring input, current graph и output hashes; липса или mismatch е FAIL без private fallback. Нови download links ще се добавят след действително publication readback.

### C# API и optional анализи

```csharp
using BgLang.Api;
using BgLang.Contracts;

using var language = BulgarianLanguageSession.Open(manifestPath);
CancellationToken token = CancellationToken.None;
LexicalEntryLookup owners = language.LookupLexicalEntries("пиша", 32, token);
if (owners.MetadataAvailable && owners.Entries.Count > 0)
{
    WordConstructionResult candidates = language.GetWordConstructionCandidates(
        owners.Entries[0].EntryId, token);
}
TextAnalysis text = language.AnalyzeText("😀 писал съм.", false,
    new TextContextOptions { IncludeWordConstructions = true }, token);
```

`AnalyzeText` запазва оригиналния текст/UTF-16 spans и връща Lexical, complete ReadingInventory, Metrics, advisory Spelling, Profile, ProperNames и bounded Context/Syntax/Validation. Основните Context/Syntax/Validation остават активни при default options, с изрична availability. Допълнителните role, subordinate-comma, fragment, spacing и constructions опции са пет независими strict booleans с default false; unknown-context ProperNames има отделен opt-in.

`WordConstructions` е nullable петнадесето Text поле. Disabled е `null` и не придобива owner forms. Enabled представя candidate evidence от действителните complete exact readings; `Complete`, owner status и unavailable token/reading references показват ограниченията. Запазват се raw duplicates, aliases, families/targets и отделните occurrences. Text съвпаденията изискват поне две части, реално whitespace и едно sentence. Едночастните кандидати са query-only. `AcceptedWordForm=false`, `GrammarAuthority=false` и `ProductiveReplayAvailable=false` с `OriginalGenerationSeedNotRetained` не дават lexical compatibility или corpus attestation.

### Console и HTTP

Съществуващият CLI е само `<manifest.json> [--json] [--measure] [--text-file <utf8.txt>]`. Няма CLI Context/construction flags. Source projection добавя всички 15 Text fields към съществуващия JSON wrapper; result полетата са PascalCase, а outer `method/input/result` са lowercase. Default JSON не доказва opt-in изпълнение. Точният export/projection consumer run остава задължителен преди release.

```powershell
dotnet build examples/BgLang.PublicExample/BgLang.PublicExample.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw 'Example build failed.' }
dotnet run --project examples/BgLang.PublicExample/BgLang.PublicExample.csproj `
    -c Release --no-build --no-restore -- ./runtime/manifest.json --json
if ($LASTEXITCODE -ne 0) { throw 'Example failed.' }
```

HTTP host/tests са отделен, optional consumer, без промяна на default Product solution. [Web guide](apps/BgLang.Web/README.md) и `/docs` описват v1/v2 и трите response formats. `word-constructions` използва съществуващото JSON поле `text`, чиято стойност е точният opaque `EntryId`; поле `entryId` се отхвърля. Whitespace в ID не се trim-ва.

```powershell
$env:BgLang__ManifestPath = (Resolve-Path './runtime/manifest.json').Path
$env:BgLang__ManifestSha256 = '7e37b0f47a1ad4dcd23c899258c5fd09aa40538cba61b55987533481c2ff6651'
dotnet run --project apps/BgLang.Web/BgLang.Web.csproj -c Release
```

Host default е `http://127.0.0.1:5070`. Примерен construction input е `{"text":"<точният EntryId от lookup-lexical-entries>"}`; за Text opt-in: `{"text":"😀 писал съм.","context":{"includeWordConstructions":true}}` към `/api/v2/analyze-text`.

### Validation и release identity

Локално приетият runtime batch има 3972 Passed / 0 Failed / 13 exact historical NotExecuted, с 43 mandatory package gate sets; historical skips не получават completion credit. Тези резултати не са нов exported-source consumer run или public CI. Преди preview.5 са нужни actual twin four-file reproduction, exact source export, Public/console/optional HTTP validation, release checksums и отделен publication readback. SourceOnly не включва authoring inputs или runtime и сам по себе си не възпроизвежда data package.

Съществуващият Windows workflow е consumer validation с `contents: read`, не publication job. Той използва HTTPS URL за точно locked authoring asset, а не runtime ZIP или трите preview.4 STORED inputs. Новият public source commit/tag, RAW export manifest, runtime/archive hashes и public CI identity се записват отделно. Не се заменят historical producer anchors с public commit ID.

---

## Исторически публикуван preview.4

Следващите инструкции, identities, source/lock/workflow references и validation са за immutable preview.4 checkout. Командите му се изпълняват само с неговия source/recipe/input набор. За текущия preview.5 source използвайте новите секции и текущия guide по-горе.


**[Изтегли preview 4](https://github.com/bgtnt/bglang/releases/tag/v0.1.0-preview.4)** — публикуван prerelease.

Български езиков API за речниково търсене, словоформи, анализ на думи и текст,
словообразувателни кандидати и езикови диагностики. Публичният вход е
`BulgarianLanguageSession.Open(manifestPath)`. Пакетът се проверява по размер,
SHA-256, структура и идентичност преди използване.

## Публикуван preview.4

`v0.1.0-preview.4` е публичен prerelease от `2026-10-19T16:54:23Z`.
Неговият public source/tag snapshot е `06d2a2d8461aed0131edceff71ac7fce6e236b63`.
Приетият local implementation base `6d3672173cae1688dc36612d1dffc2a49366c7f0`
и local documentation metadata commit `41c88c16fe84e6434f6ffdf343e49f16d113922e`
имат отделни роли от public source/tag. Последващи docs commits в `main`
не променят immutable tag, source ZIP, RAW source manifest или тяхната validation.

## Бърз старт с публикувания preview.4

Следващите download указания и runtime identity са за preview.4.
Използвайте runtime и source bundle от един и същи preview.

Изтеглете runtime ZIP и `SHA256SUMS` от release, проверете SHA-256 и
разархивирайте трите файла в `./runtime`. За API не са нужни STORED inputs
или изпълнение на package producer.

Native пакетът `bglang-neutral:de06b9b2e254e61a` съдържа `manifest.json`,
`lexicon.v3.jsonl`, `word-formation.v0.json`: общо **49 443 153 data bytes**,
**119 290 lexemes /1 549 263 readings /1 266 121 distinct forms**.
ZIP размерът, .NET библиотеките и RAM са отделни величини. С source
checkout/bundle, .NET SDK **10.0.402** и PowerShell 7:

```powershell
$ErrorActionPreference = 'Stop'
$manifest = (Resolve-Path './runtime/manifest.json').Path
dotnet build examples/BgLang.PublicExample/BgLang.PublicExample.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw 'Example build failed.' }
dotnet run --project examples/BgLang.PublicExample/BgLang.PublicExample.csproj `
    -c Release --no-build --no-restore -- $manifest --json
if ($LASTEXITCODE -ne 0) { throw 'Example failed.' }
```

```csharp
using BgLang.Api;
using BgLang.Contracts;

using var language = BulgarianLanguageSession.Open(manifestPath);
var readings = language.Lookup("книгата", LookupMode.Exact, 16);
var forms = language.GenerateForms("книга", 256);
var word = language.AnalyzeWord("свръхсупертракторите", 256);
var text = language.AnalyzeText("Аз го четем.");
```

Историческият [preview.3](https://github.com/bgtnt/bglang/releases/tag/v0.1.0-preview.3)
има public source/tag `2b7c30266641302d9168ec3fa80915753b974dd9`.
Неговият publication snapshot стъпва върху accepted source anchor
`ae247f60da2122a2d7a8f925058949dda744a95c`. Точните public source и asset
идентичности са в неговите `publication.json`/`SHA256SUMS` и source bundle
`public-source-manifest.json`. Snapshot anchor, public commit и запазеният
producer anchor `source.baseCommit` са различни роли. Старите immutable
contracts/status и pending/publication=false snapshots остават история.

## Езикови промени и validation в preview.4

Публикуваният native output е `bglang-neutral:de06b9b2e254e61a`:
**49 443 153 data bytes**, **119 290 lexemes /1 549 263 readings /
1 266 121 distinct forms**. Той използва current graph от **2 106 720 bytes**
и source `neutral-source-v3:cd595704188df3b69aa1321901ba74d17293f013351e55701ef87c5b615ed6e9`.
Трите native файла в приетата exported distribution съвпадат точно с този output.

- Exact adverb admission за `късно` добавя сравнителен `по-късно` и
  превъзходен `най-късно` path в съществуващия typed degree механизъм.
  Лексикалните readings и отделният adjective homograph остават.
- BERON пряко класифицира `късно` като наречие. И двете степени са изрично
  rule-based inferences по правила 58/114.1, а не преки речникови цитати.
  Промяната на provenance wording изисква нова graph/package identity.

За приетия implementation са затворени Release2, Focus48P, Public37P,
Regressions265P и Full3314P/0F/13 historical NotExecuted с42 package gates.
Отделно е приета validation на exported distribution за preview.4:
**Public37P/0F/0skip**, **23 mandatory gates, от които7 complete-package**,
**три Release builds с0 warnings/0 errors** и exact съвпадение на трите native файла.
Тези резултати са за immutable release snapshot-а; последващ docs update
не е нов distribution build или нова validation на source ZIP.

## Как се четат резултатите

Примерът показва lookup/forms, known/unknown analysis, exact и conditional
paths, derivation/decomposition, fuzzy lookup, segmentation и diagnostics.
Console отделя readings, `BaseEvidence`, graph, candidates, known-base и
base-type lanes. Всяка съкратена колекция има `total`, `displayed`, подредени
`items`. Display3 е presentation, не engine cap. `--json` запазва пълните
резултатни arrays/evidence, property order и explicit false/null fields.

Запазеният consumer пример върху предходния native `fa4b0d8a76e8b7a2`
показва `суперработник` като `known=False`, без exact readings:
graph `17/3` total/displayed, candidates `5/3`, known-base `0/0`, base-type `2/2`.
Exact-root path от `работник` стои отделно от conditional self-root с 1042
supporting lemmas и unmet exact-self requirement. Това не прави думата известна
или хипотезата приета. [Подробният пример](packaging/RELEASE-CANDIDATE.md#прочит-на-console-и-json)
показва program/tag, donors и всичките седем incompleteness flags. Празен
списък не доказва липса при съответния limited/incomplete flag; `BaseEvidence`
е graph decomposition evidence, не речников verdict.

„Аз го четем.“ дава agreement warning. Поддържат се кратки pronoun/particle/
clitic/finite-verb модели; SVO „Аз четем книга.“ е извън текущото покритие.
Липсата на diagnostic не доказва правилност. Example-only `Classification`
е `warning`, `unsupported-check` или `information`; оригиналните `Code`,
`Start`, `Length`, `RuleId`, `ProjectedText` се запазват. `TextDiagnostic`
не се променя. Warning е предложение за проверка, не автоматична поправка;
непознат код остава information.

## Езикови промени в публикувания preview.3

- Простото imperative има typed `Person=Second`, запазено число и `Tense=null`;
  RAW tags и допустимите омографски readings остават.
- `ела`/`елате` са добавени imperative алтернативи на `дойда`; старите 38
  readings и отделното съществително `ела` са запазени.
- Unicode/diagnostics пазят canonical еквивалентност и RAW spans, използват
  owner-aware stress lookup и request-local fuzzy reuse; примерът различава
  warning, unsupported-check и information без нов diagnostic DTO.
- Conditional proof отказва липсващ inferred root с празна част около `-`;
  валидните многокомпонентни roots и exact-self authority остават.

Първите две партиди са пряко породени от корпусния преглед; другите две са
от отделен review/analysis. Това не е оценка на качеството върху целия корпус.
Подробният [release guide](packaging/RELEASE-CANDIDATE.md#езикови-промени-и-граници)
описва границите и запазените historical skips.

Unicode проверките пазят RAW UTF-16 text/spans и canonical двойките
`той`/`тои\u0306`, `кафѐ`/`кафе\u0300`, `ѝ`/`и\u0300`, при `и≠ѝ`.
Няма NFKC или универсално stress coverage. Stress support изисква необходимата
exact-owner evidence. Cancellation се проверява по операция/текст граници;
една вече започнала DAWG операция не се прекъсва вътрешно.

## Измерване на примера

За командата по-долу подгответе свой strict UTF-8 файл `sample.utf8.txt`.

```powershell
dotnet run --project examples/BgLang.PublicExample/BgLang.PublicExample.csproj `
    -c Release --no-build --no-restore -- $manifest --json --measure `
    --text-file ./sample.utf8.txt 1>analysis.json 2>measurements.json
if ($LASTEXITCODE -ne 0) { throw 'Measured example failed.' }
```

`--text-file` добавя точно един `AnalyzeText`, чете strict UTF-8 и пази input
bytes/hash и UTF-16 length. `--measure` отделя startup, `Open`/API и еднократна
projection/summary подготовка. Финалната serialization ползва вече получените
резултати. Stderr footer `bglang-public-example.measurement.v1` отчита точния
UTF-8 stdout: `payloadBytes + trailingNewlineBytes = outputBytes`, без да
брои самия себе си. Без `--measure` няма такъв footer; unmeasured wire е запазен.

JSON serialization включва целия подготвен envelope и measurement полетата.
Console preparation включва вложеното summary formatting; финалната фаза
съединява готовите редове и ги кодира. IO, snapshots и създаването на
measurement objects са извън тези timers. За byte проверка пазете raw stdout;
shell/logger може да промени encoding/newlines. Process-wide allocations,
lifetime peak, задържаните console buffers и cache/shared-host ефектите са
обяснени в [точните граници](packaging/RELEASE-CANDIDATE.md#граници-на-измерването).
Единичните observations не са RAM/latency сертификат или общ speedup claim.

## Възпроизвеждане, CI и езиков обхват

Data reproduction за публикувания preview.4 е отделен offline workflow с
complete corrected STORED inputs `bglang-neutral:6de86ba569c1001a`
(**269 108 994 bytes**) и source/recipe от съответния immutable export.
Приетият comparison target е native `de06b9b2e254e61a`.
Native output никога не е build input. [Portable guide](packaging/RELEASE-CANDIDATE.md#възпроизвеждане-на-native-data)
и [input lock](data/dictionary/public-input-lock.v1.json) дават exact identities,
hashes и fail-fast команди. [Allowlist](packaging/public-source-allowlist.v1.json)
и RAW source manifest определят точния source export; не смесвайте recipe,
STORED inputs и native output от различни previews.

[Windows workflow](.github/workflows/windows-public.yml) приема explicit
`input_base_url`/`BGLANG_INPUT_BASE_URL` за трите locked raw STORED assets.
Публикуваният raw input base за preview.4 е
`https://github.com/bgtnt/bglang/releases/download/v0.1.0-preview.4`;
добавя се exact locked STORED asset име. Трите raw STORED assets са отделни
от runtime ZIP; recipe, lock и assets трябва да са от preview.4.
Missing URL/hash mismatch е FAIL без private/preview.1 fallback или mixing.
[CI run39289839846](https://github.com/bgtnt/bglang/actions/runs/39289839846)
е completed **SUCCESS**, head `06d2a2d8461aed0131edceff71ac7fce6e236b63`,
attempt1, приключил в `2026-10-19T17:00:30Z`. Това е резултатът за точния
public snapshot, отделно от implementation и publication evidence.

Corrected finite residual0 е приет в ограничен dictionary/register scope.
Десетте residual10 owners имат `449→218 readings` и четири base-tag корекции —
не брой surface corrections, уникални грешки или общ package reason count.
[Квалификацията](packaging/RELEASE-CANDIDATE.md#какво-означават-residual10-корекциите)
разделя direct attestation и class/register inference, включително
`пребъда`/`пребил`. Full Legacy language, all-variant morphology, productive
precision, analyzers и corpus acceptance остават отделни; historical failures
и original RAW census не се пренаписват. Preview.3 и preview.4 не затварят M1–M4 или corpus
acceptance. Запазените 13 historical skips са отделни opt-in scopes без нов
acceptance credit. Няма общ claim за непроменен ресурсен разход.

Кодът и авторските езикови данни са под [MIT](LICENSE).
Външните зависимости запазват [своите лицензи](THIRD-PARTY-NOTICES.md).
