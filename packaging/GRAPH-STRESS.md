# Отделен пакет CurrentGraphStress

Профилът `CurrentGraphStress` се избира изрично. Профилът по подразбиране `Current` остава редакционният пакет (`bglang-neutral:90bea7c5c799f04d`, manifest SHA-256 `c718c78baaf244b0...`), а `Baseline` остава базовият пакет за съвместимост (`bglang-neutral:36b0b69685d6d3e9`, manifest SHA-256 `451e4f1bd85030d3...`) с базовия граф [`word-formation-source.v0.json`](../data/word-formation/word-formation-source.v0.json) (`0606a107...`). Новата графова снимка не заменя [`word-formation-source.v0.json`](../data/word-formation/word-formation-source.v0.json) или [`public-input-lock.v1.json`](../data/dictionary/public-input-lock.v1.json).

За изграждането се използва съществуващият `SourceBuilder`: първо `apply-stress-overlay` върху заключения входен речник (`lexemes.source.json`) и базовия граф `word-formation-source.v0.json` (`0606a107...`), а след това `build` върху получения изходен файл и самостоятелния граф [`automatic-graph-stress-ist.v1.json`](../data/word-formation/automatic-graph-stress-ist.v1.json). Няма втори генератор или общ механизъм за припокриване на графове.

Уверете се, че `TEMP` и `TMP` сочат към съществуваща директория с права за запис (включително в дъщерния процес `[IO.Path]::GetTempPath()`), и изпълнете с нови, отделни папки за изход и резултати:

~~~powershell
./packaging/Build-PublicPackage.ps1 -InputDirectory <locked-authoring-directory> -Profile Current -OutputDirectory <new-article-current-directory>
./packaging/Build-PublicPackage.ps1 -InputDirectory <locked-authoring-directory> -Profile CurrentGraphStress -OutputDirectory <new-graph-stress-directory> -Test -TestWeb -ResultsDirectory <new-results-directory> -ArticleCurrentManifest <article-current-directory>/manifest.json
~~~

Изричният параметър `-ArticleCurrentManifest` е задължителна зависимост за контролите по членуване и редакционните ударения и трябва да сочи към точния манифест на `Current` (`bglang-neutral:90bea7c5c799f04d`, `c718c78b...`). Запазените базови публични и HTTP речникови/ударенски контроли се изпълняват върху паралелно построения `Baseline` (`bglang-neutral:36b0b69685d6d3e9`, `451e4f1b...`). Специализираните `CurrentGraphStress` публични и HTTP контроли и конзолният пример работят срещу новия основен пакет.

Профилът [`current-graph-stress-reproduction.v1.json`](../data/dictionary/current-graph-stress-reproduction.v1.json) фиксира четирите сурови файла и каноничния `manifest.json` (`824` байта, SHA-256 `9db1df176daea9b703ebbf42022c7b1b871f2daecaa5d9178e0b788408f2e853`), с идентификатор на пакета `bglang-neutral:9e9a4f441f679542`. По-ранният 825-байтов манифест (`3f6d48b0...`, с `+02:00` и CRLF) е отделна историческа форма и не съвпада байт-по-байт с този каноничен изход. Само успешното сравнение на всичките четири файла удостоверява точно възпроизвеждане.

Публичният контролен пример е непознатата в речника дума `"колажист"`: запазват се оригиналният индекс на графовия кандидат, коренът `"колаж"`, собственикът `legacy-base:2064768` и целевият таг `"Съществително:7a,Мъжки"`. Политиката дава прогнозно ударение (`Inferred`) върху гласна с индекс `2` (UTF-16 позиция `5` в рамките на думата), без да твърди пълно произношение и без да пренаписва текста. Конзолният пример `"😀 колажист!"` проверява оригиналния UTF-16 диапазон (`Start = 3`, `Length = 8`), а HTTP тестовете покриват `/api/v1/` и `/api/v2/`, анализ на дума/текст и трите формата за доказателства (`raw-v1`, `shared-donors-v1`, `shared-analyses-v1`).

Каноничните променливи на средата за избор на този профил в тестовете са `BGLANG_GRAPH_STRESS_PROFILE=CurrentGraphStress` и `BGLANG_GRAPH_STRESS_SUCCESSOR_MANIFEST`, докато `BGLANG_GRAPH_STRESS_BASELINE_MANIFEST` сочи към `Baseline` (`451e4f1b...`). Отделната променлива `BGLANG_GRAPH_STRESS_CURRENT_MANIFEST` запазва старите 825-байтови контроли и не се пренасочва мълчаливо. Пълният вътрешен пакет от лингвистични регресии остава отделна проверка и не се стартира от тази публична рецепта.
