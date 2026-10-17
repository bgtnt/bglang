# Локален release candidate: residual10-71e504e

Статус: подготвя се за независим review. Публична версия/tag, destination и
input URL още не са одобрени; няма публикация или hosted CI claim.

## Точен source и package contract

Runtime и offline producer source са приетият commit
`71e504e56bb3be98647ccc3a40cb90cf48079630`. Текущите recipe/docs промени са
отделен authoring snapshot; exact RAW файловете се фиксират в
`public-source-manifest.json`, а не се приписват обратно на този commit.
[Input lock](../data/dictionary/public-input-lock.v1.json) фиксира всички component
hashes. [Source allowlist](public-source-allowlist.v1.json) определя export-а.

| Роля | Package ID | Source ID | Пълни bytes |
|---|---|---|---:|
| Build input: corrected STORED | `bglang-neutral:ef0b0091489cd411` | `neutral-source-v3:5a7b8c887e787bc5301099ca26cda97e4385021e5c88980e5f03601a527960eb` | 269108737 |
| Runtime output: native | `bglang-neutral:fa4b0d8a76e8b7a2` | `neutral-source-v3:85bcb55c05b1b4b0405bb9c77769c04366764ee3c9cb708dde4ef846e3551a64` | 49433035 |

Всеки пакет има точно `manifest.json`, `lexicon.v3.jsonl`, `word-formation.v0.json`.
Current source graph е 2102275 bytes / SHA-256
`ceddc256958e300a523962b6bbf27ba0430354e3c8b1a949050503e459999025`.
Native пакетът е 119290 owners / 1549261 readings: 106552 native owners и
1533593 generated readings, плюс 12738 retained owners / 15668 stored readings.
Това са finite dictionary inventories, не enumeration на всички productive paths.

## Предложени локални assets

- `bglang-source-residual10-71e504e.zip`: exact allowlisted source, трите build
  inputs под `public-inputs/residual10-corrected-stored-v1` и RAW manifest.
- `bglang-native-fa4b0d8a76e8b7a2.zip`: трите готови runtime файла.
- `bglang-inputs-ef0b0091489cd411.zip`: същите три corrected STORED build inputs.
- `candidate.json` и `SHA256SUMS`: exact identities, asset bytes/hashes и изрично
  непопълнените publication решения. За CI могат да се доставят трите raw input
  assets в една одобрена HTTPS директория.

ZIP bytes и SHA се определят от готовите assets; не се ползват старите preview
hashes. Build input не се заменя с native output. Няма private path fallback.

## Възпроизвеждане

Windows, PowerShell 7, .NET SDK10.0.402. Task TEMP/TMP се задават към избраното
локално scratch място; workstation drive letters не са product/CI изискване.
Публичният workflow използва `RUNNER_TEMP`.

От source bundle:

```powershell
$ErrorActionPreference = 'Stop'
$work = Join-Path $env:TEMP ('bglang-' + [Guid]::NewGuid().ToString('N'))
./packaging/Build-PublicPackage.ps1 -OutputDirectory (Join-Path $work 'native') `
    -Test -ResultsDirectory (Join-Path $work 'results')
```

За сравнение с разархивиран exact runtime задайте `-CompareWith` към неговата
нова директория. Рецептата сравнява всичките RAW bytes на трите файла.
`OutputDirectory` трябва да е нова; липсващ input, SHA/file-set mismatch и
непълен пакет са FAIL. Locked NuGet restore ползва съществуващите версии от
`tests/BgLang.Public.Tests/packages.lock.json`.

За API-only tests върху готовия runtime:

```powershell
$env:BGLANG_PUBLIC_PACKAGE_MANIFEST = (Resolve-Path './runtime/manifest.json').Path
dotnet restore BgLang.Public.slnx --locked-mode
dotnet build BgLang.Public.slnx -c Release --no-restore
dotnet test tests/BgLang.Public.Tests/BgLang.Public.Tests.csproj -c Release `
    --no-build --no-restore
```

За реалния example след build:

```powershell
dotnet build examples/BgLang.PublicExample/BgLang.PublicExample.csproj -c Release
dotnet run --project examples/BgLang.PublicExample/BgLang.PublicExample.csproj `
    -c Release --no-build --no-restore -- $env:BGLANG_PUBLIC_PACKAGE_MANIFEST --json
```

Всеки direct `dotnet` command се проверява чрез `$LASTEXITCODE`; build recipe
прави това fail-fast. Примерът приема още `--measure` и `--text-file <utf8.txt>`.

## Решения преди публикация

Готовият candidate трябва да се свърже с одобрени public destination и version/tag,
както и доставка на current inputs. Съществуващото repository `bgtnt/bglang` е
наличен вариант, не разрешение за нов push/release. `input_base_url` или repository
variable `BGLANG_INPUT_BASE_URL` трябва да сочат exact current input assets;
липсваща настройка е явен FAIL, без fallback към `v0.1.0-preview.1`.
Локална validation и source export не са hosted CI или publication evidence.

## Ограничения

Current finite residual0 е приет в ограничен dictionary/register scope. Original
RAW225unfinished+13reviewed, broader productive precision, graph corpus tests и
останалите analyzers са отделни. Няма нов overall language/M2/analyzer/corpus или
release acceptance credit. API exposes limit/incompleteness flags и conditional
requirements; examples не ги превръщат в сигурни граматични заключения.

MIT scope и [third-party notices](../THIRD-PARTY-NOTICES.md) са запазени от
приетия packaging contract; не се vendor-ват SDK или NuGet binaries.
