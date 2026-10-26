# BgLang

BgLang е локален C# API за български речникови форми, морфология, анализ на текст и квалифицирано evidence за ударение. Console примерът и optional HTTP host използват същото ядро и проверен runtime пакет.

## Preview9 — 26 октомври 2026

**[v0.1.0-preview.9](https://github.com/bgtnt/bglang/releases/tag/v0.1.0-preview.9)** доставя Live whole-text редактор, graph→context→syntax, coherent nominal head+finite predicate и exact finite/Noun/Auto продължения. Запазва речника, анализаторите, stress API, NFC `ѐ/Ѐ` и всичките 12 спомагателни прочита. Runtime data identity остава непроменена.

Release страницата посочва точния public source commit/tag, активите и окончателните CI/readback резултати. Прикачените `publication.json`, `validation.json` и `SHA256SUMS` описват съответните identities и проверки. [Release guide](packaging/RELEASE-CANDIDATE.md) служи за възпроизвеждане; source/runtime публикацията не означава public hosting.

## Бърз старт с Preview9

Нужен е .NET 10 SDK, съвместим с `global.json` от същия source export. Изтеглете [Preview9 source ZIP](https://github.com/bgtnt/bglang/releases/download/v0.1.0-preview.9/bglang-v0.1.0-preview.9-source.zip) и [Preview9 runtime ZIP](https://github.com/bgtnt/bglang/releases/download/v0.1.0-preview.9/bglang-v0.1.0-preview.9-runtime.zip). Разархивирайте source в нова папка, а runtime — в нейната подпапка `runtime`, така че да има `runtime/manifest.json` и останалите три data файла. От source папката:

```powershell
dotnet build examples/BgLang.PublicExample/BgLang.PublicExample.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw 'Example build failed.' }
dotnet run --project examples/BgLang.PublicExample/BgLang.PublicExample.csproj `
    -c Release --no-build --no-restore -- ./runtime/manifest.json --json
if ($LASTEXITCODE -ne 0) { throw 'Example failed.' }
```

API използва готовия complete runtime `bglang-neutral:36b0b69685d6d3e9`, manifest SHA-256 `451e4f1bd85030d3c8d888084412644933ad6831b1314b4ee5106b4cd27963e7`. Authoring input не е нужен за този quickstart. За optional offline възпроизвеждане изтеглете [Preview9 inputs ZIP](https://github.com/bgtnt/bglang/releases/download/v0.1.0-preview.9/bglang-v0.1.0-preview.9-inputs.zip) и следвайте [guide от същия tag](https://github.com/bgtnt/bglang/blob/v0.1.0-preview.9/packaging/RELEASE-CANDIDATE.md). Source, recipe, lock и inputs трябва да са от същия release; разархивирайте inputs отделно в `./authoring-input`. Пример за възпроизвеждане:

```powershell
$ErrorActionPreference = 'Stop'
./packaging/Build-PublicPackage.ps1 `
    -InputDirectory ./authoring-input -OutputDirectory ./artifacts/preview9-runtime `
    -Test -TestWeb -ResultsDirectory ./artifacts/preview9-checks
```

Input/output/results са отделни папки; output/results трябва да са нови. Готовият runtime се използва директно от API, а producer входът е matching authoring input. Командите са указания за потребителя; не са изпълнявани при тази docs-only сверка.

## Доставено в Preview9

Preview9 включва Live whole-text анализа, graph→context→syntax, coherent nominal head+finite predicate и exact finite/Noun/Auto продълженията. [Web README](apps/BgLang.Web/README.md) описва интерфейса, а [release guide](packaging/RELEASE-CANDIDATE.md) — distribution договора и проверките.

Приетият локален browser smoke има ограничен scope: реален IME и изкуствено забавени races не са упражнявани. Preview8 остава отделен [исторически release](https://github.com/bgtnt/bglang/releases/tag/v0.1.0-preview.8), който не включва тези разширения.

| Цел | Доставена способност | Оставащо доказателство или обхват |
| --- | --- | --- |
| Използваем компактен API | Preview9: речник, морфология, Text/Context/Syntax/Validation, owner-bound конструкции и qualified stress evidence; NFC и auxiliary12 регресиите са публикувани. | Писмен знак, Legacy capture, prediction и Unknown имат различна authority; няма обща нормативна точност. |
| Анализ при писане | Live editor използва общ analyze-text inventory, слоеве и наблюдаеми времена. | Public hosting е отделен; измерените случаи не са обща latency гаранция. |
| Продуктивна морфология в контекста | Graph/nominal разширенията свързват `ADJECTIVE* + един noun head + finite predicate` с един coherent head tuple и оригинални refs. | По-общ syntax, coordination, няколко heads и вътрешна пунктуация остават извън този договор. |
| Съгласувани продължения | Finite lane поддържа nominal subject или source-licensed лично местоимение, empty/partial prefix, person/number filtering преди limit и точни речникови форми с preserved readings/caret span/cancellation. Preview9 добавя тесен `ADJECTIVE+`→noun lane и избор `Finite`/`Noun`/`Auto`, описани по-долу. | Общо членуване, продуктивно генериран output и граматика вдясно не са доставени. Dictionary rank не е вероятност за следваща дума. Finite/Noun/Auto са включени в Preview9. |
| Проверимо M1–M4 качество | Конкретните приети/частични договори и пакетни контроли са отделни acceptance obligations; публичните граници са в [release guide](packaging/RELEASE-CANDIDATE.md). | Отворените obligations изискват собствени приложими доказателства. PASS бройки, finite census и corpus обем не затварят Legacy parity, productive precision или глобална точност. |

Публичната навигация е в [Web README](apps/BgLang.Web/README.md) и [release guide](packaging/RELEASE-CANDIDATE.md). Примерите по-долу са обозначени според слоя си; общ runtime identity не прави по-късния API наличен в стария source ZIP.

## API и runtime пакет

Preview9 запазва Lexical, Text, Context, Syntax, Validation, owner-bound constructions и `GetStressEvidence`, `GenerateStressForms`, `GetDerivationalStressEvidence`. Legacy captures, писмени знаци и qualified prototype/derivational predictions пазят различен произход.

Публикуваният runtime пакет е annotated `bglang-neutral:36b0b69685d6d3e9`, schema `bglang-neutral-runtime-package.v2`, с четири data файла: `manifest.json`, `lexicon.v3.jsonl`, `word-formation.v0.json` и `lexeme-annotations.v1.json` — общо **178 704 996 RAW data bytes**. ZIP размерът, .NET binaries и RAM са отделни величини.

Preview8 запазва 12 спомагателни прочита на „бъда“ за 10 форми от сериите „бих“ и „бидох“ и включва поправката за NFC `ѐ/Ѐ` с Public/HTTP Unicode регресиите. Четирите runtime data файла остават byte-identical с приетия пакет. Exact form/lemma/full-tag/owner наличието не означава автоматичен contextual избор или ново captured ударение.

За offline възпроизвеждане използвайте [guide от Preview9 tag](https://github.com/bgtnt/bglang/blob/v0.1.0-preview.9/packaging/RELEASE-CANDIDATE.md) и lock/inputs от същия source export. Липса или mismatch е FAIL без private fallback.

### C# API и optional анализи

**Базов пример за Preview9.** Owner-bound construction и Text opt-ins са част от API; graph и continuation договорите са описани отделно.

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


### Graph→context→syntax в Preview9

Следващите два абзаца описват graph bridge и nominal разширението, включени в Preview9.

Default graph→context→syntax запазва двусловните nominal/pronominal Subject+finite Predicate клаузи и добавя затворено `ADJECTIVE* + един noun head + финален finite predicate`. Всички определения и сказуемото се обвързват с един и същ head tuple, без word-count cap или комбинации от отделни modifier alternatives. Сравняват се number и singular gender; plural gender може да липсва, а article equality не е условие. `Суперхубав супертрактор работи.` дава Attribute, Subject и Predicate. Expanded lane е `productive_nominal_phrase_graph.v1`; несъгласуването attribute–head е `productive_attribute_agreement_mismatch`, отделно от `productive_morphology_agreement_mismatch` за subject–predicate. Няма автоматична поправка или обща grammatical verdict.

`GraphReadings` сочи оригиналните token/candidate/full-tag индекси; `Readings`, `IsKnown` и complete exact inventory запазват значението си. `GraphExactRootCandidatesComplete` удостоверява само точния root обхват, дори когато условната опашка остава глобално truncated. Неизмерени, противоречиви и условни морфологични хипотези водят до abstain. Съвместим preview със запазени graph alternatives не е избран прочит. Context/Syntax/Validation остават `bounded-context.v2`, `bounded-syntax-roles.v2`, `bounded-validation.v2`. Вътрешна пунктуация, няколко noun heads, coordination и open input са извън новата затворена структура; това не е full syntax или next-word prediction.

### Owner-bound конструкции в Preview9

`WordConstructions` е nullable петнадесето Text поле. Disabled е `null` и не придобива owner forms. Enabled представя candidate evidence от действителните complete exact readings; `Complete`, owner status и unavailable token/reading references показват ограниченията. Запазват се raw duplicates, aliases, families/targets и отделните occurrences. Text съвпаденията изискват поне две части, реално whitespace и едно sentence. Едночастните кандидати са query-only. `AcceptedWordForm=false`, `GrammarAuthority=false` и `ProductiveReplayAvailable=false` с `OriginalGenerationSeedNotRetained` не дават lexical compatibility или corpus attestation.

### Console и HTTP

**Примери за Preview9.** [Web guide](apps/BgLang.Web/README.md) описва Live, graph и continuation поведенията от същия source export.

Съществуващият CLI е само `<manifest.json> [--json] [--measure] [--text-file <utf8.txt>]`. Няма CLI Context/construction flags. Примерът извиква и трите stress метода, с exact selected owner, display-only evidence, писмен знак, Unknown и unavailable prototype. JSON checker пази тези разграничения и проверява, че prediction не добавя речников запис. Source projection добавя всички 15 Text fields към съществуващия JSON wrapper; result полетата са PascalCase, а outer `method/input/result` са lowercase. Default JSON не доказва opt-in изпълнение. Exact exported projection е проверена върху съответния export/runtime; hosted резултатите са отделно посочени в release страницата.

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
$env:BgLang__ManifestSha256 = '451e4f1bd85030d3c8d888084412644933ad6831b1314b4ee5106b4cd27963e7'
dotnet run --project apps/BgLang.Web/BgLang.Web.csproj -c Release
```

Host default е `http://127.0.0.1:5070`. Примерен construction input е `{"text":"<точният EntryId от lookup-lexical-entries>"}`; за Text opt-in: `{"text":"😀 писал съм.","context":{"includeWordConstructions":true}}` към `/api/v2/analyze-text`.


## Исторически Preview8 — 23 октомври 2026

**[v0.1.0-preview.8](https://github.com/bgtnt/bglang/releases/tag/v0.1.0-preview.8)** е публикуван prerelease от 23 октомври 2026, 15:08:57 UTC, с 11 прикачени assets. Официалната release страница посочва public source commit `29fc3000822534cb185bc285e93cd27f9bebeb3d`. Версията включва поправката на compound diagnostic за NFC `ѐ/Ѐ`, Public/HTTP Unicode регресии и публичния тест за всичките 12 спомагателни прочита. Съществуващите анализатори и stress API се запазват.

Публикацията е потвърдена по metadata на този конкретен release. Hosted CI и окончателният remote readback са отделни доказателства, оставени pending в release body; тази документационна сверка не ги удостоверява, не проверява binary hashes и не обявява „най-нов release“. Прикачените validation/publication разписки и [tag-pinned guide](https://github.com/bgtnt/bglang/blob/v0.1.0-preview.8/packaging/RELEASE-CANDIDATE.md) са исторически snapshots от подготовката, дори когато съдържат pending flags. Guide-ът служи за възпроизвеждане; текущият статус на публикацията е в release страницата.

## Исторически Preview7 — 23 октомври 2026

**[v0.1.0-preview.7](https://github.com/bgtnt/bglang/releases/tag/v0.1.0-preview.7)** е публикуван: public source commit `8a4dfaf3776998aa7d604579b2a9d7acf12bbb13`, 11 проверени release assets и успешни [main CI](https://github.com/bgtnt/bglang/actions/runs/39925829479) и [exact-tag CI](https://github.com/bgtnt/bglang/actions/runs/39925831495). Съдържа 12 спомагателни прочита на „бъда“ и четирите приети combining-mark validation корекции; валидните омографи са запазени. Поправката `db08f1494` за NFC `ѐ/Ѐ` и новите публични регресии не са включени в тази Preview7 публикация; те вече са публикувани в Preview8.

Публикуваният Preview7 използва complete runtime `bglang-neutral:36b0b69685d6d3e9`, manifest SHA-256 `451e4f1bd85030d3c8d888084412644933ad6831b1314b4ee5106b4cd27963e7`. [Release guide от Preview7](https://github.com/bgtnt/bglang/blob/v0.1.0-preview.7/packaging/RELEASE-CANDIDATE.md) описва matching authoring input и локалното A/B възпроизвеждане. След осигуряване на точния input в `./authoring-input`:

```powershell
$ErrorActionPreference = 'Stop'
./packaging/Build-PublicPackage.ps1 `
    -InputDirectory ./authoring-input -OutputDirectory ./artifacts/preview7-runtime `
    -Test -TestWeb -ResultsDirectory ./artifacts/preview7-checks
dotnet run --project examples/BgLang.PublicExample/BgLang.PublicExample.csproj `
    -c Release --no-build --no-restore -- ./artifacts/preview7-runtime/manifest.json --json
if ($LASTEXITCODE -ne 0) { throw 'Example failed.' }
```

Input/output/results папките са отделни; output/results трябва да са нови. Командите са за възпроизвеждане с matching Preview7 source/input набор; публикуваният runtime може да се използва директно. Legacy captures, InputObservation, Inferred predictions и Unknown запазват различния си произход. Липсващ capture не става сигурно ударение.


### Исторически Preview6 — validation и release identity

Preview.6 е публикуван с 11 assets, възпроизведен пълен пакет и проверени public consumer/console/HTTP входове. Main и exact-tag CI са успешни: [main run](https://github.com/bgtnt/bglang/actions/runs/39830165466) и [tag run](https://github.com/bgtnt/bglang/actions/runs/39830166832). Приетият runtime baseline има 4047 Passed / 0 Failed / 13 exact historical NotExecuted и 45 mandatory package gate sets; тези historical skips не получават completion credit. SourceOnly не включва authoring inputs или runtime; за възпроизвеждане са нужни съответстващите source, recipe и locked authoring inputs от същия release.

Windows workflow изпълнява consumer validation с `contents: read` за всички branches (`branches: ['**']`), PR и manual dispatch. Публикуваният preview.6 е проверен през main push CI и explicit dispatch на точния tag с matching authoring input и `test_web=true`. Workflow използва HTTPS URL за locked authoring asset. Release assets съдържат source export, runtime, inputs и checksums; public readback е приключил. Historical producer anchors остават отделни от public commit identity.

---

## Исторически публикуван preview.4

Следващите инструкции, identities, source/lock/workflow references и validation са за immutable preview.4 checkout. Командите му се изпълняват само с неговия source/recipe/input набор. За Preview9 използвайте quickstart по-горе.


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

## Контекстни finite речникови продължения

**Локална доставка след Preview8, commit `3c935f93`.** Публичният договор е в [Web README](apps/BgLang.Web/README.md). Този opt-in не е API пример за Preview8 source ZIP.

`AnalyzeText` приема opt-in `TextContextOptions.Continuation = new TextContinuationRequest(caret, limit)`. Индексът е в оригиналния UTF-16 текст и трябва да е grapheme граница; текущият публичен bound е1–256, default12. Без тази опция `Continuation` липсва от JSON и старите Context/Syntax версии се запазват.

След завършен `ADJECTIVE* + NOUN` или source-licensed лично местоимение, например `Суперхубав супертрактор ` и `Ти раб`, `finite_continuation.v1` връща подкрепени finite интерпретации и реални форми от същия точен пакетен речник. Графът поддържа непознатите context думи, без да променя техните `IsKnown`/exact readings. Един noun tuple трябва да поддържа всички определения; няма нов word-count cap. Предложенията не са вероятност за следваща дума или пълен граматичен разбор.

Морфологията се филтрира преди лимита за различни normalized exact keys; показаната форма/score идва от най-добрия съвместим оригинален прочит. Всяко предложение запазва всички exact `Readings` и отделни `Matches` с оригиналните reading/expectation индекси. За `работи` noun Third/Singular допуска редове3/4, а `Ти` допуска finite ред2; повелителният ред1 остава в payload, но не е finite support. Dictionary score е детерминистичен речников ред, не семантична вероятност.

След whitespace replacement span е с дължина0; при частична дума той покрива целия оригинален token, включително suffix вдясно от caret. `Available`, `UnavailableReason`, `Truncated` и `SearchComplete` разграничават контекстна наличност, output limit и изчерпаност само за обявения finite exact-vocabulary обхват. Cancellation не връща complete-empty резултат. Общо членуване и граматика вдясно от caret са отложени; article/construction authority остава exact-only.

### Локално Noun/Auto продължение — текущ source

Текущият локален source добавя `TextContinuationTarget.Finite`, `Noun` и `Auto` чрез init-only `TextContinuationRequest.Target`. Старият constructor/deconstruction и API default остават `Finite`: `new TextContinuationRequest(caret, limit)` продължава да заявява същия finite lane. Например `new TextContinuationRequest(caret, limit) { Target = TextContinuationTarget.Noun }` заявява noun lane. Noun/Auto договорът е приет и локално commit-нат в `44bb169b`; отделната Preview9 distribution подготовка не го приписва на Preview8 и не удостоверява публикация.

`Noun` изисква поне едно завършено определение (`ADJECTIVE+`) в същия whitespace-only ляв sentence fragment, с празен или частичен noun prefix. Един coherent `Number` и singular `Gender` tuple трябва да бъде подкрепен от всички определения; при plural не се измисля липсващ gender. Първото определение може да запази source-licensed `Indefinite` или `Definite`, а `Full`/`Short` са допустими само за masculine singular. `Definite` не се преименува на `Full` или `Short`. Следващите определения и точният common-noun кандидат трябва да са `Indefinite`. Count, неподдържани Case/short-form markers и неизвестни или противоречиви axes не установяват приложимост. Това е локална съвместимост, без inferred Subject, общо правило за членуване или проверка на граматиката вдясно.

`Auto` избира лицензирания finite контекст преди dictionary discovery; само при неподдържан finite контекст може да избере подкрепен noun контекст. Нулеви резултати, недостъпен source, грешка, truncation или cancellation след този избор не превключват към друг lane. Изричният `Noun` може да покаже adjective интерпретации, които finite-first `Auto` не е избрал. Приоритетът не доказва единствен правилен прочит.

Noun отговорът е с действителен `Target=Noun`, версия `nominal_continuation.v1` и source `exact_package_nominal_vocabulary`; `Expectations` е празен, а `NominalExpectations` съдържа `Number`, nullable `Gender`, `FirstAttributeArticle`, `NounArticle` и оригиналните `Attributes` refs. Това не добавя anchored Attribute роли към Context. `Matches.ExpectationIndices` сочи само активния nominal списък; пълните оригинални exact readings, дубликати и nonmatching homographs се запазват. Finite version/source/Expectations се запазват, а default `Target` и null `NominalExpectations` се пропускат от JSON. Live използва „Авто“ по подразбиране и selector „Авто / Глаголи / Съществителни“; API default остава `Finite`.
