# Възпроизводима лингвистична оценка и сравнение с UDPipe 2 (`v0.2.0-alpha.2`)

Този документ представя публично възпроизводимата оценка на **BgLang** (`v0.2.0-alpha.2`) върху пакета **`CurrentGraphStress`** (`bglang-neutral:9e9a4f441f679542`, SHA-256 на `manifest.json`: `9db1df176daea9b703ebbf42022c7b1b871f2daecaa5d9178e0b788408f2e853`), сравнена с **UDPipe 2** (`bulgarian-btb-ud-2.17-251125`).

Всички числа са измерени чрез публичния API вход [`BulgarianLanguageSession.Open`](../src/BgLang.Api/Kernel/BulgarianLanguageSession.cs) (без вътрешния `VerbFrameCatalog`) със същите седем булеви опции в `TextContextOptions`, които използват `/demo` и `/demo-lab`, докато настройката `includeUnknownContextProperNames` е `false` в benchmark скрипта и `true` по подразбиране в `/demo` и `/demo-lab`. При контролна проверка на всичките `59 + 30` изречения със стойност `includeUnknownContextProperNames: true` върху същия пакет `CurrentGraphStress` всички `89` индивидуални резултата са идентични байт по байт с публикувания профил `false` (в обобщението `summary.json` се различава единствено записаната стойност на настройката).

## Точна конфигурация и възпроизвеждане

- **Входна точка:** `BulgarianLanguageSession.Open(manifestPath)` (само MIT runtime пакет `bglang-neutral:9e9a4f441f679542`).
- **Опции на `AnalyzeText`:** `includeUnknownContextProperNames: false` (изключена в benchmark; в `/demo` и `/demo-lab` по подразбиране е `true`, като за всичките `89` изречения тук двата профила дават идентични байт по байт индивидуални резултати) и седемте съвпадащи с демотата опции на `TextContextOptions`:
  - `IncludeGrammarEvidence = true`
  - `IncludeAdditionalRoleHeuristics = true`
  - `IncludeWordConstructions = true`
  - `IncludeNominalConstructions = true`
  - `IncludeSubordinateCommaDiagnostics = true`
  - `IncludeFragmentDiagnostics = true`
  - `IncludeSpacingDiagnostics = true`
- **Показани предупреждения:** всички действащи граматични, съгласувателни, членни и ремонтни кодове от `Diagnostics` / `Validation` (без правописни догадки при непознати собствени имена) плюс клиентската проекция на обвързания `TextGrammarAssessment.Ambiguous` конфликт с причина `disjoint_subject_predicate_locus` върху зависимия предикат (без автоматична поправка).
- **Очаквано обобщение и входни данни:** [`expected-alpha2-summary.v1.json`](evaluation/expected-alpha2-summary.v1.json), [`independent-59.v1.tsv`](evaluation/independent-59.v1.tsv), [`udpipe-independent-59.v1.conllu`](evaluation/udpipe-independent-59.v1.conllu) и [`edge-and-ambiguity-controls.v1.tsv`](evaluation/edge-and-ambiguity-controls.v1.tsv).

Пуснете възпроизводимата проверка от корена на публичното хранилище след разархивиране на [`bglang-v0.2.0-alpha.2-runtime.zip`](RELEASE-CANDIDATE.md) в `./runtime`:

```powershell
./packaging/Evaluate-PublicBenchmark.ps1 `
    -ManifestPath ./runtime/manifest.json `
    -OutputDir ./eval-out `
    -VerifyExpected
