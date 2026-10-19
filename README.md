# BgLang

Български езиков API за речниково търсене, словоформи, анализ на думи и текст,
словообразувателни кандидати и езикови диагностики. Публичният вход е
`BulgarianLanguageSession.Open(manifestPath)`. Пакетът се проверява по размер,
SHA-256, структура и идентичност преди използване.

## Публикуван preview.3 и планиран preview.4

Публикуваният [`v0.1.0-preview.3`](https://github.com/bgtnt/bglang/releases/tag/v0.1.0-preview.3)
остава готовият download вариант. Неговият public source/tag е
`2b7c30266641302d9168ec3fa80915753b974dd9`.
`v0.1.0-preview.4` е планиран следващ кандидат върху приетия implementation
base `6d3672173cae1688dc36612d1dffc2a49366c7f0`; това не удостоверява remote tag
или публикуван release. Metadata commit, immutable source export и новата
distribution validation предстоят и ще имат собствени identities/резултати.

## Бърз старт с публикувания preview.3

Следващите download указания и runtime identity са за preview.3.
Използвайте runtime и source bundle от един и същи preview.

Изтеглете runtime ZIP и `SHA256SUMS` от release, проверете SHA-256 и
разархивирайте трите файла в `./runtime`. За API не са нужни STORED inputs
или изпълнение на package producer.

Native пакетът `bglang-neutral:1835c7e51b034b3f` съдържа `manifest.json`,
`lexicon.v3.jsonl`, `word-formation.v0.json`: общо **49 438 708 data bytes**,
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

Publication snapshot-ът на preview.3 стъпва върху accepted source anchor
`ae247f60da2122a2d7a8f925058949dda744a95c`. Точните public source и asset
идентичности са в неговите `publication.json`/`SHA256SUMS` и source bundle
`public-source-manifest.json`. Snapshot anchor, public commit и запазеният
producer anchor `source.baseCommit` са различни роли. Старите immutable
contracts/status и pending/publication=false snapshots остават история.

## Прието за планирания preview.4

Приетият local native output е `bglang-neutral:de06b9b2e254e61a`:
**49 443 153 data bytes**, **119 290 lexemes /1 549 263 readings /
1 266 121 distinct forms**. Той използва current graph от **2 106 720 bytes**
и source `neutral-source-v3:cd595704188df3b69aa1321901ba74d17293f013351e55701ef87c5b615ed6e9`.
Това са приети implementation identities, не нови distribution резултати.

- Exact adverb admission за `късно` добавя сравнителен `по-късно` и
  превъзходен `най-късно` path в съществуващия typed degree механизъм.
  Лексикалните readings и отделният adjective homograph остават.
- BERON пряко класифицира `късно` като наречие. И двете степени са изрично
  rule-based inferences по правила 58/114.1, а не преки речникови цитати.
  Промяната на provenance wording изисква нова graph/package identity.

За този приет implementation са затворени Release2, Focus48P, Public37P,
Regressions265P и Full3314P/0F/13 historical NotExecuted с42 package gates.
Те не са validation на бъдещия source export. Public37 е очакване за новата
distribution проверка, не неин вече получен резултат. Нулевият runtime/API/
dependency delta на настоящите два документа е спрямо `6d367217`, не preview.3.

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

Data reproduction за планирания preview.4 е отделен offline workflow с
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
Публикуваният raw input base за preview.3 е
`https://github.com/bgtnt/bglang/releases/download/v0.1.0-preview.3`;
трите raw STORED assets са отделни от runtime ZIP. За preview.4 input delivery
и explicit base URL ще се обвържат с exact lock/asset hashes; не се предполага
нов URL или автоматичен fallback. Няма private/preview.1 fallback. Hosted CI evidence
се чете за точния public SHA/run; локален build или publication сами по себе си
не установяват green CI. Release metadata трябва да свързва точния public SHA
с действителните CI и publication резултати.

Corrected finite residual0 е приет в ограничен dictionary/register scope.
Десетте residual10 owners имат `449→218 readings` и четири base-tag корекции —
не брой surface corrections, уникални грешки или общ package reason count.
[Квалификацията](packaging/RELEASE-CANDIDATE.md#какво-означават-residual10-корекциите)
разделя direct attestation и class/register inference, включително
`пребъда`/`пребил`. Full Legacy language, all-variant morphology, productive
precision, analyzers и corpus acceptance остават отделни; historical failures
и original RAW census не се пренаписват. Preview.3 и планираният preview.4 не затварят M1–M4 или corpus
acceptance. Запазените 13 historical skips са отделни opt-in scopes без нов
acceptance credit. Няма общ claim за непроменен ресурсен разход.

Кодът и авторските езикови данни са под [MIT](LICENSE).
Външните зависимости запазват [своите лицензи](THIRD-PARTY-NOTICES.md).
