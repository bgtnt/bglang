# Възпроизвеждане и пакетни профили (`v0.2.0-alpha.5`)

Това ръководство описва разликата между готовия пакет с данни и независимото пълно преизграждане от изходни данни за предварителното издание [`v0.2.0-alpha.5`](https://github.com/bgtnt/bglang/releases/tag/v0.2.0-alpha.5).

## Готов пакет (`runtime.zip`) спрямо пълно преизграждане (`inputs.zip`)

- **За директна употреба на библиотеката, конзолния пример или уеб хоста:** изтеглете [`bglang-v0.2.0-alpha.5-source.zip`](https://github.com/bgtnt/bglang/releases/download/v0.2.0-alpha.5/bglang-v0.2.0-alpha.5-source.zip), готовия [`bglang-v0.2.0-alpha.5-runtime.zip`](https://github.com/bgtnt/bglang/releases/download/v0.2.0-alpha.5/bglang-v0.2.0-alpha.5-runtime.zip) и [`SHA256SUMS`](https://github.com/bgtnt/bglang/releases/download/v0.2.0-alpha.5/SHA256SUMS). Разархивирайте четирите runtime файла в `./runtime` и следвайте [началното ръководство](../README.md) и [ръководството на BgLang.Web](../apps/BgLang.Web/README.md). Не е нужно да изтегляте изходните речникови входове.
- **За независимо байт-по-байт преизграждане на пакета:** изтеглете допълнително [`bglang-v0.2.0-alpha.5-inputs.zip`](https://github.com/bgtnt/bglang/releases/download/v0.2.0-alpha.5/bglang-v0.2.0-alpha.5-inputs.zip), който съдържа заключения входен файл `lexemes.source.json`. Изисква се **.NET SDK 10.0.403** (зададен в [`global.json`](../global.json)).

## Профили за съвместимост на пакета

Скриптът [`Build-PublicPackage.ps1`](Build-PublicPackage.ps1) поддържа три изрично разделени профила, всеки със собствен идентификатор и SHA-256 контролна сума на `manifest.json`:

| Профил | Идентификатор (`packageId`) | SHA-256 на `manifest.json` | Роля и съдържание |
| --- | --- | --- | --- |
| `CurrentGraphStress` | `bglang-neutral:9e9a4f441f679542` | `9db1df176daea9b703ebbf42022c7b1b871f2daecaa5d9178e0b788408f2e853` | Активният пакет в `v0.2.0-alpha.5-runtime.zip`. Включва редакционния слой за ударение и графовия профил за производни думи на `-ист`. |
| `Current` | `bglang-neutral:90bea7c5c799f04d` | `c718c78baaf244b0dfde39f9fe0e9295abb974cc254a94903e70a3014625bbc7` | Профилът по подразбиране в `Build-PublicPackage.ps1`. Прилага редакционния слой за ударение и служи като задължителна зависимост (`-ArticleCurrentManifest`) за контролите по членуване при `-Test`. |
| `Baseline` | `bglang-neutral:36b0b69685d6d3e9` | `451e4f1bd85030d3c8d888084412644933ad6831b1314b4ee5106b4cd27963e7` | Базовият профил за съвместимост без редакционния слой и без граф-стрес профила; използва се за запазените базови речникови, ударенски и HTTP контроли. |

В профила `CurrentGraphStress` четирите произведени файла (описани в [`current-graph-stress-reproduction.v1.json`](../data/dictionary/current-graph-stress-reproduction.v1.json)) имат следните точни размери и SHA-256 хешове:

| Файл | Размер (байтове) | SHA-256 | Предназначение |
| --- | ---: | --- | --- |
| `manifest.json` | 824 | `9db1df176daea9b703ebbf42022c7b1b871f2daecaa5d9178e0b788408f2e853` | Схема `bglang-neutral-runtime-package.v2`, версии и контролни суми на останалите файлове. |
| `lexicon.v3.jsonl` | 47 335 883 | `c4d08bfe1553a35a18e81d2644d1551b63a8c40691bb13a36b2211f946483366` | Основен речник (родни и съхранени парадигми за DAWG търсене). |
| `word-formation.v0.json` | 2 110 678 | `d588cddd473dba8a6f0932d32da5fba7beda0acb76d9677f59bab7da0dc44650` | Граф за словообразуване с профил за ударение на `-ист`. |
| `lexeme-annotations.v1.json` | 129 266 618 | `1a4975434655674872196e210e4b09c60aba799f006932a0db51755a2281bea4` | Анотации за значения, връзки и данни за ударение. |

Подробности за специфичните селектори и договори на графовото ударение са в [`GRAPH-STRESS.md`](GRAPH-STRESS.md).

## Независимо преизграждане от `inputs.zip`

1. Проверете контролните суми на изтеглените архиви спрямо `SHA256SUMS`:
   ```powershell
   Get-FileHash bglang-v0.2.0-alpha.5-source.zip, bglang-v0.2.0-alpha.5-inputs.zip, bglang-v0.2.0-alpha.5-runtime.zip -Algorithm SHA256
   ```
2. Разархивирайте `bglang-v0.2.0-alpha.5-inputs.zip` и поставете **само** заключения файл `lexemes.source.json` в `./authoring-input` (в пълния source пакет файлът може да се намира и под `public-inputs/preview7-auxiliary-repair-authoring-v5/lexemes.source.json`). Договорът [`public-input-lock.v1.json`](../data/dictionary/public-input-lock.v1.json) изисква точно **146 429 828** байта и SHA-256 `55c79288eca912c08f297a790f73327f768a0c9e9ebd3924b84d81474253046a`.
3. Уверете се, че в текущата PowerShell сесия променливите `TEMP` и `TMP` сочат към налична папка с права за запис, и задайте нови, несъществуващи целеви папки за изход и резултати:

```powershell
$ErrorActionPreference = 'Stop'

# 1. Изграждане на зависимостта за профила Current (нужна за контролите по членуване):
./packaging/Build-PublicPackage.ps1 -InputDirectory ./authoring-input -Profile Current -OutputDirectory ./article-current

# 2. Изграждане на пакета CurrentGraphStress и изпълнение на публичните и HTTP тестове:
./packaging/Build-PublicPackage.ps1 -InputDirectory ./authoring-input -Profile CurrentGraphStress `
    -ArticleCurrentManifest ./article-current/manifest.json `
    -OutputDirectory ./graph-a -ResultsDirectory ./graph-results-a -Test -TestWeb

# 3. (По избор) Повторно изграждане и байт-по-байт сверка на четирите файла:
./packaging/Build-PublicPackage.ps1 -InputDirectory ./authoring-input -Profile CurrentGraphStress `
    -ArticleCurrentManifest ./article-current/manifest.json `
    -OutputDirectory ./graph-b -ResultsDirectory ./graph-results-b -Test -TestWeb -CompareWith ./graph-a
```

**Важни правила на рецептата:**
- `-ArticleCurrentManifest` е задължителен при `-Test` с профил `CurrentGraphStress` или `Baseline`, за да обвърже контролите по членуване към точно построения пакет `Current`.
- `-TestWeb` изисква `-Test`; без `-TestWeb` не се изпълняват HTTP тестовете на `BgLang.Web.Tests`.
- При `-Test` с `CurrentGraphStress` посветените граф-стрес тестове и конзолният пример работят срещу новоизградения `CurrentGraphStress` пакет, докато запазените базови публични и HTTP контроли се проверяват срещу паралелно построения `Baseline` пакет (а не смесват профилите).
- Липсващ вход, несъответствие на SHA-256 хеш или невалиден набор от файлове води до незабавен отказ (`FAIL`) без резервно заместване.

## Самостоятелно тестване и износ на публичния код

- **Публичният списък от файлове:** [`Export-PublicSource.ps1`](Export-PublicSource.ps1) изнася точния списък от [`public-source-allowlist.v1.json`](public-source-allowlist.v1.json) след проверка на MSBuild проектите и референциите; с флаг `-SourceOnly` изходът пропуска `lexemes.source.json`.
- **Проверка на редакционната възпроизводимост:** [`Test-EditorialReproduction.ps1`](Test-EditorialReproduction.ps1) проверява байт-по-байт възпроизводимостта на профилите `Current` и `Baseline` между пълен и `-SourceOnly` експорт.
- **Проверка на JSON изхода на примера:** при `-Test` рецептата извиква автоматично `Verify-PublicConsumerJson.ps1` върху конзолните проби, включително `"😀 колажист!"` за `CurrentGraphStress`.
- **Възпроизводима лингвистична оценка и сравнение с UDPipe 2:** [`Evaluate-PublicBenchmark.ps1`](Evaluate-PublicBenchmark.ps1) оценява [`independent-59.v1.tsv`](evaluation/independent-59.v1.tsv) (срещу [`udpipe-independent-59.v1.conllu`](evaluation/udpipe-independent-59.v1.conllu)) и [`edge-and-ambiguity-controls.v1.tsv`](evaluation/edge-and-ambiguity-controls.v1.tsv) чрез публичното `BulgarianLanguageSession.Open` и сверява [`expected-summary.v2.json`](evaluation/expected-summary.v2.json) (подробен анализ в [`LINGUISTIC-EVALUATION.md`](LINGUISTIC-EVALUATION.md)):
  ```powershell
  ./packaging/Evaluate-PublicBenchmark.ps1 -ManifestPath ./graph-a/manifest.json -OutputDir ./eval-out -VerifyExpected
  ```
- **Ръчно пускане на публичните тестове и примера срещу изградения пакет:**
  ```powershell
  dotnet build examples/BgLang.PublicExample/BgLang.PublicExample.csproj -c Release
  dotnet run --project examples/BgLang.PublicExample/BgLang.PublicExample.csproj -c Release --no-build -- ./graph-a/manifest.json --json
  ```
- **Ръчна проверка на изтеглените архиви:** сравнявайте локално изчисления хеш на всеки `.zip` с реда му в `SHA256SUMS`:
  ```powershell
  Get-Content ./SHA256SUMS
  ```

## CI проверки за `v0.2.0-alpha.5`

Работният процес [`.github/workflows/windows-public.yml`](../.github/workflows/windows-public.yml) изпълнява автоматично на Windows:
- проверка на клиентския JavaScript синтаксис (`node --check` за `live-client.js`, `live-demo.js`, `demo-lab/demo.js` и всички `demo-tree/*.js`) с **Node.js 24**;
- проверка на относителните Markdown връзки (`node tools/check-md-links.mjs`);
- клиентски транспортни и DOM тестове (`node --test tests/BgLang.Web.Tests/Client/*.test.mjs`), включително общия редактор;
- изтегляне и SHA-256 проверка на заключения `lexemes.source.json`, построяване на `Current` и `CurrentGraphStress` с `-Test` (и `-TestWeb` при push/PR или когато `test_web=true` при `workflow_dispatch`), както и второ сравнително изграждане с `-CompareWith` при ръчно пускане (`workflow_dispatch`). Забележка: самият push на tag не задейства този workflow, затова приемането на издание се проверява върху съответния публичен commit и ръчния `workflow_dispatch` с `test_web=true`.

## Промени в `v0.2.0-alpha.5`

- При съчинени сказуеми с общ явен подлог API и редакторът могат да посочат несъгласувана лична форма. В „Аз чета и пишем.“ предупреждението е върху „пишем“. То е условно, защото българският език допуска отделен неизразен подлог.
- Връзките от HTTP справката към редактора, CSS, API и проверките за готовност работят и при публикуване под префикс като `/bglang`. Адресите `/docs/` и `/docs.html/` запазват префикса и параметрите на заявката при пренасочване.

## Исторически промени в `v0.2.0-alpha.4`

- API и общото демо показват допълнително сказуемо при разпознати съчинени глаголни групи. Основното сказуемо остава достъпно чрез `predicateTokenIndex`. В „Аз чета и пишем.“ се виждат две сказуеми. Проверката за съгласуване с общия подлог все още пропуска грешката във второто.
- Възвратното „се“ получава отделна роля `ReflexiveClitic`. При разпозната глаголна група могат да се проследят също кратките допълнения „ми“ и „ви“ до техния глагол.
- Включени са поправките след Alpha 3 за подлога в демото, запетаите при съюзи и показването на предложения за правопис. Данните в пакета `CurrentGraphStress` запазват същия SHA-256.

## Исторически промени в `v0.2.0-alpha.3`

- Общият редактор `/demo-tree` е включен в source архива и е началното локално демо; `/demo` и `/demo-lab` отварят същия редактор.
- Анализът на живо използва SignalR, обединява междинните редакции и показва резултати за текущия текст. Речникът, словоформите, ударенията, продълженията и HTTP работилницата са достъпни от същия редактор.
- Връзките в `predicateLinks` покриват бъдещо време с междинни кратки форми, възвратни клитики и условни спомагателни групи. Възможна структурна връзка не означава съгласуваност; конфликтите и оригиналните позиции остават отделни данни в отговора.
- Пакетът `CurrentGraphStress` и заключеният вход запазват точните си идентификатори и SHA-256 хешове от `v0.2.0-alpha.2`. Алгоритмите и клиентският код са от новото издание; номерът на пакетните данни е отделна идентичност.
- В публикуваната Alpha 3 проверката на 59 + 30 изречения съвпадна с историческото обобщение от Alpha 2. Текущият скрипт използва същите входове и сверява коригирания резултат за членуването при UDPipe с `expected-summary.v2.json`. [Методика и резултати](LINGUISTIC-EVALUATION.md)

## Исторически промени в `v0.2.0-alpha.2`

В сравнение с `v0.2.0-alpha.1`, изданието **`v0.2.0-alpha.2`** добавя:
- изолиране на предложното местоимение при несъвместим падеж без излишно предупреждение върху глагола (`"Заради аз закъсняхме."` маркира само `"аз"`);
- коректно предаване на подчинени изречения с `"че/защото + местоимение + ще"` към `ClauseFusion`;
- съгласуване при фамилия/лично име като подлог спрямо възвратно представяне с calling/naming предикат (`"Чорбанова се казвам."` остава чисто без `Subject` роля върху фамилията, докато `"Чорбанова четат."` и `"Чорбанова се смеят."` маркират глагола);
- независимо съвместно действие на проверките за род/число и членуване (`"Чорбанова четат на червена пейката."`, `"Малките книгата..."`);
- видимо предупреждение в `/demo` и `/demo-lab` без авто-поправка при `Ambiguous:disjoint_subject_predicate_locus` (`"Момичетата рисува пейзажи."`);
- обяснение на четирите слоя на отговора, границите на вътрешния `VerbFrameCatalog` и възпроизводима оценка [`Evaluate-PublicBenchmark.ps1`](Evaluate-PublicBenchmark.ps1) + [`LINGUISTIC-EVALUATION.md`](LINGUISTIC-EVALUATION.md) при непроменен пакет `CurrentGraphStress` (`bglang-neutral:9e9a4f441f679542`).

## Предишни издания

Всички по-стари версии, техните архиви, SHA-256 суми и исторически бележки са достъпни в [GitHub Releases](https://github.com/bgtnt/bglang/releases).
