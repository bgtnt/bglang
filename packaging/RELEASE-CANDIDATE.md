# v0.1.0-preview.8 — локална подготовка на source/runtime distribution

**Preview8 е локален кандидат; export/recipe/consumer/archive проверките и независимото packaging ревю предстоят.** Няма Preview8 public source/tag, hosted CI или публикувани assets; `accepted=false`, `published=false` за тази distribution партида. [Публикуваният Preview7](https://github.com/bgtnt/bglang/releases/tag/v0.1.0-preview.7) остава immutable: public source/tag `8a4dfaf3776998aa7d604579b2a9d7acf12bbb13`, 11 assets и неговите успешни CI са отделна завършена публикация. Нови download URLs не се обещават. Историческият preview.4 guide по-долу е запазен.

## Текущ договор и package identity

Приетият private implementation commit е `db08f149472bb4666310ccd95c6b840b575bc8af`. Той добавя поправката за compound predicate при NFC `ѐ/Ѐ` и Public/HTTP Unicode и literal auxiliary12 регресиите върху приетия Preview7 runtime. Lexical/Text, bounded Context/Syntax/Validation, owner-bound constructions, трите stress операции, дванадесетте спомагателни прочита и четирите combining-mark корекции се запазват. Runtime producer anchor `dd547b56a41b6f4ba01a4fea01829d90cf5bdb9a` в lock файла има отделна роля; data bytes не се променят. Captured Legacy evidence, писмените знаци и квалифицираните predictions пазят отделен произход. Няма обща нормативна гаранция за непозната дума, NER/parser/semantic accuracy, Animacy/Transitivity validation или corpus completion claim.

Целевият пълен runtime `bglang-neutral:36b0b69685d6d3e9` има schema `bglang-neutral-runtime-package.v2` и точно четири компонента:

| Файл | RAW B | SHA-256 |
| --- | ---: | --- |
| `manifest.json` | 797 | `451e4f1bd85030d3c8d888084412644933ad6831b1314b4ee5106b4cd27963e7` |
| `lexicon.v3.jsonl` | 47 335 883 | `c4d08bfe1553a35a18e81d2644d1551b63a8c40691bb13a36b2211f946483366` |
| `word-formation.v0.json` | 2 109 799 | `0606a10718c0d99c2e61efc33979d9683c4c3e110895657852d3d069cd98a58b` |
| `lexeme-annotations.v1.json` | 129 258 517 | `a9334305068d9f476d48f50b0f80e7ff9518666fa3b26fcbf753b01e4ff4c8d5` |

Общите data bytes са 178 704 996. Това са RAW file sizes, без ZIP/.NET/RAM. Runtime source ID е `neutral-source-v3:7e43ca45a13e2b3ef2948d32b7576834e5225e724cb14f26e1474a54db4410c7`; annotations source ID е `neutral-annotations-v4:f8f04b61cc9cf7e8ae03a19aa2b72ec85601689b89a30689f67a4483d8c23865`. Приложенията използват готовия runtime и `BulgarianLanguageSession.Open(manifestPath)`; не е необходимо да изпълняват package producer.

## Възпроизвеждане и SourceOnly

Новият [input lock](../data/dictionary/public-input-lock.v1.json) използва `authoringInput`: един `lexemes.source.json`, 146 429 828 B, SHA-256 `55c79288eca912c08f297a790f73327f768a0c9e9ebd3924b84d81474253046a`, под `public-inputs/preview7-auxiliary-repair-authoring-v5`. Входът е `bglang-neutral-authoring.v5`: sparse stressPattern с required bindingId и accentParadigm. Това е source формат; runtime annotations са v4, а manifest schema остава v2. Current graph е отделно locked в source. Старият `storedInput` остава historical producer provenance и не е default вход за Preview8. Това е exact приетият auxiliary-authoring-2 source; дванадесетте стари captured cells са rebound, без нови captures или Legacy re-import/compaction. Runtime output никога не е build input.

От Full export с включен locked authoring input; sibling `source-only-1` е отделният SourceOnly export, а `authoring-input-1` съдържа само matching `lexemes.source.json`:

```powershell
$ErrorActionPreference = 'Stop'
$validationRoot = Join-Path $env:TEMP ('bglang-preview8-' + [Guid]::NewGuid().ToString('N'))
./packaging/Build-PublicPackage.ps1 `
    -OutputDirectory (Join-Path $validationRoot 'annotated-a') `
    -Test -TestWeb -ResultsDirectory (Join-Path $validationRoot 'checks-a')
$sourceOnlyRoot = (Resolve-Path '../source-only-1').Path
$matchingInput = (Resolve-Path '../authoring-input-1').Path
& (Join-Path $sourceOnlyRoot 'packaging/Build-PublicPackage.ps1') `
    -InputDirectory $matchingInput -OutputDirectory (Join-Path $validationRoot 'annotated-b') `
    -ResultsDirectory (Join-Path $validationRoot 'checks-b') `
    -CompareWith (Join-Path $validationRoot 'annotated-a')
```

При checkout или SourceOnly export подайте `-InputDirectory ./authoring-input` към точния locked authoring набор. `-TestWeb` е optional и изисква `-Test`; без него няма HTTP validation claim. `-CompareWith` сравнява всичките четири output файла. Output/results директориите са fresh и различни от immutable inputs. Missing input, hash mismatch, непълен или допълнителен file set са FAIL без SKIP/private fallback.

`Export-PublicSource.ps1` запазва точния evaluated/allowlisted source closure. Нормалният export копира locked authoring input; `-SourceOnly` го пропуска и не включва готов runtime. SourceOnly е годен кодов export, но изисква външен matching runtime за API/consumer checks и отделния authoring input за reproduction. `public-source-manifest.json` описва exact relative RAW source hashes; не е Git blob/archive identity или доказателство за публикация.

Recipe използва existing SourceBuilder `build` върху authoring entry и current graph. Public/example validation избира same absolute manifest чрез `BGLANG_PUBLIC_PACKAGE_MANIFEST`, `BGLANG_LEXICAL_MANIFEST` и `BGLANG_LEXICAL_REQUIRE_MANIFEST=1`. Задължителните stress checks използват и `BGLANG_STRESS_MANIFEST` плюс `BGLANG_STRESS_REQUIRE_MANIFEST=1`; optional Web checks използват `BGLANG_WEB_TEST_MANIFEST` плюс exact manifest SHA-256. Repair checks избират и `BGLANG_PREVIEW6_REPAIR_MANIFEST` плюс `BGLANG_PREVIEW6_REPAIR_REQUIRE_MANIFEST=1` върху същия manifest. Деветте selectors (при TestWeb) се записват преди Process.Start и се възстановяват с оригиналните byte values и absent/present състояние. HTTP test restore използва съществуващите pinned dependencies; няма ново обещание за locked HTTP restore. Workstation TEMP/TMP и свободният диск се избират от оператора; CI ползва `RUNNER_TEMP`, без фиксирана drive зависимост.

## Console и C#

```powershell
dotnet build examples/BgLang.PublicExample/BgLang.PublicExample.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw 'Example build failed.' }
dotnet run --project examples/BgLang.PublicExample/BgLang.PublicExample.csproj `
    -c Release --no-build --no-restore -- ./runtime/manifest.json `
    --json --measure --text-file ./sample.utf8.txt
