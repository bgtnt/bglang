# BgLang

**BgLang** е детерминистичен инструментариум за български език за .NET 10 и C# с локален HTTP API и интерактивно уеб демо. Библиотеката предоставя речниково търсене (точни, префиксни и суфиксни форми, словоформи по лема, приблизително търсене), морфологичен анализ и графово словообразуване за непознати думи, квалифицирани данни за лексикално ударение и ограничени контекстни и граматични проверки.

**За кого е предназначен:**
- **.NET / C# разработчици**, които вграждат офлайн, предвидим анализ на български текст и словоформи чрез `BulgarianLanguageSession`.
- **Интегратори на локални услуги**, които предпочитат готов HTTP JSON интерфейс (`POST /api/v2/...` и съвместим `POST /api/v1/...`).
- **Автори на езикови инструменти, лингвисти и изследователи**, които изследват морфологични прочити, графови хипотези за нови думи, словообразуване и ударения.

**Какво включва решението (`BgLang.Public.slnx`):**
- `src/BgLang.Api` и `src/BgLang.Contracts` — публичният вход (`BulgarianLanguageSession.Open`) и договорите за резултати (`TextAnalysis`, `WordAnalysis`, `StressEvidence`).
- `src/BgLang.Core` — ядрото за речниково търсене (DAWG), словообразувателен граф и анализатори на текста.
- `apps/BgLang.Web` — локалният ASP.NET Core хост, уеб демото и справката за API.
- `examples/BgLang.PublicExample` — готов конзолен пример с JSON изход.

## Общо онлайн демо и локално изпълнение

