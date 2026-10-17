# BgLang

Български езиков API за речниково търсене, словоформи, анализ на думи и текст,
словообразувателни кандидати и езикови диагностики. Публичният вход е
`BulgarianLanguageSession.Open(manifestPath)`. Пакетът се проверява по размер,
SHA-256, структура и идентичност преди използване.

## Текущ локален release candidate

Candidate `residual10-71e504e` използва приетия runtime/producer source commit
`71e504e56bb3be98647ccc3a40cb90cf48079630` и complete native пакет
`bglang-neutral:fa4b0d8a76e8b7a2`. Трите runtime файла са **49 433 035 bytes**;
.NET библиотеките и RAM са отделни величини. Exact source snapshot се определя
от `public-source-manifest.json`, включително текущите packaging/docs промени.
Това е локално подготвен кандидат, а не публикуван tag или hosted CI резултат.

[Candidate guide](packaging/RELEASE-CANDIDATE.md) описва source/runtime/input
assets, точните identities, ограниченията и решенията преди публикация.
[Input lock](data/dictionary/public-input-lock.v1.json) е authority за
current input и output имената, размерите и SHA-256. Старият preview runtime
не е заместител на current candidate или build input.

Нужни са Windows, PowerShell 7 и .NET SDK **10.0.402**. След разархивиране на
подготвения source bundle, който съдържа трите locked build inputs:

```powershell
$ErrorActionPreference = 'Stop'
$run = Join-Path $env:TEMP ('bglang-candidate-' + [Guid]::NewGuid().ToString('N'))
./packaging/Build-PublicPackage.ps1 -OutputDirectory (Join-Path $run 'runtime') `
    -Test -ResultsDirectory (Join-Path $run 'public-tests')
if ($LASTEXITCODE -ne 0) { throw 'Candidate recipe failed.' }
```

Рецептата използва съществуващия `SourceBuilder/promote-native`, изисква exact
complete output, изпълнява API-only публичните тестове и реалния пример.
Липсващ или променен input е FAIL, без SKIP и без private fallback.
При Git checkout inputs се доставят отделно; задайте `-InputDirectory` към
трите файла на **corrected STORED** пакета `ef0b0091489cd411`. Native output
`fa4b0d8a76e8b7a2` никога не се използва като correction/build input.

## Използване на API и примера

Готовият runtime bundle съдържа само `manifest.json`, `lexicon.v3.jsonl` и
`word-formation.v0.json`. След проверка на bundle SHA-256 от `SHA256SUMS`:

```powershell
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
var text = language.AnalyzeText("Аз четем книга.");
```

Примерът показва lookup/forms, known и unknown word analysis, exact и conditional
paths, derivation/decomposition, fuzzy lookup, segmentation и diagnostics.
`--json` запазва целите DTO масиви и explicit false/null fields. `--measure`
добавя действителни startup/API/process observations; те не са RAM/latency
сертификация. `--text-file <utf8.txt>` анализира посочен кратък текст. Limit и
incompleteness flags трябва да се четат заедно с резултатите; hypotheses и
conditional evidence не означават известна речникова дума или приет productive proof.

## Source export и CI

```powershell
./packaging/Export-PublicSource.ps1 -InputDirectory $inputs -OutputDirectory $newExport
```

[Allowlist](packaging/public-source-allowlist.v1.json) задава точните авторски
файлове. Export-ът добавя само трите hash-verified build inputs и RAW source
manifest; не включва Git state, bin/obj, private regression harness или NuGet cache.
`BgLang.Public.slnx` съдържа Contracts/Core/API и публичните API-only tests.
Offline producer и example се изграждат отделно.

[Windows workflow](.github/workflows/windows-public.yml) използва
`workflow_dispatch.input_base_url` или repository variable `BGLANG_INPUT_BASE_URL`.
URL трябва да е HTTPS директория с **трите current locked STORED input assets**.
Няма fallback към стария preview. Не конфигуриран или грешен input е явен FAIL.
Hosted CI за този candidate ще може да се заяви след одобрена доставка; локалните
резултати не се представят като GitHub run. Няма извършена публикация, push или sync.

## Scope и исторически preview

Приетият corrected finite residual е 0 unfinished + 0 reviewed. Това не е
пълно Legacy language, all-variant morphology, natural productive precision,
analyzer или corpus acceptance. Original RAW census и историческите failures
остават отделни. Част от finite inventories са квалифицирани Legacy/register
или named-class inferences; candidate не обещава универсална нормативност.

[Публикуваният `v0.1.0-preview.1`](https://github.com/bgtnt/bglang/releases/tag/v0.1.0-preview.1)
и [старият successful hosted run](https://github.com/bgtnt/bglang/actions/runs/38352105167)
са историческият snapshot `7e3ac814b686b2fa223b54a2b30238a93c4b5274` с runtime
`bglang-neutral:9b1115996e1790af`. Те не удостоверяват current candidate.

Кодът и авторските езикови данни са под [MIT](LICENSE).
Външните зависимости запазват [своите лицензи](THIRD-PARTY-NOTICES.md).