if ($LASTEXITCODE -ne 0) { throw 'Example failed.' }
```

CLI е само `<manifest.json> [--json] [--measure] [--text-file <utf8.txt>]`, без Context/construction flags. `--text-file` добавя един AnalyzeText, използва strict UTF-8 и пази original file byte/hash metadata; начален BOM се маха само от API текста. `--measure` добавя отделен stderr measurement footer. Cumulative allocations, phase timers и lifetime peak са различни величини, без общ speedup/RAM сертификат.

Text JSON projection включва 15 полета: Text, Tokens, Sentences, Phonetics, Diagnostics, Lexical, ReadingInventory, Metrics, Spelling, Profile, ProperNames, Context, Syntax, Validation и WordConstructions. Result fields са PascalCase; outer schema/method/input/result остава съществуващият wrapper. Consumer checks сравняват membership, не DTO declaration order. Запазени са старите DiagnosticValue/WordValue enrichments и false/null/empty/unavailable distinctions. Exact exported projection се валидира отделно преди release.

`AnalyzeText(text, includeUnknownContextProperNames, contextOptions, cancellationToken)` има четири задължителни аргумента; старите overloads остават. Петте допълнителни TextContextOptions flags са независими и default false. Core Context/Syntax/Validation се изпълняват по подразбиране с availability/evidence; disabled optional groups/lists и WordConstructions са null. При изпълнена проверка без попадения съответният списък с кандидати или diagnostics е празен. Enabled-empty WordConstructions е evidence object с празни collections. Недостъпното придобиване има отделен статус/причина. Unknown-context ProperNames е отделен opt-in, без общ NER claim.

`CompleteReadingIndex` е в complete resolved reading route: SupportReadings, когато не е null, иначе CanonicalReadings. TokenIndex сочи оригиналния Tokens slot, а ReadingInventory.Occurrences го свързва със Sets. Display prefix не ограничава complete inventory и равните raw записи не се deduplicate-ват. Този индекс е различен от HTTP analysisRef.

## Optional HTTP consumer

```powershell
$env:BgLang__ManifestPath = (Resolve-Path './runtime/manifest.json').Path
$env:BgLang__ManifestSha256 = '451e4f1bd85030d3c8d888084412644933ad6831b1314b4ee5106b4cd27963e7'
dotnet run --project apps/BgLang.Web/BgLang.Web.csproj -c Release
```

Default host е `http://127.0.0.1:5070`, с `/demo` и `/docs`. Host-ът strict-валидира пълния пакет и сверява предварително избрания manifest hash. [Web README](../apps/BgLang.Web/README.md) описва v1/v2 и `raw-v1`, `shared-donors-v1`, `shared-analyses-v1`. Клиентът възстановява shared форматите, преди да сравнява данни. HTTP JSON е camelCase.

