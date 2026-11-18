# BgLang.Web — локален HTTP хост и уеб демота (`v0.2.0-alpha.2`)

`BgLang.Web` е ASP.NET Core (.NET 10) приложение, което зарежда един строго проверен пакет от данни чрез `BulgarianLanguageSession.Open` и предоставя локален HTTP JSON API (`/api/v2/`, `/api/v1/`) заедно с две локални уеб приложения (`/demo` и `/demo-lab/index.html`) и вградена документация (`/docs`).

> **Само за локално изпълнение:** BgLang не предоставя публично хостван уеб сървър. По подразбиране хостът слуша единствено на `http://127.0.0.1:5070`, без CORS и без доверие към `Forwarded` / `X-Forwarded-*` заглавия.

## Подготовка и стартиране

Изисква се **.NET SDK 10.0.403** (от [`global.json`](../../global.json)) и четирите файла от [`bglang-v0.2.0-alpha.2-runtime.zip`](https://github.com/bgtnt/bglang/releases/download/v0.2.0-alpha.2/bglang-v0.2.0-alpha.2-runtime.zip), проверени спрямо [`SHA256SUMS`](https://github.com/bgtnt/bglang/releases/download/v0.2.0-alpha.2/SHA256SUMS) от изданието [`v0.2.0-alpha.2`](https://github.com/bgtnt/bglang/releases/tag/v0.2.0-alpha.2).

Поставете четирите runtime файла (`manifest.json`, `lexicon.v3.jsonl`, `word-formation.v0.json`, `lexeme-annotations.v1.json`) в подпапка `./runtime` на изходния код (профил `CurrentGraphStress`, `bglang-neutral:9e9a4f441f679542`, manifest SHA-256 `9db1df176daea9b703ebbf42022c7b1b871f2daecaa5d9178e0b788408f2e853`). От корена на хранилището:

```powershell
$env:BgLang__ManifestPath = (Resolve-Path './runtime/manifest.json').Path
$env:BgLang__ManifestSha256 = '9db1df176daea9b703ebbf42022c7b1b871f2daecaa5d9178e0b788408f2e853'
dotnet run --project apps/BgLang.Web/BgLang.Web.csproj -c Release
```

Хостът проверява целия пакет и сравнява хеша на манифеста преди да отвори порта; невалиден или различен пакет спира стартирането.

## Локални маршрути и страници

- `/demo` (и `/` пренасочва натам) — **BgLang Live**: редактор за анализ в реално време, инспектор на думата, речникови продължения (`context.continuation`), търсене по лема и API explorer;
- `/demo-lab/index.html` (и `/demo-lab` пренасочва натам) — **BgLang Live Lab**: експериментална лаборатория с наслагващо оцветяване, меню за продължения, синтактични роли по изречения и прочит с ударения (вж. [бележките за Live Lab](wwwroot/demo-lab/README.md));
- `/docs` — локалната HTTP API справка с пълните таблици за операциите, схемите и кодовете за състояние;
- `/health/live` — връща `200` `{"status":"live"}`;
- `/health/ready` — връща `200` `{"status":"ready"}` след зареждане на сесията (`503` при спиране);
- `GET /api/v2/` и `GET /api/v1/` — връщат действителните версии, метаданните на заредения пакет и ефективните ограничения на хоста.

Например можете да проверите заредения пакет и ефективните ограничения с:

```powershell
Invoke-RestMethod -Uri 'http://127.0.0.1:5070/api/v2/' | ConvertTo-Json -Depth 4
```

## Конфигурация и ограничения на хоста

Всички настройки могат да се подават чрез променливи на средата (с двойна долна черта `__`) или аргументи на командния ред:

| Настройка | Стойност по подразбиране | Описание |
| --- | --- | --- |
| `BgLang__ManifestPath` | *(задължителна)* | Път до `manifest.json` на зареждания пакет. |
| `BgLang__ManifestSha256` | *(задължителна)* | Точният 64-знаков SHA-256 хеш на `manifest.json` (`9db1df176daea9b703ebbf42022c7b1b871f2daecaa5d9178e0b788408f2e853` за `v0.2.0-alpha.2`). |
| `BgLang:RequestsPerMinute` | `30` | Положително цяло число за лимит на `POST` заявките на минута на клиентски IP (включително невалидни заявки). Отразява се в `GET /api/v2/`. |
| `BgLang:ResponseByteLimit` | `67108864` (`64 MiB`) | Максимален размер в UTF-8 байтове на успешен отговор при count-only preflight; над него се връща `413 response-too-large` без частично орязване. |
| `ASPNETCORE_URLS` / `--urls` | `http://127.0.0.1:5070` | Локален адрес и порт за слушане. |

**Вградени ограничения, кодове и мрежова политика:**
- **Дължини на входа и тялото:** дума/лема до `256` UTF-16 символа, текст до `16384` UTF-16 символа (`analyze-text`, `find-forms-in-text`, `stress`), HTTP тяло до `128 KiB` UTF-8 байтове.
- **Изпълнение и тайм-аут:** една сесия с последователен worker (`concurrency: 1`), опашка до `4` чакащи заявки (максимум `2 s` изчакване, иначе `503` с `Retry-After`) и `10 s` cooperative deadline (`504`).
- **HTTP кодове за отговор:** `200` (успех), `400` (невалиден JSON/поле/дължина/лимит), `413` / `415` (голямо тяло или отговор / липсващ `application/json`), `429` (надвишен `RequestsPerMinute` с `Retry-After`), `503` (опашка/спиране), `504` (тайм-аут), `500` (вътрешна грешка без връщане на входен текст или stack trace).
- **Локална сигурност:** по подразбиране хостът слуша само на `127.0.0.1:5070`, няма CORS достъп от други домейни и игнорира `Forwarded` / `X-Forwarded-*` заглавия (при евентуално публикуване зад reverse proxy е необходим HTTPS и изрична конфигурация за доверен прокси сървър). Логовете и кешът на заявки са изключени по подразбиране.

## Операции, формати и проверен пример

Поддържат се 14 `POST` операции под `/api/v2/` и `/api/v1/`:
- **Речник и словоформи:** `lookup`, `lookup-prefix`, `lookup-lexical-entries`, `lexical-entry`, `word-constructions`, `generate-forms`;
- **Морфология и словообразуване:** `analyze-word`, `derive-words`, `decompose-word`;
- **Ударения:** `stress`, `generate-stress-forms`, `derivational-stress`;
- **Анализ и търсене в текст:** `analyze-text`, `find-forms-in-text`.

`/api/v2/` използва `evidenceFormat: "shared-analyses-v1"` по подразбиране, а `/api/v1/` пази `raw-v1`; и двете версии поддържат изрично `raw-v1`, `shared-donors-v1` и `shared-analyses-v1`. За всички схеми, опциите в `context` (`includeNominalConstructions`, `includeWordConstructions`, `includeGrammarEvidence`, `continuation` и др.) и правилата за `limit` вижте локалната страница `/docs` (или файла [`wwwroot/docs.html`](wwwroot/docs.html)).

**Проверена заявка към `POST /api/v2/analyze-text`:**

```powershell
Invoke-RestMethod -Method Post -Uri 'http://127.0.0.1:5070/api/v2/analyze-text' `
  -ContentType 'application/json; charset=utf-8' `
  -Body '{"text":"Аз 😀 чете."}' | ConvertTo-Json -Depth 6
```

Очаквани основни полета в отговора:

```json
{
  "apiVersion": "2",
  "evidenceFormat": "shared-analyses-v1",
  "data": {
    "text": "Аз 😀 чете.",
    "diagnostics": [
      {
        "code": "productive_morphology_agreement_mismatch",
        "start": 6,
        "length": 4,
        "ruleId": "clause_fusion_agreement.v1",
        "projectedText": null
      }
    ]
  }
}
```

- `data.text` остава `"Аз 😀 чете."` без автоматична промяна.
- `start: 6`, `length: 4` сочат точно думата `"чете"` в UTF-16 координати (след `😀`, което заема 2 UTF-16 единици).
- При `"Аз 😀 чета."` и при епистемичния контекст `"Може да беше прав."` хостът връща `0` предупреждения.

### Четири слоя на отговора и `VerbFrameCatalog`

- **`diagnostics` / `validation`:** доказани предупреждения с точни UTF-16 отмествания (`start`, `length`).
- **`grammar` (`context.includeGrammarEvidence: true`):** оценки `Compatible`, `Mismatch`, `Ambiguous` и `Unassessed`. В `/demo` и `/demo-lab` конфликтите `Ambiguous` с `reason: "disjoint_subject_predicate_locus"` (напр. `"Момичетата рисува пейзажи."`) се показват като предупреждение върху зависимия предикат **без** автоматична поправка.
- **`syntax`:** кандидатни роли (`Predicate`, `Subject` и др.) с маршрут `Selected`, `SourceScan` или `Abstained`.
- **`repairs`:** само еднозначно проверени локални предложения за замяна.
- **`VerbFrameCatalog`:** `BgLang.Web` отваря пакета чрез публичното `BulgarianLanguageSession.Open` без вътрешния `VerbFrameCatalog`, тъй като BVFL каталогът е под `CC BY 3.0` (извън MIT runtime границата) и съотношенията по типове рамки не са калибрирани спрямо реални корпусни честоти.

## Проверки на клиента и сървъра

Клиентските тестове и проверката на синтаксиса не изискват стартиран сървър:

```powershell
node --check apps/BgLang.Web/wwwroot/live-client.js
node --check apps/BgLang.Web/wwwroot/live-demo.js
node --check apps/BgLang.Web/wwwroot/demo-lab/demo.js
node --test tests/BgLang.Web.Tests/Client/live-demo.test.mjs tests/BgLang.Web.Tests/Client/demo-lab.test.mjs
```

Пълните HTTP пакетни тестове се изпълняват срещу изрично посочен пакет (`BGLANG_WEB_TEST_MANIFEST` и `BGLANG_WEB_TEST_MANIFEST_SHA256`), както е описано в [ръководството за възпроизвеждане](../../packaging/RELEASE-CANDIDATE.md).

## Свързани документи и предишни издания

- [Начално ръководство (`README.md`)](../../README.md)
- [Бележки за BgLang Live Lab](wwwroot/demo-lab/README.md)
- [Възпроизвеждане на пакета (`packaging/RELEASE-CANDIDATE.md`)](../../packaging/RELEASE-CANDIDATE.md)
- [Възпроизводима лингвистична оценка (`packaging/LINGUISTIC-EVALUATION.md`)](../../packaging/LINGUISTIC-EVALUATION.md)
- Предишни издания и архивни бележки: [GitHub Releases](https://github.com/bgtnt/bglang/releases)
