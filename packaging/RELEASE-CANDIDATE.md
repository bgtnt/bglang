# Candidate v0.1.0-preview.4

`v0.1.0-preview.4` е планираният следващ candidate label. Приетият local
implementation base е `6d3672173cae1688dc36612d1dffc2a49366c7f0`, native output
`bglang-neutral:de06b9b2e254e61a`. Metadata commit, immutable RAW source export,
distribution validation, CI и publication за кандидата предстоят; техни hashes,
source/Compile counts или PASS резултати не са обявени тук.

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
запазен historical runtime/producer anchor, не бъдещият metadata/export commit.
Historical contract/status,
allowlist anchor и pending/publication=false snapshots остават история и не
се използват сами по себе си като доказателство за текуща публикация.

## Първо използвайте runtime ZIP

За незабавно използване вземете runtime ZIP, `SHA256SUMS` и source bundle от
публикувания preview.3. За preview.4 download указанията ще са приложими след
неговата публикация. Проверете hash и разархивирайте трите data файла в
`./runtime`. Със source checkout/bundle от същия preview, Windows, PowerShell 7
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

Това е отделен offline workflow със source/recipe от съответния immutable
export и complete corrected STORED inputs. Следващите identities са приетият
local target за preview.4; нов export/distribution run още не е изпълнен.
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

За новия distribution run Public37 е очакване, не вече получен резултат.
Приетите Release2/Focus48P/Public37P/Regressions265P/Full3314P с0F,
13 historical NotExecuted и42 package gates са implementation evidence за
`6d367217`/`de06`, отделно от предстоящата проверка на exported source.
Настоящата metadata подготовка променя само README и този guide; това не е
твърдение за нулев Product delta спрямо preview.3.

[Source allowlist](public-source-allowlist.v1.json)/RAW manifest определят
конкретния export. Source/Compile counts се установяват от текущия allowlist
и evaluated project closure, а actual export се приема по новия RAW manifest.
Не пренасяйте старите preview.3 counts/резултати върху кандидата.
Ползвайте source/recipe и locked inputs от същия preview;
preview.2 STORED assets не съвпадат с текущия lock. Missing component, hash
mismatch или непълен export не се заместват с private/стар input.

## CI и публикация

[Windows workflow](../.github/workflows/windows-public.yml) приема explicit
`input_base_url`/`BGLANG_INPUT_BASE_URL` за трите raw STORED assets.
Публикуваният raw input base за preview.3 е
`https://github.com/bgtnt/bglang/releases/download/v0.1.0-preview.3`;
добавя се името на locked STORED asset. За preview.4 се изисква отделно
обвързване на input delivery/base URL с exact lock/asset hashes; нов URL не е
предварително удостоверен. Това не е runtime ZIP URL;
missing URL/hash mismatch е FAIL без preview.1 fallback.
CI evidence се отнася до exact public SHA/run, отделно от local checks и
release assets. Publication сама по себе си не доказва green CI/corpus result.
Проверявайте actual public SHA/run и asset hashes в release metadata;
публикационният snapshot anchor не е автоматично public repository commit.

## Езикови промени и граници

Приетото допълнение за планирания preview.4 допуска exact adverb `късно`
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