`POST /api/v2/word-constructions` и v1 използват `{"text":"<точният opaque EntryId>"}`. Поле `entryId` се отхвърля; ID не се trim-ва, декодира или замества с lemma. Text opt-in е `{"text":"😀 писал съм.","context":{"includeWordConstructions":true}}` към `analyze-text`. Query връща всички accepted owner OwnForms, включително форми без приложим template, и всички приложими Candidates/templates, включително one-part query-only templates. Text свързва само multi-part candidates с exact raw readings, original UTF-16 spans, same sentence и real whitespace. Aliases, target tags, family differences, complete indices и duplicates остават. Кандидатът не е accepted word form/grammar authority/corpus attestation; productive replay остава unavailable поради липсващ original generation seed.

## Stress consumer и граници

`GetStressEvidence("😀 аванпост.", new(1, "legacy-base:64", "аванпост", "Съществително:1,Мъжки"))` запазва exact owner selection и оригиналните UTF-16 offsets. Captured Legacy данните не стават Editorial/ReviewedExact. Display-only evidence остава Inferred; писмен знак е InputObservation, а липсващият capture остава Unknown.

`GenerateStressForms("legacy-base:64", "космоаванпост")` използва изрично избран opaque прототип. Резултатът има qualified fit provenance и Inferred marks; не добавя lemma към Lookup или обикновените GenerateForms. Padding в prototype EntryId не се премахва и дава unavailable/empty forms. `GetDerivationalStressEvidence("играч")` връща prediction върху действителен licensed derivation и named source; не дава forward-generation permission или измислени Legacy координати.

Console JSON включва пет stress заявки и две exact-storage negatives наред с трите оригинални text samples. Literal checker проверява public DTO membership, точни марки/offsets и provenance. Public87/Web145 са предварително фиксираният fresh target за една нормална Full-export recipe с `-Test -TestWeb`; изпълнението още предстои. Новият output се сравнява RAW с приетите четири runtime файла. Предходното A/B възпроизвеждане е historical corroboration след exact dependency binding, а не свеж двоен Preview8 run. SourceOnly recipe може да използва отделния matching authoring input и `-CompareWith`; hosted manual dispatch запазва existing twin Public/Web checks.

