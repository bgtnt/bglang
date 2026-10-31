# Отделен CurrentGraphStress пакет

Профилът се избира изрично. Default Current остава editorial пакетът90bea; Baseline остава36b0 с graph0606. Новият graph snapshot не заменя data/word-formation/word-formation-source.v0.json или public-input-lock.v1.json.

Използва се съществуващият SourceBuilder: първо apply-stress-overlay върху locked authoring и graph0606, после build върху получения authoring и отделния automatic-graph-stress-ist.v1.json. Няма втори generator или общ graph overlay механизъм.

След зададени explicit TEMP/TMP и проверен child Path.GetTempPath(), изпълнете с нови output/results директории:

~~~powershell
./packaging/Build-PublicPackage.ps1 -InputDirectory <locked-authoring-directory> -Profile Current -OutputDirectory <new-article-current-directory>
./packaging/Build-PublicPackage.ps1 -InputDirectory <locked-authoring-directory> -Profile CurrentGraphStress -OutputDirectory <new-graph-stress-directory> -Test -TestWeb -ResultsDirectory <new-results-directory> -ArticleCurrentManifest <article-current-directory>/manifest.json
~~~

Изричният ArticleCurrentManifest е dependency за старите article/editorial controls и трябва да е точният90bea manifest. Legacy Public/Web lexical/stress controls остават върху възпроизведения Baseline36b0. Dedicated successor Public/HTTP controls и console примерът използват новия primary пакет.

Профилът current-graph-stress-reproduction.v1.json фиксира четирите RAW файла и prospective canonical manifest824bytes/SHA256 9db1df176daea9b703ebbf42022c7b1b871f2daecaa5d9178e0b788408f2e853. Package ID остава bglang-neutral:9e9a4f441f679542. Старият825bytes/3f6d manifest с +02:00/CRLF е отделна historical authority и не е RAW twin на този output. Очакванията са фиксирани преди actual producer измерването; само успешното сравнение на четирите файла установява reproduction.

Публичният полезен случай е exact-unknown „колажист“: запазени original graph candidate index, root „колаж“, owner legacy-base:2064768 и target tag Съществително:7a,Мъжки. Политиката дава Inferred mark2@5, word-local UTF16; не установява complete pronunciation и не пренаписва текста. Console sample „😀 колажист!“ проверява original span3/8. HTTP проверките покриват v1/v2, word/text и трите evidence formats.

Dedicated canonical selectors са BGLANG_GRAPH_STRESS_PROFILE=CurrentGraphStress и BGLANG_GRAPH_STRESS_SUCCESSOR_MANIFEST. BGLANG_GRAPH_STRESS_BASELINE_MANIFEST сочи36b0. Старият private BGLANG_GRAPH_STRESS_CURRENT_MANIFEST запазва историческите825byte controls; не се пренасочва мълчаливо.

Нова implementation acceptance изисква един combined Full с resource3, запазени strict45/original13M1/historical13 controls и независим validated-batch review. Този recipe не стартира private Full и не замества този gate.
