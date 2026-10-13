> **Публичен preview `v0.1.0-preview.1`.** Legacy паритетът и корпусната проверка още не са завършени. Вижте [входове, готов пакет и ограничения](PREVIEW.md) и [синхронизацията](PUBLIC-SYNC.md).

# BgLang

Български езиков API за речниково търсене, словоформи, анализ на думи и текст,
словообразувателни кандидати и езикови диагностики. Публичният вход е
`BulgarianLanguageSession.Open(manifestPath)`; пакетът се проверява по размер,
SHA-256, структура и идентичност преди използване.

## Изграждане и проверка

Нужни са Windows, PowerShell 7 и .NET SDK **10.0.401**. Публичната рецепта проверява
точната версия. `BgLang.Public.slnx` съдържа API, Contracts, Core и малките публични
тестове; тестовете имат само пряка зависимост към API. Offline производителят и
примерът се изграждат с отделни команди.

[Input lock](data/dictionary/public-input-lock.v1.json) фиксира трите файла на
Corrected151Stored, текущия RAW граф и очаквания пакет
`bglang-neutral:9b1115996e1790af`. В подготвената локална дистрибуция входовете са в
`public-inputs/corrected151-stored-v1`. При checkout на кода доставете същите три
файла отделно и задайте `-InputDirectory`; имената, размерите и SHA са в lock-а.

```powershell
$run = Join-Path $env:TEMP ('bglang-public-' + [Guid]::NewGuid().ToString('N'))
$inputs = './public-inputs/corrected151-stored-v1'
./packaging/Build-PublicPackage.ps1 -InputDirectory $inputs `
    -OutputDirectory (Join-Path $run 'native-a') -Test `
    -ResultsDirectory (Join-Path $run 'results-a')
./packaging/Build-PublicPackage.ps1 -InputDirectory $inputs `
    -OutputDirectory (Join-Path $run 'native-b') -Test `
    -ResultsDirectory (Join-Path $run 'results-b') -CompareWith (Join-Path $run 'native-a')
```

Рецептата използва съществуващия `SourceBuilder/promote-native`, проверява целите
три файлови набора и сравнява всички RAW изходни bytes, включително manifest-а.
Изходната директория трябва да е нова. MSTest зависимостите са фиксирани чрез
`packages.lock.json` и locked restore. Публичните тестове изискват целия точен
пакет; липсващ вход е FAIL, без SKIP.

## Използване на API

Реалният [пример](examples/BgLang.PublicExample/Program.cs) има само project reference
към `BgLang.Api` и използва избрания пълен пакет:

```powershell
dotnet run --project examples/BgLang.PublicExample/BgLang.PublicExample.csproj `
    -c Release -- path/to/package/manifest.json
```

```csharp
using BgLang.Api;
using BgLang.Contracts;

using var language = BulgarianLanguageSession.Open(manifestPath);
foreach (WordReading reading in language.Lookup("книгата", LookupMode.Exact, 16))
    Console.WriteLine($"{reading.Form}\t{reading.Lemma}\t{reading.PartOfSpeech}");
foreach (WordForm form in language.GenerateForms("книга", 256))
    Console.WriteLine($"{form.Form}\t{form.Tag}");
var text = language.AnalyzeText("Книгата е на масата.");
```

Таговете, типизираните features и произходът остават достъпни в резултатите.
Словообразувателните и морфологичните кандидати могат да са хипотези. Проверявайте
incomplete, clipped и work/evidence flags от `AnalyzeWord`; примерът ги отпечатва.
CancellationToken overload-ите позволяват cooperative cancellation.
`Open` материализира пакета и създава lookup индексите в паметта; размерът на
съхранения source не е обещание за RAM или време за стартиране.

## Дистрибуция и CI

[Source allowlist](packaging/public-source-allowlist.v1.json) изброява публичния
evaluated Compile/Content/None/reference обхват и нужните fixtures. Локалните
export директории съдържат `public-source-manifest.json` с точните RAW hashes.

[Windows CI](.github/workflows/windows-public.yml) се стартира ръчно с HTTPS
директория за трите lock-нати файла. Изпълнява същата рецепта, два package builds,
публичните тестове и примера. Адресът за публична доставка и изпълнението в GitHub
се установяват при публикацията; локалната проверка не означава публикуван release.

Вътрешните исторически M4/G5 и reference/candidate проверки са отделни от
публичния target. Техните package identities и независимият stored oracle се
запазват; този packaging обхват не добавя linguistic coverage.

Кодът и авторските езикови данни са под [MIT](LICENSE).
Външните зависимости запазват своите [лицензи](THIRD-PARTY-NOTICES.md).