HTTP v1/v2 имат `stress`, `generate-stress-forms` и `derivational-stress`. Използвайте заявките от [Web README](../apps/BgLang.Web/README.md); към тези операции не се изпраща limit. SourceBuilder build консумира вече подготвения source. Release recipe не повтаря Legacy import/compaction. Required bindingId се пази при export/edit/build; stale inventory identity е отказ, не разрешение за пренасочване на capture.

CI push е branch-only (`branches: ['**']`); PR и manual dispatch са запазени. При отделно разрешено публикуване hosted проверките ще са main push на точния public commit и explicit workflow_dispatch на exact Preview8 tag с matching input_base_url и test_web=true. Сега няма разрешение за push/tag/release или hosted execution. Нови readiness claims изискват actual run identities и asset readback. [GitHub workflow syntax](https://docs.github.com/en/actions/reference/workflows-and-actions/workflow-syntax#onpushbranchestagsbranches-ignoretags-ignore) определя branch-only семантиката.

## CI, текуща validation и publication

[Windows workflow](../.github/workflows/windows-public.yml) остава validation job с `contents: read`. Нужен е HTTPS `input_base_url` или `BGLANG_INPUT_BASE_URL` за точно `authoringInput.files`, не runtime ZIP и не preview.4 STORED assets. Workflow проверява raw input bytes/hash преди recipe. Push/PR включват HTTP проверките; manual dispatch може да зададе `test_web=false`, при което няма HTTP validation claim. Manual dispatch прави twin build и all-four-file сравнение.

Приетата implementation партида има Full4125P/0F/13 exact historical NotExecuted, 45 mandatory package groups и original13 M1 keys. Това е retained implementation evidence чрез exact source/package bindings; private Full не се повтаря само за packaging. Предварителният distribution target е Full216 и SourceOnly216 allowlisted paths, Public87/Web145 с exact current leaf identities, console3 и една свежа recipe с all-four RAW сравнение с accepted package. Fresh exports/evaluated boundary/archives, независимо packaging ревю и scoped local commit предстоят. Unchanged guards/selectors и старото второ възпроизвеждане се отчитат отделно като retained, след конкретно dependency binding. Public source/tag, hosted CI и asset readback за Preview8 не са извършени.

Private implementation, historical producer anchors, final validated export, public source commit/tag и runtime/archive identities имат различни роли. Запазете public history и старите immutable tags/assets; не push-вайте private implementation branch като public payload. Съществуващият workflow не създава release или assets. Publication transport, raw input availability и mutation order се фиксират в отделния разрешен publisher handoff, без нова publication архитектура.

Точните release identities са в future actual `publication.json`, `SHA256SUMS` и `public-source-manifest.json`. До actual readback новата версия остава подготовка. Historical failures/skips и numeric allocation FALSE не се пренаписват; няма нов numeric ceiling или resource-neutral claim. Legacy/fitted provenance не дава normative authority; Animacy/Transitivity, full corpus/M1–M4 и обща linguistic accuracy остават отделни граници.

---

## Исторически guide за публикувания preview.4

Следващият запазен текст описва единствено immutable preview.4 source/recipe/runtime и тогавашните validation резултати. Неговите relative source/lock/workflow references и команди се отнасят до exact preview.4 checkout, а не до текущия preview.5 lock. Историята не е нов release-acceptance claim.

# Публикуван v0.1.0-preview.4

**[Изтегли preview 4](https://github.com/bgtnt/bglang/releases/tag/v0.1.0-preview.4)**

Публичният prerelease е публикуван в `2026-10-19T16:54:23Z`; неговият
public source/tag snapshot е `06d2a2d8461aed0131edceff71ac7fce6e236b63`.
Приетият local implementation base `6d3672173cae1688dc36612d1dffc2a49366c7f0`
и local documentation metadata commit `41c88c16fe84e6434f6ffdf343e49f16d113922e`
имат отделни роли. Runtime output е `bglang-neutral:de06b9b2e254e61a`.
Exported distribution и CI резултатите по-долу са за immutable release snapshot-а.
Последващи docs commits в `main` не променят tag/source ZIP/RAW manifest
или validation на тези artifacts.

Публикуваният [`v0.1.0-preview.3`](https://github.com/bgtnt/bglang/releases/tag/v0.1.0-preview.3)
остава отделен release с public source/tag
`2b7c30266641302d9168ec3fa80915753b974dd9` и
[преминалия негов Windows CI run](https://github.com/bgtnt/bglang/actions/runs/39209750145).
Неговите source bundle/runtime assets и резултати не приемат автоматично preview.4.
Publication snapshot anchor `ae247f60da2122a2d7a8f925058949dda744a95c` е история
на preview.3. Точните identities са в съответните `publication.json`,
`SHA256SUMS` и `public-source-manifest.json`, а не в бъдещи предполагаеми hashes.

Input lock запазва producer `source.baseCommit=399c166d9aa82355a8599f64a7457348789bb395`.
Това е различен anchor от publication snapshot-а и приетия base `6d367217`.
Allowlist `sourceBaseCommit=308b5a46ac1539ca98498d291ccf94d04543baf1` също е
запазен historical runtime/producer anchor, различен от local metadata и public source/tag commit.
Historical contract/status,
allowlist anchor и pending/publication=false snapshots остават история и не
се използват сами по себе си като доказателство за текуща публикация.

## Първо използвайте runtime ZIP

Вземете runtime ZIP, `SHA256SUMS` и source bundle от свързания по-горе
preview.4. Проверете hash и разархивирайте трите data файла в `./runtime`.
Със source checkout/bundle от същия preview, Windows, PowerShell 7
и .NET SDK **10.0.402**:

```powershell
$ErrorActionPreference = 'Stop'
$manifest = (Resolve-Path './runtime/manifest.json').Path
dotnet build examples/BgLang.PublicExample/BgLang.PublicExample.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw 'Example build failed.' }
dotnet run --project examples/BgLang.PublicExample/BgLang.PublicExample.csproj `
    -c Release --no-build --no-restore -- $manifest
if ($LASTEXITCODE -ne 0) { throw 'Example failed.' }
```

`--json` пази всички резултатни DTO arrays/evidence, original property order,
explicit false/null и diagnostic полета. `--text-file <utf8.txt>` добавя един
`AnalyzeText` след демонстрациите. Strict UTF-8 отказва malformed bytes;
начален BOM се премахва от текста, но file bytes/hash са на оригиналния файл.

## Прочит на console и JSON

`IsKnown` идва от exact readings. `BaseEvidence` е reviewed graph decomposition
evidence. Nonconditional graph paths имат exact-self-reading root; conditional
paths пазят program/base tag, supporting lemmas и unmet requirements.
`Candidates`, `KnownBaseCandidates`, `BaseTypeCandidates` са hypotheses lanes,
не accepted readings. Exact-base authority не прави липсващата форма известна.

Всяка съкратена колекция има `total`, `displayed`, ordered `items`. Display3
е console presentation. Следващите counts/flags са запазен consumer пример
върху предходния native `fa4b0d8a76e8b7a2`, не ново измерване на preview.3 или preview.4.
В този пример `суперработник` е `known=False`:

| Lane | total | displayed |
| --- | ---: | ---: |
| Readings | 0 | 0 |
| GraphCandidates | 17 | 3 |
| Candidates | 5 | 3 |
| KnownBaseCandidates | 0 | 0 |
| BaseTypeCandidates | 2 | 2 |

Първият path има exact root `работник`; вторият е conditional self-root с
`productive.noun.masculine_type14.v0`. Има 1042 donors; displayed са
`авджилък`, `авиомозък`, `авиотрафик`. Unmet requirement е exact self reading
`суперработник` / `Съществително:14,Мъжки`. Нито 1042 donors, нито наблюдаваните
50 paths при друг input са engine caps. KnownBaseCandidates е празен във
валидирания пример; не се измисля положително покритие за този lane.

Всичките седем flags се показват. За същия input:

```json
{"GraphSearchIncomplete":false,"GraphCandidatesTruncated":false,"CandidateSearchLimited":false,"CandidateEvidenceLimited":true,"KnownBaseSearchLimited":false,"BaseTypeSearchLimited":true,"BaseTypeEvidenceLimited":true}
```

Incomplete/limited не установява изчерпателност, truncated различава output
omission, evidence-limited — ограничено donor evidence. Четете flag-а на
съответния lane и при total0; първите три paths не представят всички paths.

Diagnostic projection пази `Code`, `Start`, `Length`, `RuleId`, `ProjectedText`
и example-only `Classification`. Warning е предложение за проверка,
unsupported-check е непокрита проверка, information е сведение/непознат код.
„Аз го четем.“ има agreement warning на `[6,5]`; SVO „Аз четем книга.“ остава
извън текущото покритие. Празен diagnostic списък не удостоверява правилност.

## Граници на измерването

Consumer-ът отделя phase measurements чрез `--measure`; без него няма stderr
measurement footer. За командата подгответе свой strict UTF-8 `sample.utf8.txt`.

```powershell
dotnet run --project examples/BgLang.PublicExample/BgLang.PublicExample.csproj `
    -c Release --no-build --no-restore -- $manifest --json --measure `
    --text-file ./sample.utf8.txt 1>analysis.json 2>measurements.json
if ($LASTEXITCODE -ne 0) { throw 'Measured example failed.' }
```

| Поле/фаза | Точен измерен обхват |
| --- | --- |
| Startup | Approximate process age при managed entry; cumulative managed entry→Open start/complete. Не е чист JIT/OS startup benchmark. |
| API `measurement` | Едно `call()`; `Open` включва strict package load и DAWG construction. Existing snapshot/delta значения са запазени. |
| `measurement.preparation` | Едно JSON `project(result)` или console `summary(result)`, включително вложеното summary JSON formatting. |
| Footer `serialization` | JSON: едно `SerializeToUtf8Bytes` на целия prepared envelope, включително measurement fields. Console: join на вече форматирани редове и UTF-8 encoding. Няма втори API call. |
| `payloadBytes` | Реално произведеният stdout UTF-8 payload, без отделния последен newline. |
| `trailingNewlineBytes` / `outputBytes` | Последният newline и exact сборът за stdout. Stderr footer-ът е изключен, без self-reference. |
| Изключени дейности | Input read/decode, output IO, snapshots, създаване на measurement objects. Console measurement-line formatting е преди финалния join; JSON metadata formatting е вътре във финалната serialization. |

За byte проверка пазете raw stdout; външен shell/logger може да промени
encoding/newlines. Process wall time включва и изключените от фазите дейности.
Allocation deltas са process-wide и могат да включат snapshot overhead/други
allocations. Managed bytes не са RAM; PeakWorkingSet е lifetime high-water,
не самостоятелен phase peak. Console buffering задържа допълнителни данни.
Една session загрява следващите заявки; fresh process не чисти OS cache.
Shared commit/paging/IO влияят на наблюденията. Няма resource threshold,
owner numeric cap или общ speedup claim. Bounded heavy fixture е 64
повторения, 2112 UTF-16 code units /3712 UTF-8 bytes, не `--text-file` лимит.

## Възпроизвеждане на native data

Това е отделен offline workflow със source/recipe от immutable preview.4
export и complete corrected STORED inputs. Следващите identities са на
публикувания preview.4 и приетата exported distribution.
Готовият runtime е достатъчен за API.
[Input lock](../data/dictionary/public-input-lock.v1.json) фиксира component hashes:

| Роля | Package ID | Source ID | Пълни data bytes |
| --- | --- | --- | ---: |
| Corrected STORED build input | `bglang-neutral:6de86ba569c1001a` | `neutral-source-v3:4e6b294e97c5585d4af44ae0c71b5baab338a0cc32c83ad0dd83019482cd18ff` | 269108994 |
| Native runtime output/comparison | `bglang-neutral:de06b9b2e254e61a` | `neutral-source-v3:cd595704188df3b69aa1321901ba74d17293f013351e55701ef87c5b615ed6e9` | 49443153 |

Точно трите компонента са `manifest.json`, `lexicon.v3.jsonl`,
`word-formation.v0.json`. Native има 119290 owners /1549263 readings /
1266121 distinct forms: 106551 native owners с1533555 generated readings и
12739 stored owners с15708 stored readings. ZIP bytes, .NET binaries и RAM
са други обхвати. Native никога не е build input. STORED graph е531796 bytes;
recipe използва отделния locked current graph от2106720 bytes.
За source ZIP с включени locked STORED inputs и RAW source manifest:

```powershell
$work = Join-Path $env:TEMP ('bglang-' + [Guid]::NewGuid().ToString('N'))
./packaging/Build-PublicPackage.ps1 -OutputDirectory (Join-Path $work 'native') `
    -Test -ResultsDirectory (Join-Path $work 'results')
if ($LASTEXITCODE -ne 0) { throw 'Package recipe failed.' }
```

При checkout подайте `-InputDirectory` към exact STORED component директория.
`-CompareWith` сравнява всички RAW output bytes с exact runtime. Нов output
directory е задължителен; missing input/hash/file-set/incomplete package е
FAIL без SKIP/private fallback. CI използва `RUNNER_TEMP`; workstation drive
letters не са product изискване. Locked restore ползва existing test lockfile.

API-only tests върху готов runtime:

```powershell
$env:BGLANG_PUBLIC_PACKAGE_MANIFEST = (Resolve-Path './runtime/manifest.json').Path
dotnet restore BgLang.Public.slnx --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Public restore failed.' }
dotnet build BgLang.Public.slnx -c Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Public build failed.' }
dotnet test tests/BgLang.Public.Tests/BgLang.Public.Tests.csproj -c Release --no-build --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Public package tests failed.' }
```

Приетата exported distribution за preview.4 има **Public37P/0F/0skip**,
**23 mandatory gates, от които7 complete-package**, **три Release builds
с0 warnings/0 errors** и exact RAW съвпадение на трите native файла с `de06`.
Приетите Release2/Focus48P/Public37P/Regressions265P/Full3314P с0F,
13 historical NotExecuted и42 package gates остават отделно implementation evidence
за `6d367217`/`de06`; distribution резултатът не се извежда от тях.

[Source allowlist](public-source-allowlist.v1.json)/RAW manifest определят
immutable export-а. Public source/tag `06d2a2d8461aed0131edceff71ac7fce6e236b63`
и неговият source ZIP/manifest запазват собствените си identities и validation
при последващи docs commits в `main`. Тази поправка е само на README и guide;
не пренася preview.3 counts/резултати и не променя Product/API/dependencies.
Ползвайте source/recipe и locked inputs от същия preview;
preview.2 STORED assets не съвпадат с текущия lock. Missing component, hash
mismatch или непълен export не се заместват с private/стар input.

## CI и публикация

[Windows workflow](../.github/workflows/windows-public.yml) приема explicit
`input_base_url`/`BGLANG_INPUT_BASE_URL` за трите raw STORED assets.
Публикуваният raw input base за preview.4 е
`https://github.com/bgtnt/bglang/releases/download/v0.1.0-preview.4`;
добавя се exact locked STORED asset име. Recipe, lock и трите raw STORED assets
трябва да са от същия preview. Това не е runtime ZIP URL;
missing URL/hash mismatch е FAIL без private/preview.1 fallback или mixing.
[CI run39289839846](https://github.com/bgtnt/bglang/actions/runs/39289839846)
е completed **SUCCESS**, head `06d2a2d8461aed0131edceff71ac7fce6e236b63`,
attempt1, приключил в `2026-10-19T17:00:30Z`.
Това е exact public SHA/run evidence, отделно от local checks и release assets.
Publication и green CI не установяват corpus acceptance или обща accuracy.
Asset hashes се проверяват спрямо release metadata; historical producer/snapshot
anchors не се подменят с public repository commit.

## Езикови промени и граници

Допълнението в публикувания preview.4 допуска exact adverb `късно`
в съществуващия typed degree механизъм за `по-късно` и `най-късно`.
Лексикалните readings и adjective homograph се запазват; изборът на
`BaseEvidence` за `по-късно` може да стане `късно` по съществуващия rank.
Това не е общо разрешение за degree върху всеки adverb или премахване на
ambiguity. BERON пряко класифицира `късно` като наречие; и двете степени са
rule-based inferences по58/114.1, не преки речникови цитати. Коригираното
provenance wording има нова graph/package identity, без нова lexical admission.

От публикувания preview.3 се запазват следните четири приети партиди:

| Партида | Конкретен ефект | Произход и граница |
| --- | --- | --- |
| Imperative typed projection | Simple imperative: `Person=Second`, запазено число, `Tense=null`; RAW tags/readings и допустимите омографи остават. | Пряко corpus-origin; обща projection поправка, не contextual disambiguation. |
| `ела`/`елате` | Две imperative алтернативи на `дойда`; един lexical owner38→40, запазени old38, noun `ела`4, `дойди` и двете `дойдете` readings. | Пряко corpus-origin; един lexical owner, без промяна на shared146a graph. |
| Unicode/diagnostics R1–R4 | Canonical equivalence и RAW spans; exact-owner stress lookup, request-local fuzzy reuse; example classification и изрична граница на agreement coverage. | Отделен review/analysis; няма автоматична source correction или общ resource-neutral claim. |
| Productive root shape | Conditional proof отказва unattested inferred Base с празна U+002D част: начален/краен `-` или `--`. | Отделен review/analysis; exact SELF authority и валидни multiple-hyphen roots остават; не е input-wide spelling veto. |

Corpus наблюденията `22538` affected returned-response usages /`2591` exact
surfaces са само inventory в27 discovery документа, включително омографски
контексти; не са gold imperative errors или глобално качество. `й` е анализиран
без нов lexical/normalization patch. Техническите repairs и невалидните FB2
не са допълнителни езикови поправки.

M1–M4 и общото corpus acceptance остават незатворени. Приетите full-suite
партиди пазят13 historical NotExecuted:12 M4 opt-in audits без
`BGLANG_M4_PACKAGE_DIR` и1 G5 isolation audit без `BGLANG_G5_ISOLATION_ROOT`.
Тези scopes не получават completion credit; запазените failures и snapshots
не се пренаписват. Publication и finite census не доказват full Legacy language,
всички productive комбинации, semantic precision или analyzer completeness.
Ресурсните observations са за точните workloads/host, без общо твърдение за
непроменен RAM, allocations или latency.

## Какво означават residual10 корекциите

Десетте избрани owners имат **449→218 readings**, запазени public lemmas и
четири base-tag корекции. Това не е брой surface corrections, уникални грешки
или общ reason breakdown за целия пакет. Основанията се припокриват:

- Exact current BERON inventories ограничават `бъда`/`мра`, без да обявяват
  всяка omitted historical/copula форма за неграматична.
- Impersonal/reflexive/third-person рамките на `потръсва`, `премрежва`,
  `умъдря`, `заболи` пазят конкретния source/register sense; част от finite
  клетките са named-class inference, не отделно direct attestation.
- Historical owner `пряспичка` пази identity, но singular outputs са
  `преспичка/преспичката`; plural/articles имат class квалификация.
  `нуминозно` има коригиран neuter base и отделен regular-class inference.
- `пребъда` пази девет `пребил…` readings, пряко изброени под exact head в
  pre-2002 Grammar source, с historical/register квалификация. Не са blind
  prefixing на `съм`, не са certified preferred-modern и не премахват
  foreign `пребия` readings. `зра` има отделни poetic/register/class основания.

Не сумирайте тези категории като независими reason counts. Source absence
не е negative proof. Corrected finite residual0 не е full Legacy language,
all-variant morphology, productive/analyzer/corpus acceptance. Original RAW
225unfinished+13reviewed, fixed71139zero и historical failures/skips са отделни.

Unicode проверките пазят RAW UTF-16 slices, canonical `й/и\u0306`,
`ѐ/е\u0300`, `ѝ/и\u0300`, при `и≠ѝ`. Stress proof изисква exact owner/reading
authority; няма универсално stress coverage. Cancellation е на операция/текст
граници; една започнала DAWG операция не се прекъсва отвътре.
MIT scope и [third-party notices](../THIRD-PARTY-NOTICES.md) са запазени;
SDK/NuGet binaries не се vendor-ват.