Онлайн демото има един адрес за посетителите от GitHub и MAP.BG. Версията на активната услуга и речниковият пакет се виждат в метаданните на API. Публичният достъп предстои да бъде потвърден при пускането на портала. [Онлайн демо](https://map.bg/bglang) · [Метаданни на API](https://map.bg/bglang/api/v2/).

Изтегленият пакет може да се използва изцяло офлайн. След стартиране на `BgLang.Web` редакторът обработва текста на вашата машина. Отворете `http://127.0.0.1:5070/demo-tree` за демото или `http://127.0.0.1:5070/docs` за справката.

## Изтегляне и подготовка (`v0.2.0-alpha.5`)

Изисква се **.NET SDK 10.0.403** (зададен в [`global.json`](global.json)) и PowerShell 7 за скриптованите стъпки.

1. Изтеглете от страницата на изданието [`v0.2.0-alpha.5`](https://github.com/bgtnt/bglang/releases/tag/v0.2.0-alpha.5) (prerelease):
   - [`bglang-v0.2.0-alpha.5-source.zip`](https://github.com/bgtnt/bglang/releases/download/v0.2.0-alpha.5/bglang-v0.2.0-alpha.5-source.zip) — публичният изходен код;
   - [`bglang-v0.2.0-alpha.5-runtime.zip`](https://github.com/bgtnt/bglang/releases/download/v0.2.0-alpha.5/bglang-v0.2.0-alpha.5-runtime.zip) — готовият пакет с езикови данни (`CurrentGraphStress`, `bglang-neutral:9e9a4f441f679542`);
   - [`SHA256SUMS`](https://github.com/bgtnt/bglang/releases/download/v0.2.0-alpha.5/SHA256SUMS) — контролни суми на архивите.
   - *(По избор)* [`bglang-v0.2.0-alpha.5-inputs.zip`](https://github.com/bgtnt/bglang/releases/download/v0.2.0-alpha.5/bglang-v0.2.0-alpha.5-inputs.zip) е нужен **само** за независимо пълно преизграждане на пакета с данни (вж. [ръководството за възпроизвеждане](packaging/RELEASE-CANDIDATE.md)); за стандартна употреба готовият `runtime.zip` е достатъчен.
2. Проверете SHA-256 контролните суми спрямо `SHA256SUMS`:
   ```powershell
   Get-FileHash bglang-v0.2.0-alpha.5-source.zip, bglang-v0.2.0-alpha.5-runtime.zip -Algorithm SHA256
   ```
3. Разархивирайте `bglang-v0.2.0-alpha.5-source.zip` в нова папка (напр. `bglang`), а четирите файла от `bglang-v0.2.0-alpha.5-runtime.zip` — в нейната подпапка `./runtime`, така че вътре да се намират:
   - `runtime/manifest.json` (SHA-256 `9db1df176daea9b703ebbf42022c7b1b871f2daecaa5d9178e0b788408f2e853`);
   - `runtime/lexicon.v3.jsonl`;
   - `runtime/word-formation.v0.json`;
   - `runtime/lexeme-annotations.v1.json`.

## Стартиране на локалното уеб демо

От корена на разархивираната папка стартирайте локалния HTTP хост `BgLang.Web`:

```powershell
$env:BgLang__ManifestPath = (Resolve-Path './runtime/manifest.json').Path
$env:BgLang__ManifestSha256 = '9db1df176daea9b703ebbf42022c7b1b871f2daecaa5d9178e0b788408f2e853'
dotnet run --project apps/BgLang.Web/BgLang.Web.csproj -c Release
```

След стартиране отворете [локалното демо](http://127.0.0.1:5070/demo-tree). Изданието `v0.2.0-alpha.5` включва общия редактор `/demo-tree`; `/demo` и `/demo-lab` отварят същия редактор. Докато пишете, редакторът оцветява думите и показва връзките и проверките за текущото изречение. Изберете дума в текста или схемата за прочити, форми и предложения за поправка. Можете също да видите текста с ударения, да поискате продължение с Ctrl+Space или да потърсите срещания по лема. В „Много ви моля, успокойте се и ми се доверете.“ можете да изберете „се“ или „ми“ и да проследите връзката към съответния глагол. При „Аз чета и пишем.“ редакторът маркира „пишем“ и обяснява защо формата не се съгласува с „аз“, ако двете сказуеми имат общ подлог. За операциите и форматите вижте [локалната API справка](http://127.0.0.1:5070/docs).

## Бърз старт: конзолен пример и компилация на решението

За да компилирате публичното решение или да изпълните конзолния пример `BgLang.PublicExample` срещу пакета в `./runtime`:

```powershell
dotnet build BgLang.Public.slnx -c Release
dotnet build examples/BgLang.PublicExample/BgLang.PublicExample.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw 'Example build failed.' }
dotnet run --project examples/BgLang.PublicExample/BgLang.PublicExample.csproj -c Release --no-build --no-restore -- ./runtime/manifest.json --json
if ($LASTEXITCODE -ne 0) { throw 'Example failed.' }
```

## Проверен пример за API и очакван резултат

Следният пример демонстрира анализа на текст с несъгласуване и емоджи (2 UTF-16 единици) върху точно това издание.

**C# (`BgLang.Api` + `BgLang.Contracts`):**

```csharp
using BgLang.Api;
using BgLang.Contracts;

using var session = BulgarianLanguageSession.Open("./runtime/manifest.json");
TextAnalysis analysis = session.AnalyzeText("Аз 😀 чете.");
```

**HTTP (`POST http://127.0.0.1:5070/api/v2/analyze-text`):**

```powershell
Invoke-RestMethod -Method Post -Uri 'http://127.0.0.1:5070/api/v2/analyze-text' `
  -ContentType 'application/json; charset=utf-8' `
  -Body '{"text":"Аз 😀 чете."}'
```

**Очаквани ключови полета в отговора:**

```json
{
  "data": {
    "text": "Аз 😀 чете.",
    "diagnostics": [
      {
        "code": "productive_morphology_agreement_mismatch",
        "start": 6,
        "length": 4,
        "ruleId": "clause_fusion_agreement.v1"
      }
    ]
  }
}
```

- `Text` (в HTTP `data.text`) остава точно `"Аз 😀 чете."` — ядрото никога не пренаписва входния текст автоматично.
- `Diagnostics` (в HTTP `data.diagnostics`) връща предупреждение с `Code = "productive_morphology_agreement_mismatch"`, `Start = 6` (UTF-16 отместване, защото емоджито `😀` заема 2 UTF-16 единици), `Length = 4` (думата `"чете"`) и `RuleId = "clause_fusion_agreement.v1"`.
- При съгласуван вход (`"Аз 😀 чета."`) или при епистемичен/нееднозначен контекст (`"Може да беше прав."`) се връщат `0` предупреждения.

Основните методи на `BulgarianLanguageSession` обхващат още:
- `Lookup` / `FindSimilar` — точно, префиксно, суфиксно и приблизително търсене по форма;
- `GenerateForms` — словоформи по лема от пакета;
- `AnalyzeWord` / `DecomposeWord` / `DeriveWords` / `SegmentWord` — морфологични прочити и графово словообразуване за непознати думи;
- `LookupLexicalEntries` / `GetWordConstructionCandidates` / `GetStressEvidence` — лексикални статии, конструкции и ударения;
- `AnalyzeText` — включително незадължителни opt-in опции в `TextContextOptions` (`Continuation`, `IncludeNominalConstructions`, `IncludeWordConstructions`, `IncludeGrammarEvidence` и др. — вж. [ръководството на BgLang.Web](apps/BgLang.Web/README.md) и `/docs`).

## Четири слоя на текстовия отговор и `VerbFrameCatalog`

В `TextAnalysis` (и JSON полетата на `POST /api/v2/analyze-text`) резултатът е разделен на четири ясни слоя:
- **`Diagnostics` (`diagnostics` / `validation`):** доказани предупреждения с точен UTF-16 обхват и без автоматична промяна на входния `Text`.
- **`Grammar` (`grammar`, при `IncludeGrammarEvidence = true`):** структурни оценки `Compatible`, `Mismatch`, `Ambiguous` и `Unassessed`. Общото демо показва обвързания `Ambiguous` конфликт с `reason: "disjoint_subject_predicate_locus"` (напр. `"Момичетата рисува пейзажи."`) като видимо предупреждение върху сказуемото **без** автоматична замяна, защото двете страни на конфликта не определят еднозначно коя дума е сгрешена.
- **`Syntax` (`syntax`):** кандидатни синтактични роли (`Predicate`, `Subject`, `Object` и др.) с изричен маршрут `Selected`, `SourceScan` или `Abstained`.
- **`Repairs` (`repairs`):** само еднозначно проверени локални предложения за замяна, прилагани единствено при изрично потребителско действие.

Публичният вход `BulgarianLanguageSession.Open(manifestPath)` **не** зарежда вътрешния експериментален `VerbFrameCatalog`, защото изходният му лексикон за глаголни рамки (BVFL) се разпространява под `CC BY 3.0` (извън чистия MIT runtime пакет), а съотношенията по брой рамкови типове не са калибрирани спрямо корпусни честоти на употреба.

## Ограничения

Следваща задача за пунктуацията е корпусна проверка на границите на подчинени изречения и на крайния знак при пряк и косвен въпрос. За „Учителят каза че е умен.“ сегашната проверка дава кандидат пред „че“. При „Учителят каза, че е умен.“ кандидатът отпада. Задачата включва тестове чрез премахване, добавяне и подмяна на един знак, правилни контроли като „Готов ли е?“ и „Той попита дали е готов.“, както и проверка на позицията и предложението в API и демото.

- **Предварително издание (`v0.2.0-alpha.5`):** това е алфа prerelease и договорите/покритието продължават да се развиват.
- **Не е пълен синтактичен парсер:** BgLang не извършва пълен синтактичен или дискурсен разбор на произволен български текст.
- **Ограничен обхват и съзнателно въздържание:** граматичните проверки покриват поддържани локални именни групи (`NP`) и прости конструкции на изречението. При неподдържани или двусмислени конструкции анализаторът умишлено се въздържа — **`0` предупреждения не е доказателство за граматическа правилност**.
- **Без автоматично пренаписване:** предупрежденията и предложенията само сочат позиции и алтернативи; редакторът не променя текста без изрично действие от потребителя.
- **Хипотези при непознати думи:** графовите и морфологичните кандидати за думи извън речника са структурни хипотези, а не потвърдени речникови статии.
- **Лексикално ударение:** данните за ударение отразяват правилата и означенията в заредения пакет, а не пълна изреченска прозодия.

## Документация и предишни издания

- [Ръководство за локалния HTTP хост (`apps/BgLang.Web/README.md`)](apps/BgLang.Web/README.md)
- [Възпроизвеждане на пакета и CI проверки (`packaging/RELEASE-CANDIDATE.md`)](packaging/RELEASE-CANDIDATE.md)
- [Възпроизводима лингвистична оценка и сравнение с UDPipe 2 (`packaging/LINGUISTIC-EVALUATION.md`)](packaging/LINGUISTIC-EVALUATION.md)
- [Профил за графово ударение `CurrentGraphStress` (`packaging/GRAPH-STRESS.md`)](packaging/GRAPH-STRESS.md)
- [Инструмент за изграждане на пакети (`tools/BgLang.SourceBuilder/README.md`)](tools/BgLang.SourceBuilder/README.md)
- Предишни издания и архивни бележки: [GitHub Releases](https://github.com/bgtnt/bglang/releases)

## Лиценз

Кодът и авторските езикови данни се разпространяват под лиценза [MIT](LICENSE). Външните зависимости запазват своите условия в [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