```

---

## 1. Набор [`independent-59.v1.tsv`](evaluation/independent-59.v1.tsv) (59 изречения, 381 думи) и сравнение с UDPipe 2

Наборът съдържа `25` граматични изречения (`R`, `172` думи) с анотирани главно сказуемо и подлог (`23` с изричен подлог и `2` безлични/безподложни с `-`) и `34` изречения с точно по `1` целева грешка (`E`), разделени в 5 категории (`AN`, `ART`, `AUX`, `PRED`, `SV`). Изходът от `UDPipe 2` е запазен в [`udpipe-independent-59.v1.conllu`](evaluation/udpipe-independent-59.v1.conllu) и оценен по детерминистични 6-правилни UD зависимости.

### Метрика 1: Попадения на целеви грешки и роли в `R`

| Категория | Общо | BgLang попадения на целта | BgLang точни / disjoint | BgLang условни (`Ambiguous`) | BgLang тихи | UDPipe 2 попадения (6 правила) | UDPipe 2 тихи |
| --- | ---: | ---: | --- | ---: | ---: | ---: | ---: |
| `AN` (определение – същ.) | 8 | **8 / 8** | 8 / 0 | 0 | 0 | 7 / 8 | 1 |
| `ART` (пълен / кратък член) | 8 | **8 / 8** | 8 / 0 | 0 | 0 | 4 / 8 | 4 |
| `AUX` (спом. глагол – причастие) | 3 | **3 / 3** | 3 / 0 | 0 | 0 | 3 / 3 | 0 |
| `PRED` (предикативно съгласуване) | 5 | **4 / 5** (+1 условен) | 4 / 0 | 1 | 0 | 5 / 5 | 0 |
| `SV` (подлог – сказуемо) | 10 | **10 / 10** | 9 / 1 | 0 | 0 | 9 / 10 | 1 |
| **Общо `E` контроли** | **34** | **33 / 34** | **32 / 1** | **1** | **0** | **28 / 34** | **6** |

- Единственото условно `E` изречение (`"Ябълките от нашата градина са сладка."`) се улавя вътрешно в `Grammar` като `Ambiguous` с причина `competing_predicative_role`, без да се повдига като твърдо предупреждение.
- Единственият `disjoint_subject_predicate_locus` случай (`"Фирмата търсят опитен счетоводител."`) се показва в `/demo` и `/demo-lab` като непоправяемо предупреждение върху `"търсят"`.
- **Роли в `R` (`25` граматични изречения):**
  - Главно сказуемо: **BgLang `25 / 25`** (`0` грешни, `0` пропуснати) | UDPipe 2 `25 / 25`.
  - Изричен подлог (`23` изречения): **BgLang `23 / 23`** (`0` грешни, `0` пропуснати) | UDPipe 2 `22 / 23`.
  - Безподложни (`2` изречения): **BgLang `0` излишни подлога**.
- **Структурно съгласие BgLang ↔ UDPipe 2 върху всички `59` изречения:** BgLang извлича синтактично главно сказуемо в `44 / 59` (`74.58%`) и в **`44 / 44` (`100%`)** от тези изречения сказуемото съвпада с UD `root`/`aux`/`cop`; при съвпаднало сказуемо и наличен подлог в двете системи (`36` изречения) съгласието по подлог е **`35 / 36` (`97.22%`)** (`bgOnlySubject = 0`, `udOnlySubject = 5`).

### Метрика 2: Излишни и нецелеви предупреждения

| Измерване | Изречения / думи | BgLang резултат | UDPipe 2 резултат |
| --- | --- | --- | --- |
| Чисти `R` контроли с предупреждение | 25 изр. / 172 думи | **0 / 25** (`0.000` / 1000 думи) | 0 / 25 |
| Грешни `E` изречения с излишно предупреждение извън целта | 34 изр. | **0 / 34** (`extraWarningsTotal = 0`) | `0` извънцелеви (`errorHitElsewhere = 0`) |
| Грешни `E` попадения на грешен токен (`hitElsewhere`) | 34 изр. | **0 / 34** | 0 / 34 |

### Метрика 3: Неоценени и двусмислени случаи (`Ambiguous` / `Unassessed` / `Syntax` / `Validation`)

- **`Grammar.Matches` (`Assessment = Ambiguous`, общо `21` съвпадения в `59` изречения):**
  - `competing_nominal_construction`: `8` (тихо въздържание)
  - `unspecified`: `6` (тихо въздържание)
  - `competing_predicate_group`: `4` (тихо въздържание)
  - `competing_predicative_role`: `2` (тихо въздържание)
  - `disjoint_subject_predicate_locus`: `1` (показвано в `/demo` и `/demo-lab` като конфликт без авто-поправка)
- **`Grammar.Matches` (`Assessment = Unassessed`):** `0`.
- **Маршрути на `Syntax` (`59` изречения, статус `Calculated = 59`):** `Selected = 29`, `SourceScan = 15`, `Abstained = 15`.
- **`Validation.UnavailableChecks` (`61` записа):**
  - `article.intransitive_animate:typed_Animacy_and_Transitivity_unavailable`: `59`
  - `subordinate_comma:reviewed_marker_authority_unavailable`: `2`

---

## 2. Набор [`edge-and-ambiguity-controls.v1.tsv`](evaluation/edge-and-ambiguity-controls.v1.tsv) (30 контролни изречения, 126 думи)

Този набор проверява гранични конструкции и регресии извън `independent-59`: несъвместими местоимения в предложна група (`PRON_CASE`), подчинени изречения с `"ще"` (`SUBORD_FUTURE_SV`), фамилия като подлог спрямо представяне с `"се казвам"` (`SURNAME_SV` и чистият контрол `"Чорбанова се казвам."`), многократни грешки в едно изречение (`MULTI_SV_ART`, `MULTI_AN_ART`), неразделени `"не+глагол"`, бройна форма, вложени релативни изречения и конфликти с разделен локус (`AMBIG_DISJOINT_SV`).

### Метрика 1: Попадения по категория

| Категория | Общо | Попадения на целта | Точни / `disjoint` | Тихи | Излишни предупреждения |
| --- | ---: | ---: | --- | ---: | ---: |
| `AMBIG_DISJOINT_SV` | 2 | **2 / 2** | 0 / 2 | 0 | 0 |
| `COUNT_NOUN` | 1 | **1 / 1** | 1 / 0 | 0 | 0 |
| `JOINED_NEG` | 1 | **1 / 1** | 1 / 0 | 0 | 0 |
| `MULTI_AN_ART` (всички цели) | 2 | **2 / 2** | 2 / 0 | 0 | 0 |
| `MULTI_SV_ART` (всички цели) | 1 | **1 / 1** | 1 / 0 | 0 | 0 |
| `PRON_CASE` | 2 | **2 / 2** | 2 / 0 | 0 | 0 |
| `REL_CLAUSE` | 2 | **1 / 2** | 1 / 0 | 1 | 0 |
| `REL_MATRIX_SV` | 1 | **1 / 1** | 1 / 0 | 0 | 0 |
| `SUBORD_FUTURE_SV` | 3 | **3 / 3** | 3 / 0 | 0 | 0 |
| `SURNAME_SV` | 3 | **3 / 3** | 3 / 0 | 0 | 0 |
| **Общо `E` контроли** | **18** | **17 / 18** | **15 / 2** | **1** | **0** |

### Метрика 2 и Метрика 3 върху `edge-and-ambiguity-controls.v1.tsv`

- **Чисти и умишлено въздържани контроли (`C`, `12` изречения, `50` думи):** **`0 / 12`** предупреждения (`0.000` на 1000 думи), включително `"Чорбанова се казвам."`, `"Може да беше прав."` и `"Четат на червена 📚 пейката."`.
- **Излишни предупреждения в грешни изречения (`18` `E` изречения):** **`0`** (`sentencesWithExtraWarning = 0`, `extraWarningsTotal = 0`). Например при `"Заради аз закъсняхме."` предупреждението остава само върху `"аз"` без каскадно маркиране на `"закъсняхме"`.
- **Единственото тихо въздържание (`1 / 18`):** `"Видях хората, които живее тук."` (`REL_CLAUSE` след многозначното `"хората"`), докато `"Книгите, за които говорихме вчера, пристигна навреме."` и матричното `"Книгите на хората, които живеят тук, излезе вчера."` се засичат.
- **`Grammar.Matches` (`Ambiguous = 2`, `Unassessed = 0`):** и двете `Ambiguous` съвпадения са `disjoint_subject_predicate_locus` (`"Момичетата рисува пейзажи."`, `"Майката поздравиха дъщерята."`), показвани в UI като предупреждение без замяна.
- **`Syntax` маршрути (`30`, статус `Calculated = 30`):** `Selected = 13`, `SourceScan = 9`, `Abstained = 8`. **`UnavailableChecks` (`31`):** `article.intransitive_animate:typed_Animacy_and_Transitivity_unavailable` (`30`), `fragment:predicate_absence_unproven` (`1`).

---

## 3. Допълнителна корпусна справка (1 200 чисти изречения)

При вътрешен контрол върху `1 200` чисти литературни, новинарски и нормативни изречения (`17 969` думи; оригиналните корпусни текстове не се включват в MIT хранилището поради авторски лицензи на източниците) със същия публичен пакет `CurrentGraphStress` и същите `TextContextOptions`:

- Предупреждения: `28` предупреждения в `25 / 1 200` изречения (`1.558` на `1 000` думи): `23` за `accent_sensitive_i_grave` и `5` за `preposition_missing_complement`.
- Лъжливи предупреждения за подлог–глагол съгласуване (`productive_morphology_agreement_mismatch`): **`0`**.
- Синтактични маршрути: `Selected = 738`, `SourceScan = 64`, `Abstained = 398`.

---

## Ограничения и статут на `VerbFrameCatalog`

1. **`0` предупреждения не означава граматическа доказаност:** при конструкции с високо структурно колебание или липсващ авторитетен маркер ядрото съзнателно се въздържа (`Abstained` / `Ambiguous`).
2. **Без авто-поправки при `disjoint_subject_predicate_locus`:** конфликтът показва, че две интерпретации се разминават (напр. `"Момичетата рисува пейзажи."`), без системата да избира произволно подлога или сказуемото.
3. **Защо публичното `BulgarianLanguageSession.Open` не включва `VerbFrameCatalog`:** експерименталният `VerbFrameCatalog` разчита на лексикон с глаголни рамки (BVFL) под лиценз `CC BY 3.0`, който стои извън MIT runtime границата; освен това съотношенията по брой уникални рамкови типове не са калибрирани спрямо реални честоти на употреба в корпус. Поради тези две причини каталогът остава вътрешен/изследователски механизъм, а публичният C# API остава чист MIT договор.
