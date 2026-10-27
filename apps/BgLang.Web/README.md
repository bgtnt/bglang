# BgLang Live — езикова лаборатория

## Preview11 — текущ source snapshot

[Preview11 release](https://github.com/bgtnt/bglang/releases/tag/v0.1.0-preview.11) е authority за окончателния publication/CI/readback статус. Този source snapshot е преди финалната публикация. Matching source и runtime използват същия complete package `bglang-neutral:36b0b69685d6d3e9`; [quickstart](../../README.md) задава точните download имена. Default local host остава `http://127.0.0.1:5070/demo`.

Измерен пример със същия complete package: в „Това е новз дете.“ предложението „ново“ се премества от сурова позиция 4 на контекстна позиция 1; при „Това е новз книга.“ „нова“ се премества от 2 на 1. Distance остава първият критерий, а raw Alternatives, пълните readings и Score са непроменени. Това е подкрепа за преглед, без обещание за единствена правилна поправка.

При изричен правописен избор Live запазва простия кирилски регистър: „друфа“→„друга“, „Друфа“→„Друга“, „ДРУФА“→„ДРУГА“. Mixed/неподдържан source и смислено главни dictionary форми запазват raw предложението. Combining marks не се нормализират; заменя се само оригиналният проверен UTF-16 диапазон. Невалидни surrogate/mark граници не предлагат действие.

Корекцията на позната, но неподходяща дума още не е реализирана. В „Рисувам сипя река.“ „сипя“ е exact-known (`NotRequestedKnown`) и няма такава диагностична поправка. Отделно `FindSimilar("сипя", 1, 10)` намира „синя“ на позиция 5, без доказателство за намерението или единствена корекция. Context остава Ambiguous и Syntax се въздържа и при поправения вариант и правилните контроли; това въздържане не е диагноза за грешка.

### Проверен локален browser сценарий — 27 октомври 2026

Оригиналните Live JS/CSS и complete package са проверени с Chrome156 върху Windows, видим viewport375×1061, default budget30POST/min и една serial POST лента. Краткият текст „😀 ѝ. Аз чете. Това е Друфа дума. ДРУФА дом.“ показва затвореното предупреждение за „Аз чете.“ и ръчните „Друга“/„ДРУГА“ замени на original UTF-16 spans23+5/35+5. Реална редакция на средния span23..33 запазва prefix/suffix. При задържан parsed стар response и по-нов input старите spelling/stress действия се обезсилват; старият отговор не става свеж, после „нова“ е първото съвместимо предложение пред „книга“.

Измерени са отделни warmed browser input samples: 259,20ms до първия exact fresh DOM и 369,50ms до double-rAF rendering opportunity; след middle edit — 2081,60ms/2148,50ms, включително cadence/queue при30POST/min. Ръчно задържаният стар body е задържан22,02s общо; по-късният последен input достига свежия DOM за12949,20ms и double-rAF за13003,90ms. Последният actual HTTP parsed body е97,50ms. Това са три отделни наблюдения с debounce, queue, network и render; не percentiles, физическо pixel време или обща latency гаранция. Сценарият има9POST/max1 active body, всички200. Native IME не е упражнен; composition/stale callback controls са отделни client tests. Временният observer е извън Product и не се публикува; първият capture-microtask observer запис е запазен, а поправеният повторен run е без freshness mismatches.

Локално: exact Release build, focused1, Public203/Web174 и Node22.15 client84/syntax2 са PASS, без failures/skips. Node24 hosted validation се удостоверява от действителните CI logs на release страницата.

## Исторически snapshot — Preview10 и предходни версии

Следващият текст пази предходните version/status/results snapshots; старите „текущ“, client80 и pending формулировки не описват Preview11.


## Preview10 — локален Web consumer

Този guide описва [Preview10](https://github.com/bgtnt/bglang/releases/tag/v0.1.0-preview.10) source/runtime distribution. **Текстът е snapshot преди окончателната публикация**; целевите download връзки в [Preview10 quickstart](../../README.md) стават достъпни след release. Release страницата е authority за текущия статус, exact public source/tag, действителните main/tag CI и all11 anonymous bytes/hash readback. Тук няма предварителен hosted PASS или public hosting claim. Използвайте matching source и четирифайлов runtime от една версия. От source папката с runtime в `./runtime`:

~~~powershell
$env:BgLang__ManifestPath = (Resolve-Path './runtime/manifest.json').Path
$env:BgLang__ManifestSha256 = '451e4f1bd85030d3c8d888084412644933ad6831b1314b4ee5106b4cd27963e7'
dotnet run --project apps/BgLang.Web/BgLang.Web.csproj -c Release
~~~

Default адресът е `http://127.0.0.1:5070`, с `/demo` и `/docs`. Complete runtime остава `bglang-neutral:36b0b69685d6d3e9`. [Release guide](../../packaging/RELEASE-CANDIDATE.md) описва source/package/consumer verification.

### Live поведение в Preview10 source

Последният завършен анализ остава видим, докато чака нов отговор, с обозначени предишни данни. Действията проверяват text/selection/revision и оригиналния диапазон; stale бутон не може да приложи промяна. Същият snapshot/избор запазва inspector DOM без междинен placeholder.

Диагностиките имат три класа: „Предупреждение“, „Неподдържана проверка“ за `full_short_article_check_not_ready` и „Информация“. Preview кодове не се превръщат в warnings. Празни и недостъпни проверки се различават; липсата на съобщение не доказва обща граматична правилност. `closed_exact_agreement.v1` добавя diagnostic-only exact pronoun+finite и `ADJECTIVE* + един common-noun head + финален finite` проверки с original complete refs, запазени алтернативи и без нов Context/Syntax selection или fabricated graph опора.

Правописният панел предлага близки речникови форми в целия текст, включително по-ранна дума при курсор в края. Замяна има само при изричен избор върху проверения оригинален UTF-16 span. Raw Alternatives, distance и всички candidate readings се запазват. Отделното `contextualCandidates` evidence може да даде предимство на measured adjective→following-noun съвместимост при равна distance. „друфа“ в „Това е друфа дума“ има пет distance-one алтернативи; съвместимият feminine singular прочит на „друга“ е подкрепа за предложение, без unique-correction claim. Имена, graph evidence и незавършена дума остават несигурни.

### Client проверки и CI

[Windows workflow](../../.github/workflows/windows-public.yml) конфигурира **Node.js 24** и изрично изпълнява syntax2 и transport/DOM [client harness](../../tests/BgLang.Web.Tests/Client/live-demo.test.mjs), отделно от C# recipe:

~~~powershell
node --check apps/BgLang.Web/wwwroot/live-client.js
node --check apps/BgLang.Web/wwwroot/live-demo.js
node --test tests/BgLang.Web.Tests/Client/live-demo.test.mjs
~~~

Локалното evidence е **Node.js 22.15.0**, client80 PASS/0 FAIL/0 skipped след DOM fixture поправката и syntax2 PASS за същите product JS bytes; предходният fixture run73 PASS/7 FAIL е запазен. Това не е Node24 hosted резултат или окончателно full-suite/browser/export приемане. Действителните публикационни проверки и точните им bindings се записват в release body след изпълнение.

### Предходен публикуван Preview9

[Preview9](https://github.com/bgtnt/bglang/releases/tag/v0.1.0-preview.9) от public source `e43ef2b97ef2fa2a9d9f01c2bc5e616d5f37d0c1` има успешни main/tag CI и anonymous all11 readback. Неговият workflow няма JS step; това е исторически факт за Preview9, не описание на Preview10 конфигурацията.

## Исторически snapshot — преди публикацията на Preview9, 26 октомври 2026

Целият запазен текст по-долу отразява тогавашния source/preparation етап. „Локален Preview9“, Node22+, client48, старите Preview6 hashes и твърденията за pending publication/липсващ JS CI step са исторически; текущият статус, runtime и candidate договор са горе. Историческите секции не се преномерират и старите receipts не се приемат за новия кандидат.

## Текущият Web интерфейс

Това е текущият локален кандидат `v0.1.0-preview.9` върху приетия source `44bb169b`, без нова публикация или public hosting claim. /demo е жив редактор: анализ на целия оригинален текст, инспектор при движение на курсора, typed части на речта, речникова многозначност и кандидатни проверки. Всички текстови слоеве идват с една analyze-text заявка с явни opt-ins; няма frontend езиков engine. Старият API explorer, word actions, donor/JSON evidence и търсенето по лема остават в „Разширена работилница“. /docs остава HTTP справката.

Оцветяването изисква съгласие между върнатите typed POS, а не frontend разчитане на raw tags. Пълните прочити се адресират през occurrence/readingSetIndex и supportReadings ?? canonicalReadings; празен support не преминава към canonical. Unknown, непълнота и многозначност не са червени езикови грешки. Null изключено, изпълнен празен списък и unavailable са различни състояния. Евристичните роли, имена и конструкции не са общ dependency parser или нормативно решение.

Контекстните finite и noun продължения в текущия локален Live идват чрез `context.continuation` в същата `analyze-text` заявка. Selector „Авто / Глаголи / Съществителни“ използва `Auto` по подразбиране; API default остава `Finite`. Typed person/number съвместимостта за finite или coherent number/gender/article tuple за noun се проверява преди лимита за показани форми (1–256), включително при празен prefix след whitespace и при частично въведена дума. Контекстът използва същия оригинален inventory и exact/graph опори; всяка предложена форма пази пълната exact група прочити и matching reading indices. При изричен избор се заменя целият captured Word span (или се вмъква при празен span) само ако заснетите text/caret/revision и режим още съвпадат, без range selection или IME composition. Десният текст се запазва, без гаранция за неговата граматика. Редът е детерминистичен dictionary rank, не probability.

`POST /api/v2/lookup-prefix` остава отделен обикновен речников endpoint с `text` и optional `limit` (1–256, default32), тънък adapter към съществуващия `Lookup(Prefix)`; `lookup` остава Exact. Неговият лимит е за прочити, така че surface група може да е отрязана. Той не се използва за контекстните finite предложения в Live.

Има една обща POST лента за Live, prefix, refresh, word actions и старите форми. Debounce150ms и максимум600ms за непрекъснат typing coalesce-ват към последната версия; IME composition не изпраща междинни текстове. Стара успешна/неуспешна заявка не променя текущия изглед. Следващ POST започва едва след прочетения/parsed body и текущия host бюджет. Retry-After при429/503 важи и за stale/невалиден JSON отговор. HTTP ms включва целия body; отделният DOM ms не е kernel време или обещание за latency.

BgLang:RequestsPerMinute е положителна целочислена host настройка с default30. GET /api/v1/ и /api/v2/ връщат effective requestsPerMinutePerIp; клиентът ползва тази стойност. Няма loopback exemption или trusted forwarded headers; един serialized lane, queue4 и deadline10s се запазват. За изрично локално интерактивно демо може да изберете по-висок rate след измерване на конкретния host; public deployment запазва default30, ако не е конфигуриран другояче.

Светла/тъмна тема се сменя с бутон и е единствената localStorage настройка. Входният текст не се запомня. prefers-reduced-motion изключва pulse/transitions; pulse се показва само при активна HTTP работа. Ударенията/owner alternatives са отделно действие; изборът ползва точните tokenIndex/entryId/form/nullable fullTag от rich stress отговора. Няма измислено capture или merge на mutually exclusive marks.

### Независими client проверки

Допълнителният test harness изисква Node22+ само за development проверката, без npm/framework зависимости:

~~~powershell
node --test tests/BgLang.Web.Tests/Client/live-demo.test.mjs
node --check apps/BgLang.Web/wwwroot/live-client.js
node --check apps/BgLang.Web/wwwroot/live-demo.js
~~~

Той проверява actual fetch timestamps/body settlement, latest/stale/IME, Retry-After, UTF-16 offsets, shared references и safe DOM със злонамерен markup. Web package tests отделно проверяват C# adapters, protections и served assets; GET200 не е JS/browser verification. Реален browser smoke и независимото review са отделни acceptance evidence.

Приетият локален source има48 client PASS и две syntax PASS. Exported client се проверява изрично и отделно от C# recipe; съществуващите recipe и hosted CI не изпълняват JS тестовете. Hosted JS coverage не се твърди.

Реалният локален browser smoke от26 октомври е приет при exact API/assets binding: B1–B5 PASS, B6–B8 PASS_SCOPED. Проверени са whole-text интерфейсът, finite/Noun режимите, noun agreement и negative conflict, graph subject, UTF-16 replacement/range guard, бързо писане, теми/mobile и word actions. Реален IME и injected delayed races не са упражнявани; responsive проверката е при една тясна ширина. Host-ът е graceful затворен. Това е local smoke с изричен rate240, без exported-runtime, public hosting или hosted CI claim; production default30 не е променен.

Следващите Preview6 checkpoints/публикационни твърдения и hashes са исторически snapshots; текущият matching complete runtime е bglang-neutral:36b0b69685d6d3e9, manifest SHA-256451e4f1bd85030d3c8d888084412644933ad6831b1314b4ee5106b4cd27963e7. Не ползвайте стария hash за текущото демо.

---

# BgLang Web: минимален public API consumer

Кандидатът обслужва `/demo`, `/docs`, `/api/v2/`, съвместимия `/api/v1/`, `/health/live` и
`/health/ready` чрез ASP.NET Core .NET 10. Демото „Думи, форми и словообразуване“ приема
собствен текст и отделна заявка, сравнява точни изписвания с всички върнати леми
и маркира UTF-16 offsets върху същия оригинален текст. Explorer на public API
операциите остава допълнителна възможност. Примерите са входове, не записани резултати.
Няма втори езиков двигател, frontend framework, database, accounts или LLM ключ.

## Публикационен статус

Този source checkout подготвя `v0.1.0-preview.6`; preview.5 е публикуван отделно. Нови public source/tag,
release assets, exported-source consumer резултати и CI още не са потвърдени.
Локалното приемане на API разширенията е отделно от проверката на distribution.
Използвайте source и пълен четирифайлов runtime от една и съща проверена версия;
[release guide](../../packaging/RELEASE-CANDIDATE.md) описва точните входове и проверки.
Предходните checkpoints, receipts и pending статуси по-долу остават исторически.
Историческите private `.tmp` и `Docs/` provenance identifiers не са public evidence
връзки и съответните файлове не се включват в публичния source export.

## Stress HTTP операции за preview.6

Трите операции са под `/api/v1/` и `/api/v2/`; JSON отговорите са camelCase. Изпращайте exact opaque EntryId и оригиналния raw fullTag. Полето fullTag е задължително в stressSelection, включително когато стойността му е null. Към тези операции не се изпраща limit; prototypeEntryId е приложимо само към generate-stress-forms.

| Route | JSON body |
| --- | --- |
| `POST /api/v2/stress` | `{"text":"😀 аванпост.","stressSelection":{"tokenIndex":1,"entryId":"legacy-base:64","form":"аванпост","fullTag":"Съществително:1,Мъжки"}}` |
| `POST /api/v2/generate-stress-forms` | `{"text":"космоаванпост","prototypeEntryId":"legacy-base:64"}` |
| `POST /api/v2/derivational-stress` | `{"text":"играч"}` |

Captured Legacy е provenance, не нова linguistic review. Display-only realization остава Inferred; InputObservation не е нормативно потвърдено ударение. Required source bindingId пази original capture identity. Prototype fit и derivational realization са qualified predictions с източник; липсващ или несвързан prototype дава unavailable/empty forms. Те не попълват Lookup/GenerateForms и не дават forward generation permission. Legacy offset arrays остават null, когато няма действителни captures. Stress rendering не предсказва sentence prosody.

Използвайте тези директни HTTP заявки; explorer-ът няма отделни stress controls. HTTP consumer tests проверяват действителните routes; това не е нова UI функционалност. Exact package4 manifest SHA за подготовката е `c137643e00ca568b2fcf249627c5c1396ab8a004dd63fed213f575a2faeedb12`. Нов distribution/hosted CI резултат се записва след изпълнение, отделно от приетите private tests.

## Подготовка и стартиране

Нужен е .NET SDK от repository `global.json` за build, ASP.NET Core 10 runtime
за framework-dependent deployment и **пълен, независимо избран release пакет**.
Host-ът отваря manifest чрез `BulgarianLanguageSession.Open`, валидира целия
пакет и сверява действителния `ManifestSha256` с предварително зададения хеш.
Няма автоматичен избор на „последния“ локален пакет.

От корена на release checkout:

Задайте `TEMP` и `TMP` към съществуваща директория с достатъчно свободно място
преди build/test/run и проверете child `[IO.Path]::GetTempPath()`. Използвайте
отделни fresh output/results директории. Конкретните workstation drives и
repository scratch aliases не са product или CI зависимости.

```powershell
dotnet build apps/BgLang.Web/BgLang.Web.csproj -c Release
dotnet build tests/BgLang.Web.Tests/BgLang.Web.Tests.csproj -c Release

$env:BgLang__ManifestPath = (Resolve-Path './runtime/manifest.json').Path
$env:BgLang__ManifestSha256 = '<приетият SHA-256 на manifest.json>'
dotnet run --project apps/BgLang.Web/BgLang.Web.csproj -c Release --no-build
```

Default listen е `http://127.0.0.1:5070`; `/demo` и `/docs` са на този адрес.
`--urls` / `ASPNETCORE_URLS` могат да зададат deployment endpoint. Startup
приключва само след успешен Open; проблемен пакет или различен хеш спира host-а.
Една сесия се използва през целия живот на процеса.

## Договор и истинност на резултата

POST операции: `lookup` (само Exact), `lookup-lexical-entries`, `lexical-entry`, `word-constructions`, `analyze-word`, `analyze-text`,
`generate-forms`, `derive-words`, `decompose-word`, `find-forms-in-text`. JSON за
public explorer: `{"text":"книга"}`;
за колекционните операции изричен `limit` от 1 до 256 се предава директно на
facade. Без поле limit: GenerateForms използва 128; Lookup, AnalyzeWord и
DeriveWords запазват Web default 32. Това са отделни HTTP defaults; публичният
facade има Lookup default 16 и DeriveWords default 16. Explorer-ът изпраща
избрания лимит: начален избор 256 за словоформи, 32 за другите колекционни
операции, с видимо поле за промяна. HTTP договорът е в `/docs` и `wwwroot/docs.html`.

`POST /api/v2/lookup-lexical-entries` с `{"text":"книга","limit":32}` извиква
`LookupLexicalEntries` и връща exact owners, разрешени преди presentation limit.
`data.metadataAvailable` различава пакет без annotations от owner с празни
`meaning`/`related`; `data.truncated` отчита отрязани owners. Омонимите остават
отделни. Значенията пазят буквалните strings, включително CR/LF, празни стойности,
повторения и ред. Релациите пазят raw `kind` и реда на групите/фразите.
Resolved фразата съдържа `words` с точни `entryId`/`form`/nullable `fullTag`
references. Ако изходна дума не може да се свърже точно, цялата фраза остава
в същата позиция с празно `words`, пълен `unresolvedSourcePhrase` от raw Word IDs
и `isResolved:false`. Тя не доказва singleton relation и не участва в текстови
съвпадения. `entry.relationsComplete` отчита тези source gaps за конкретния owner;
`packageHasUnresolvedRelations` ги отчита за целия зареден пакет и води до
`coverage.complete:false`. Това поле не оценява значенията или общата езикова пълнота.

`POST /api/v2/lexical-entry` с `{"text":"<entryId от първата заявка>"}` извиква
`FindLexicalEntry`. Идентификаторът се предава точно, без trim/нормализация;
няма входен `limit`. Важат текущите HTTP проверки за непразен вход до 256 UTF-16
символа. `data.entry:null` с `metadataAvailable:true` означава липсващ owner.
Двата маршрута са налични и под `/api/v1/`.

`AnalyzeText` добавя `data.lexical`: owners за Word токените с оригинални UTF-16
spans и exact срещания на декларирани многословни relation targets. Фразата сочи
изходния owner и occurrence индексите; тя не наследява неговото значение.
Whitespace може да свързва фразови токени, но пунктуация и newline separators
прекъсват съвпадението. Per-token owner limit е 32 с явен `truncated`.
Тези полета се запазват във всички response формати. Пакетът на preview 4 няма
annotations: новите заявки честно връщат `metadataAvailable:false`.

HTTP версията се избира чрез route и се връща в `apiVersion`. Без `evidenceFormat`
`/api/v2` използва `shared-analyses-v1`; `/api/v1` пази raw default. На двата
routes изрично се поддържат `raw-v1`, `shared-donors-v1` и `shared-analyses-v1`.
Raw пази inline public `data`; промяната на v2 default не е скрита смяна на v1 schema.
Всички readings/tags, graph paths, conditional-root evidence, original source
offsets, sources и kernel flags се запазват. JSON-ignore feature projections в
contracts остават JSON-ignore; C# API/contracts и source tags не се променят.

Двата shared формата добавят `evidenceFormat` и `donorSets` — списък
от пълни подредени donor масиви, споделени само в този отговор. Всеки graph path
заменя непразното или празното поле `conditionalSupportingLemmas` с
`conditionalSupportingLemmasRef`: целочислен индекс от 0 в `donorSets`.
Липсващият/null списък продължава да е липсващ, без reference.
Морфологичните кандидати, включително `shorterEndingCandidates`, заменят
`supportingLemmas` със `supportingLemmasRef` по същия начин. Всички други полета
остават непроменени. References не важат за друг отговор или пакет.

`shared-analyses-v1` добавя response-local `wordAnalyses`. Всеки TextAnalysis token
пази оригиналните `text`, `kind`, `start`, `length`, но заменя `analysis` с
`analysisRef`: индекс от нула в `wordAnalyses` или null при липсващ анализ.
Reference-identical WordAnalysis инстанциите се записват веднъж, в реда на
първата поява на токена. Kernel AnalyzeText вече memo-изира ordinal изписването;
различни инстанции не се сливат по дума, нормализация или подобно evidence.
`queryAnalysis` остава inline веднъж. Standalone WordAnalysis запазва своя data
shape; споделянето на token analyses се отнася до TextAnalysis токените.

Споделянето сравнява целите масиви с ordinal equality, включително реда;
еднакъв program/tag, брой или частичен overlap не са достатъчни.
Няма donor cap или промяна на cardinality, self-exclusion, unmet requirements,
sources, readings, exact/conditional authority и incomplete flags.
Разрешаването на donor и analysis references възстановява raw public `data`.
Donor списъците остават query-specific: не се заместват с общ cell ID или отделен
query-independent registry. Непознат `evidenceFormat` връща 400 `invalid-evidence-format`.
JSON използва UTF-8 кирилица със запазено HTML-sensitive/control escaping;
decoded стойностите и UTF-16 source positions са непроменени.

Старият API explorer изпраща POST към `/api/v2` без format opt-in; Live редакторът
изпраща явен `shared-analyses-v1`. Подробното evidence
и JSON се създават при поискване. Избраният текстов
токен използва първоначалния AnalyzeText резултат и неговите UTF-16 offsets;
отварянето на evidence не изпълнява нов AnalyzeWord. JSON disclosure показва
реалното HTTP представяне: оригинален token fragment с `analysisRef` и достъп
до целия отговор с `wordAnalyses` и `donorSets`. Resolver връща същия table object,
без hydration или копиране на analysis/donor масиви; един viewer показва пълния
избран donor списък, без отделен голям donor string във всяка карта.

Предходният checkpoint `afb8cc7e9718e4a074720dffa08be1cb5a001ed2` има изпълнени
Release builds с **0 warnings /0 errors**, Web suite **49 PASS /0 FAIL /0 SKIP**,
реални native23 HTTP и browser проверки. Actual HTTP body е 34,277,481 bytes raw
и 3,680,496 bytes при shared donors; това са конкретни predecessor measurements,
без general latency/resource или R5/R7 acceptance. Пълните резултати са в
predecessor validation receipt (исторически private provenance: `.tmp/public-mit-release/web/shared-donors-validation-receipt.json`; файлът не се разпространява).

Новият v2/response-lifecycle batch има Release builds с **0 warnings /0 errors**,
focused suite **69 PASS /0 FAIL /0 SKIP** и full suite **73 PASS /0 FAIL /0 SKIP**
поотделно с двата точно идентифицирани пълни пакета: historical native193
`a7a1c485cf2e8fc8a9b7a76b1a705c49db5918c75aebf18867256eb2f317a94a` и current native299
`e5233811daa3b4dacc3aa261029d37d6d24dd05d4ab70c9a01a6b01145da3270`.
Предходните 49 cases пазят имената, outcomes и ErrorInfo; 24 нови cases са PASS.
Това сравнение на тестове не означава еднакъв езиков резултат от двата пакета.

Реалните v2 HTTP entity bytes за native23 / 372 повторения / вход с 270 distinct
unknown думи са съответно **1,840,466 /802,994 /7,968,983** за native193 и
**1,823,891 /815,806 /7,087,104** за native299. Всеки успешен language отговор има
точен Content-Length; пълните подредени donor sets, proofs, token references,
UTF-16 offsets и kernel flags са проверени. Двата raw варианта на повторенията
връщат **413 с 254 bytes**, без успешно частично JSON; следващ lookup е 200.
Browser проверката с native299 потвърждава 1 exact /4 lemma matches, целия
conditional donor списък, правилните token offsets и споделения analysis при
372 повторения. Няма browser parse/render или general latency/resource claim.

Изпълнението и ограниченията са в
default-response validation receipt (исторически private provenance: `.tmp/public-mit-release/web/default-response-validation-receipt.json`; файлът не се разпространява)
за единния независим validated-batch review. **R5/R7 не са приети.**
Договорът и кандидатният profile са фиксирани преди implementation/measurement в
замразения source receipt (исторически private provenance: `.tmp/public-mit-release/web/default-response-receipt.json`; файлът не се разпространява).
Историческите receipts и неуспехи остават непроменени. Замразените преди build
source metrics (исторически private provenance: `.tmp/public-mit-release/web/default-response-metrics.json`; файлът не се разпространява) и свежите
validation metrics (исторически private provenance: `.tmp/public-mit-release/web/default-response-validation-metrics.json`; файлът не се разпространява)
отделят authored bytes/LOC от generated outputs и действителните Release boundaries.
[scope-metrics.json](scope-metrics.json) е непроменено историческо source-only
наблюдение от 9 октомври 2026 г. Старите counts и private receipt references
в него не описват текущата release validation.

Envelope:

- `apiVersion`, `versions`: HTTP contract и действителни informational assembly
  версии (с SDK revision suffix, когато съществува), плюс .NET runtime версия.
- `package`: всички полета на реалния `PackageProvenance`, включително hashes.
- `limits`: requested/effective/hostMaximum/hostLimitApplied/scope. `null`
  requested означава default или операция без native входен result limit.
  При изричен колекционен limit requested == effective, hostMaximum е 256,
  hostLimitApplied е false. Операциите без входен limit пазят отделните си scopes.
  Metadata routes отделят resultLimit (максимум) от defaultResultLimit,
  generateFormsDefaultLimit, textWordAnalysisLimit и queryAnalysisLimit.
- `coverage.hostTruncated`: false при успешен отговор — няма post-hoc slicing на public резултата.
- `coverage.possibleTruncation`: достигнат лимит на поне една collection.
  Лимитът на facade може да влияе на discovery/ranking; не правим по-голямо
  извикване и `Take` и не представяме capped discovery като завършено.
- `coverage.complete`: false при WordAnalysis kernel limit flag; иначе null,
  защото липсва explicit exhaustive-result proof. Никога не се измисля true.

При `find-forms-in-text` се подава `{"text":"...", "query":"книга"}` без
limit; text е до 4096, query до 256 UTF-16 символа, единична дума/лема без
whitespace. Host-ът последователно извиква AnalyzeWord(query, 32) и AnalyzeText.
Двете действителни analyses остават в JSON. Малка typed projection сравнява
цели Word tokens с query чрез ordinal case-sensitive равенство и joins всички
върнати WordReading.Lemma, GraphCandidate.DerivedLemma и MorphologyCandidate.Lemma
във всички lanes/shorter-ending children. Не изчислява нови форми и не използва
root Base или supporting donor lemmas като лема на производната дума.

Matches пазят tokenIndex и UTF-16 Start/Length. StoredLemmaMatches изисква
readings от двете страни; EvidenceLemmaMatches съдържа поне една хипотеза.
Ambiguity, unknown, conditional и incomplete се означават отделно, без
контекстно разрешаване. Conditional се отнася до действително споделена лема;
останалите условни пътища остават видими в оригиналните analyses/JSON.
Всички открити текстови matches се пазят; 32 ограничава основните списъци
Readings, GraphCandidates, Candidates, KnownBaseCandidates и BaseTypeCandidates
в query/word analysis, не броя tokens/matches. Вложени ShorterEndingCandidates
и supporting donor lemmas нямат общ лимит 32. Празен lemma резултат не доказва
езикова липса.
Двата панела slice-ват оригиналния TextAnalysis.Text по public offsets,
запазват whitespace/emoji и добавят text nodes, без innerHTML.
Click разрешава оригиналния token.analysisRef към същия AnalyzeText анализ и
показва реалния JSON фрагмент, а не нов AnalyzeWord с различен work budget.

При `AnalyzeText` няма входен limit и не се премахват токени/diagnostics;
32 е native cap на всяка word analysis. При `DecomposeWord` няма входен limit
и не се премахват segments/evidence. Само тези ограничения се показват в
операционния scope. Пълнотата на capped резултат не е доказана.

Демото показва всеки точен прочит отделно и позволява избор на лема. Public
`GenerateForms(lemma)` може да върне няколко парадигми със същата лема; UI пази
всички tags и не се преструва, че е избрал единствена парадигма. Щракване върху
форма извиква нейния анализ/разлагане и показва JSON и съответното C# извикване.
Примерите `книга`, `пила`, `супермравка` имат реални package acceptance
assertions; още не са проверени върху бъдещия финален release пакет.

`GenerateForms(lemma)` връща краен набор словоформи от пакета. Продуктивният
анализ на собствена незаписана дума чрез `AnalyzeWord`/`DecomposeWord` може да
върне допустим графов път, без да изисква предварително изброяване на всички
производни думи. `DeriveWords` прави ограничено търсене от основа. UI показва
действително върнатите основа, състояния, сегменти и идентификатори на
ребра/правила/приложения; не измисля междинни думи или операции от тези IDs.
Conditional-root support/неизпълнените exact-self requirements остават видими.
Confidence е оценка от пакета, не вероятност за езикова правилност;
ProductiveGeneration е само върнатото решение за избрания decomposition path.
Няма обещание за всички новообразувания. Крайният набор словоформи не доказва
M2 productive coverage: held-out незаписани комбинации, forward/inverse evidence
и отрицателни контроли се приемат в отделната kernel/package задача.

## Ресурсни и HTTP граници

UTF-16: дума/лема <=256, текст <=4096. HTTP request body <=32768 bytes,
включително JSON whitespace/escaping; Kestrel прилага границата и при chunked
transfer. Непознати JSON полета, празен/whitespace вход и невалидни limits
дават 400; content type дава 415; body size дава 413.

Кандидатният response profile допуска до **32 MiB (33554432 UTF-8 body bytes)**
за успешен language ApiEnvelope, конфигурируем чрез положителен
`BgLang:ResponseByteLimit`. Това е host engineering setting, не owner-imposed
или универсален acceptance таван. Всички routes/формати, включително raw,
минават count-only preflight преди response headers. Counting sink не пази
целия body; допуснатият резултат се сериализира с bounded write buffers.
Над границата: **413 `response-too-large`**, малък error JSON с
`coverage: {hostTruncated:true, possibleTruncation:true, complete:false}`.
Не се връща частичен 200 и не се режат proof/donor/token списъци.
Малкият bounded protocol error е извън language body cap, дори при настройка
по-малка от error bytes, за да няма recursive отказ. Фиксираните малки
metadata/health отговори също са извън този cap. Пълният profile още не е приет.

Един queue worker притежава kernel, projection, count preflight, сериализацията
и awaited success transfer до действителното приключване на language callback-а. Bounded channel
пази максимум 4 чакащи операции. При запълване — 503; queue wait максимум 2 s,
след което заявката се изключва от изпълнение и дава 503. Linked 10 s cooperative
deadline покрива четенето на request body, kernel, projection, count preflight,
сериализацията и awaited success transfer. AnalyzeWord/AnalyzeText и query
анализът проверяват cancellation в обратното търсене и генерирането на клетки.
Прекъсването спира реалната работа; slot-ът се освобождава след излизането ѝ.
Останалите синхронни операции проверяват token-а при вход/изход; преди headers
504 се връща след действителното приключване или прекъсване на операцията. След headers прекъснат отговор се abort-ва, без
дописване на error JSON tail. Това не е гаранция за Internet delivery time.
Малките фиксирани protocol `ErrorAsync` отговори (413/504 и други) се изпращат
извън queue worker-а и този deadline с `RequestAborted`; за тях няма безусловен
10-секунден error-transfer timeout.
Start/completion cancellation изчаква действителния започнат callback; не
освобождава линията преждевременно. Disconnect премахва чакаща задача, но не
допуска паралелна работа върху активната сесия или нов response callback.
Shutdown спира приемането, пропуска queued work и изчаква активната операция
преди session Dispose. При host shutdown deadline worker-ът продължава да
притежава сесията; принудително process kill е OS решение, не fake cancellation.

AnalyzeText запазва досегашните доказателства и бюджети. За съседни непознати
думи с еднакви последни до пет нормализирани символа използва повторно само
пълните replay резултати на проверени обикновени речникови донори. Всяка
логическа replay проверка продължава да се брои; при смяна на окончанието
донорският cache се изчиства, а хипотетичните леми остават локални за думата.
Сравнението подрежда речниковите прочити, известните основи, морфологичните
кандидати, хипотезите за лема и графовите пътища като изгледа за дума.
Еднакви презентационни редове се обединяват само при еднакви лема, вид,
условност, подредени тагове и източник; суровите анализи остават цели.

POST rate limit по подразбиране: 30 заявки/минута/непосредствен IP, включително
невалидните; при тази default конфигурация 31-вата дава 429 с Retry-After.
`BgLang:RequestsPerMinute` задава положителен целочислен rate, а metadata връща
ефективната стойност. Registry <=1024 IP entries; неизтекли entries
не се изхвърлят, за да се заобиколи rate limit. При capacity за нов peer — 503;
изтеклите windows се възстановяват без фонов timer/cache. Kestrel: <=64
connections, headers timeout 10 s, keepalive 30 s, без upgraded connections.

Всички forwarded headers се игнорират. Зад reverse proxy този default брои
proxy IP като един клиент. Публичният host profile трябва да използва директен
Kestrel HTTPS или **конкретно прегледан proxy trust profile**. Forwarded trust
няма да се включва само чрез произволен клиентски header. Няма implicit CORS.
Default loopback HTTP не е публичен production deployment.

Logging providers са изключени: няма retained application logs, input/body
logs, results cache или telemetry. Ако operator добавя service/proxy logging,
изключва bodies/входния текст и фиксира общ <=100 MiB retention с rotation в
реалния host profile. Това deployment изискване не е отчетено като вече прието.

## Проверки

```powershell
# И двата build-а по-горе, после small tests без package Open:
dotnet test tests/BgLang.Web.Tests/BgLang.Web.Tests.csproj -c Release --no-build --filter 'TestCategory!=FullPackage' --results-directory .tmp/public-mit-release/web --logger 'trx;LogFileName=web-small.trx'

# Хешът се задава от приетия release receipt, не се избира автоматично:
$env:BGLANG_WEB_TEST_MANIFEST = (Resolve-Path './runtime/manifest.json').Path
$env:BGLANG_WEB_TEST_MANIFEST_SHA256 = '<приетият SHA-256>'
dotnet test tests/BgLang.Web.Tests/BgLang.Web.Tests.csproj -c Release --no-build --results-directory .tmp/public-mit-release/web --logger 'trx;LogFileName=web-full.trx'
```

FullPackage тестът **FAIL-ва** при липсващ manifest/hash. Не skip-ва и не
замества package acceptance с synthetic fixture. Един Open споделя сесията
между реалния loopback Kestrel host и последователни direct public calls.
Проверява всички операции и целия JSON, hashes, assets/readiness, ambiguity,
незаписан graph path, UTF-16/body граници (с Content-Length и chunked), content
type/JSON/limit errors и непробиваем от forwarded header rate limit.
Основният text/query flow сравнява двете действителни analyses с direct calls и
проверява exact срещу inflected lemma попадения, case-sensitive изписване и
Cyrillic/emoji UTF-16 offsets. Small projection tests използват отделни,
hand-authored public-contract evidence за многозначни леми, unknown/conditional
пътища, всички candidate lanes, incomplete flags, abbreviation/stress/CRLF
source alignment и повече от 32 текстови попадения без clipping.
`GenerateForms("пиша", 256)` трябва да има >32 реални форми. Заявките limit=100
и limit=256 се сравняват със същия direct public limit; default заявката се
сравнява с direct 128. Проверяват requested/effective/hostMaximum и unknown
completeness, включително когато броят е под лимита. DeriveWords limit=256 също
се сравнява с direct 256, без скрит oversampling.

Small тестовете проверяват независим ownership при cancelled waiter, bounded
queue overflow/expiry и shutdown, както и IP registry capacity/window и UTF-16.
Те не претендират за package, morphology или host performance acceptance.
100-request latency profile, 10-minute load, TLS/restart/rollback и независимият
review остават за точния release candidate и избрания server profile.
Планираният p95 е <=250 ms за word операции и <=2 s за текст <=1000 UTF-16
символа, без startup/client Internet latency. Това не са измерени резултати.
По-големият разрешен текст се мери отделно; праговете не се намаляват при FAIL.

## Източници, dependencies и текуща валидация

Консултирани конкретни механизми преди писане:

- `src/BgLang.Api/Kernel/BulgarianLanguageSession.cs`: native operation limits,
  session Open/Dispose, per-text analysis и graph completeness flags.
- `src/BgLang.Contracts/Kernel/{PackageProvenance,WordAnalysis,TextAnalysis,
  TokenAnalysis,WordReading,WordForm,WordDerivation,WordDecomposition,MorphologyCandidate,
  MorphSegment,ExactSelfReadingRequirement}.cs`:
  съхранени domain contracts и JSON-ignore projections.
- `tools/BgLang.ApiConsumer/Program.cs`: пример за директна public dependency
  и разграничаване на stored reading/graph hypothesis; кодът не е копиран.
- `tests/BgLang.Tests/BgLang.Tests.csproj`: същите test package versions.
- Исторически private provenance: `Docs/START-HERE.md`, `Docs/PUBLIC-MIT-RELEASE-PLAN.md` — R5/R7 договор; файловете не се разпространяват.
- Microsoft [.NET Channels](https://learn.microsoft.com/en-us/dotnet/core/extensions/channels),
  [ASP.NET minimal APIs](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/minimal-apis?view=aspnetcore-10.0)
  и [hosted services](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/host/hosted-services?view=aspnetcore-10.0):
  bounded queue, HTTP hosting и lifecycle механизми.

Няма съществуващ ASP.NET host за замяна. Не е четен/копиран PublicConsumer,
test/reflection consumer или архивен morphology engine. Нов runtime NuGet
dependency няма: ASP.NET Core идва от Web SDK/framework. Test dependencies
вече съществуват в repository; реален Kestrel избягва нов TestServer пакет.

Source dependency edges: Web -> Public API; Web tests -> Web + ASP.NET shared
framework; API -> Kernel/Contracts остават непроменени. Нито Web, нито Web tests
са добавени в `BgLang.Api.slnx`. Замразеният predecessor `scope-metrics.json` от 9 октомври 2026 г.
отчита историческите authored bytes/LOC и source surface counts; evaluated build
boundaries са проверени за историческия run по-долу. Тези counts са source-only
история и не са актуални release counts.

Историческият Release build и Web suite от 2026-10-09 имат **45 PASS /0 FAIL
/0 SKIP**, с реалния `current-native-20261008/package/manifest.json`, SHA-256
`a7a1c485cf2e8fc8a9b7a76b1a705c49db5918c75aebf18867256eb2f317a94a`,
package `bglang-neutral:5a4d2a4cd67de6fb`. Финалният build има 0 warnings /0 errors.
Проверени са evaluated Compile/ProjectReference за петте проекта в closure и
действителните references на техните Release assemblies: няма зависимост към
Offline, Evidence, Corpus, Calibration, Benchmark или Legacy/UI.

Първите два full run-а запазват **44 PASS /1 FAIL /0 SKIP**: chunked oversized
заявката прекъсва вместо да върне 413. Малките JSON error responses вече имат
точен UTF-8 Content-Length; HTTP/1.x 413 затваря връзката, за да не се използва
отново с unread/faulted request body. Усилените assertions изискват цялото
JSON тяло, 413, framing, Connection: close и точния error code; Kestrel body
ограничението и всички функционални assertions остават. Точната причина за
първоначалното прекъсване не е установена с transport trace.

Run logs, TRX, source/binary identities и ограниченията на resource наблюдението
са в замразения predecessor execution receipt (исторически private provenance: `.tmp/public-mit-release/web/execution-receipt.json`; файлът не се разпространява);
[замразения predecessor scope-metrics.json](scope-metrics.json) от 9 октомври 2026 г.
пази историческия source-only cost delta, без текущ release validation claim. Това е Web
integration върху native193 преди shared-donors checkpoint-а. По-късните реални
HTTP/browser проверки и 49-case suite са в predecessor validation receipt по-горе;
те не приемат новия v2/byte/deadline batch. Финалният release/profile load,
graph/M2/R5/R7 acceptance и изолиран Open/startup performance резултат остават
неприети. Историческият full suite wall time е 24.227 s.

## Text composition — 2026-10-21

`AnalyzeText` добавя `readingInventory`, `metrics`, `spelling`, `profile` и `properNames`.

- `readingInventory` запазва пълните exact прочити преди показвания positional prefix от 32. Редът, дубликатите, original token indices и UTF-16 spans остават проследими; support reference може да сочи прочит след индекс 31. Canonical и support routes са отделни.
- `metrics` съдържа девет описателни брояча и отношения: tokens, Word occurrences, sentences, средни думи на sentence, средна UTF-16 дължина на Word, raw OrdinalIgnoreCase type/token ratio, punctuation, допълнителни normalized повторения и exact-unknown non-abbreviation ratio. Това са наблюдения върху текста.
- `spelling` запазва ordinary forward-index търсене с distance 2 и limit 10, source spans и ranked alternatives. `search`/`reason` разграничават пропуснато, guarded/unavailable и завършило търсене, включително празен резултат. Score означава ranking; решението за редакция остава при клиента.
- `profile` обобщава typed reading groups и coverage. Nullable full counts и projection issues разграничават непълна оценка от пълен празен резултат. Group counts описват наблюдавани алтернативи.
- `properNames` съдържа exact typed PROPN supports и maximal compatible groups. Всяка reference използва оригиналните `tokenIndex` и `completeReadingIndex`; всички exact алтернативи остават в inventory.

Unknown-context proper-name advice се включва изрично:

```http
POST /api/v2/analyze-text
{"text":"д-р Алдор.","includeUnknownContextProperNames":true}
```

При omitted, null или false advisory lanes не се изпълняват: `unknownContextEvaluated` е false и `unresolvedContextTokens` е null. При true се оценяват title, mid-sentence, surname и closed-clause lanes; enabled empty result използва празен списък. Graph BaseEvidence отлага unknown-context оценката. `completeForEnabledScope` и `deferredTokenIndices` показват обхвата; inferred context остава advisory evidence.

Полето е приложимо само към `analyze-text`. Изрично подадено false или true при друга операция връща 400 `option-not-applicable`; невалиден тип или непознато поле връща 400 `invalid-json` преди language work. `find-forms-in-text` запазва обичайния AnalyzeText overload.

Петте Text полета са налични и в трите response формата. Съществуващите request bounds, response budgets, deadlines и cancellation остават приложими. Typed `features` добавя nullable `nameKind`; това е additive serialization change спрямо предишния Text contract.

Support references са response-local: tokenIndex намира occurrence, readingSetIndex избира sets entry, а completeReadingIndex адресира supportReadings, когато не е null, иначе canonicalReadings. supportReadings:null означава неизползван support route; [] означава изпълнен support lookup с празен резултат. Display prefix и HTTP wordAnalyses са отделни index spaces.

Profile версията typed-reading-profile.v1 групира по изрично определения typed tuple. Разпределенията на POS и lemma броят displayed readings за всяко Word occurrence, включително повторенията. Projected group differences са representation observations; изборът на контекстно правилен прочит изисква отделна оценка.

Exact name candidates използват ANY typed PROPN support и запазват съществуващите common alternatives в inventory. Кандидатът и групата описват подкрепени spans; интерпретацията и entity identity остават отделни задачи.

## Контекст, синтактични роли и ограничени проверки — непубликувано

Текущият source checkout добавя `context`, `syntax` и `validation` към `AnalyzeText` и трите HTTP response формата. Това са bounded candidates и advice. Всички оригинални exact readings, дубликати и RAW UTF-16 spans остават в `readingInventory`; избраният контекст е отделен overlay. `context.available`/`syntax.available`/`validation.available` и `unavailableReason` показват наличността на входните данни. `context.sentences[].complete` описва пълнотата на входа, а не граматическа правилност.

Автоматично се оценяват седемте подготвени контекстни пътеки и пет основни роли: Predicate, Subject, Object, Complement и PrepositionalObject. `syntax.sentences[].predicateTokenIndex` е главното действително изведено сказуемо; останалите предложения са в candidates. Историческите `data.diagnostics` се запазват; новият `validation` е отделно evidence.

```http
POST /api/v2/analyze-text
{"text":"Иван каза че чете.","context":{"includeSubordinateCommaDiagnostics":true}}
```

Петте независими полета са `includeAdditionalRoleHeuristics`, `includeSubordinateCommaDiagnostics`, `includeFragmentDiagnostics`, `includeSpacingDiagnostics` и `includeWordConstructions`. Всяко е strict boolean с default false. Omitted/null `context`, `{}` и пет false стойности изключват петте opt-in разширения; автоматичните контекстни пътеки, основните роли и bounded validation остават активни. Първите четири опции и `includeUnknownContextProperNames` остават независими от конструкциите. Същият договор важи под `/api/v1/`.

При изключена опция съответното optional поле е null. При изпълнена налична проверка без попадение съответният списък с кандидати или diagnostics е празен. Enabled-empty `data.wordConstructions` е evidence object с празни collections. При липсваща предпоставка има изрична причина. Проверки, зависещи от Animacy/Transitivity, остават недостъпни: пакетът не предоставя такава typed authority. Default intensifier advice използва доказан тесен Bulgarian-letter обхват и текущия abbreviation механизъм; други форми на head остават изрично недостъпни. Няма автоматична поправка или общ parser.

`includeAdditionalRoleHeuristics` отчита точно девет groups: `unknown-preverbal-attribute`, `closed-register-title-attribute`, `closed-adverbials`, `nominal-possessive-clitic`, `preposed-object-clitic`, `special-preposed-dual-and-suppression`, `closed-da-short-clitic-subpredicate`, `closed-postverbal-clitic-subject`, `closed-dative-speech-subpredicate`. `candidateCount` брои изведените записи, приписани на конкретната Rule група; един запис се брои веднъж, а потиснато предложение дава нула. Тези contiguous модели изискват whitespace в едно sentence.

Всеки non-null `context`, включително `{}`, при друга операция връща 400 `option-not-applicable`. Непознато вътрешно поле и null/string/number вместо boolean връщат 400 `invalid-json` преди language queue. Request/response bounds, cooperative deadline и cancellation се запазват.

C# запазва старите overloads и добавя четири задължителни аргумента:

```csharp
TextAnalysis result = language.AnalyzeText("Ти работи.", false,
    new TextContextOptions { IncludeAdditionalRoleHeuristics = true },
    CancellationToken.None);
```

`AnalyzeText(text, default)` и `AnalyzeText(text, default, token)` остават еднозначни. Ръчно създаден стар `new TextAnalysis(text, tokens)` има null Context/Syntax/Validation. Новите nullable Features полета са `Case`, `IsShortForm`, `ParticipleKind`; null не означава Nominative, long form или някакъв подразбран вид причастие. Constructor/deconstruction tuple остава с 11 аргумента. JSON Features вече има 15 полета. `typed-reading-profile.v1` не включва трите нови axes в tuple, но корекциите на съществуващите Person/Number/Gender/Article и flags могат да променят групите и issues. Features record equality и serialization са additive compatibility промени.

Публикуваният [preview 4](https://github.com/bgtnt/bglang/releases/tag/v0.1.0-preview.4) е от 19 октомври и не съдържа тази последваща source партида. Отчет и проверими граници (исторически private provenance: `Docs/CONTEXT-SYNTAX-API-2026-10-21.md`; файлът не се разпространява).

## Кандидатни глаголни конструкции — непубликувано

Операцията `word-constructions` използва точно opaque `entryId` от `lookup-lexical-entries` в съществуващото поле `text`. Не го trim-ва, декодира или заменя с лема; няма fallback към друг owner. Примерите са заявки към текущия source checkout, без обещан записан резултат:

```http
POST /api/v2/lookup-lexical-entries
{"text":"пиша","limit":32}

POST /api/v2/word-constructions
{"text":"<точен entryId от data.entries на първата заявка>"}

POST /api/v1/word-constructions
{"text":"<същият непроменен entryId>"}
```

Не се подават `limit`, `query`, non-null `context` или `includeUnknownContextProperNames` със стойност false/true: те са неприложими за тази операция и връщат 400. Важат текущите проверки за непразен вход до 256 UTF-16, строг JSON преди language queue, трите `evidenceFormat` варианта, response budgets, cooperative deadline и cancellation. `limits.scope` е `scalar-evidence`; няма presentation limit или отрязване на own forms/кандидати.

| `data.status` | Значение |
| --- | --- |
| `OwnerMetadataUnavailable` | Няма lexical catalog за exact EntryId заявката. |
| `OwnerNotFound` | Catalog е наличен, но точно този идентификатор липсва. |
| `OwnFormsUnavailable` | Owner е намерен, но complete accepted own forms са недостъпни. |
| `NoApplicable` | Own forms са налични; няма приложим supported template, включително при наличен празен набор. |
| `Ready` | Има owner-bound кандидатни templates. |

`ownForms:null` означава недостъпност, а `ownForms:[]` — наличен празен набор. Заявката връща всички accepted own forms на избрания owner, включително техния ред и дубликати, и всички приложими templates с `familyId`, `targetConstructionTag`, `parts`, `basePartIndex` и локален `ownFormIndex`. Query може да съдържа и едночастни templates. Aliases не се сливат по лема.

Текстовото разпознаване се включва отделно с петата опция `TextContextOptions.IncludeWordConstructions = true` (default false), съответно `context.includeWordConstructions`:

```http
POST /api/v2/analyze-text
{"text":"😀 писал съм.","context":{"includeWordConstructions":true}}
```

Същата заявка е налична под `/api/v1/analyze-text`. Старите overloads и `AnalyzeText(text, includeUnknownContextProperNames, contextOptions, cancellationToken)` с четири задължителни аргумента се запазват:

```csharp
CancellationToken token = CancellationToken.None;
LexicalEntryLookup owners = language.LookupLexicalEntries("пиша", 32, token);
if (owners.MetadataAvailable && owners.Entries.Count > 0)
{
    WordConstructionResult candidates = language.GetWordConstructionCandidates(
        owners.Entries[0].EntryId, token);
}
TextAnalysis text = language.AnalyzeText("😀 писал съм.", false,
    new TextContextOptions { IncludeWordConstructions = true }, token);
```

Disabled `TextAnalysis.WordConstructions`/`data.wordConstructions` е null и не извършва owner pairing или own-form acquisition. Enabled резултатът е `word-constructions.v1`: `owners` пази owner status и complete own forms, `candidates` е споделена таблица, а всеки `matches` запис сочи `candidateIndex`, оригинални RAW UTF-16 `start`/`length` и `anchor.tokenIndex`/`anchor.completeReadingIndex`. Text включва само многосъставни templates, anchored в действителните complete exact readings, включително след display индекс 31. Запазва aliases, различни families/targets, дублирани прочити и отделните срещания; кандидат може да няма match. Съвпадението изисква Word части, whitespace и едно sentence, като запазва raw source span.

`complete` описва придобиването на exact owners/own forms в заявения обхват. При недостъпни own forms на owner резултатите на останалите owners се запазват; съответният `owners[].status` е `OwnFormsUnavailable`, `ownForms` е null и `complete` е false. Недостъпни pairing/key входове се посочват чрез действителни `unavailableReadings` references и `unavailableTokenIndices`. Неподдържан празен lookup без owner pairing остава unavailable token, без измислен reading 0; наличен изпълнен празен обхват е отделен случай и не доказва backend capability извън него. Без annotations Text може да запази реални candidates с `entryId:null` и валидиран `lexemeKey`; exact EntryId query остава `OwnerMetadataUnavailable`. Scoped `complete` не повишава глобалното HTTP `coverage.complete`.

Всеки кандидат има `acceptedWordForm:false` и `grammarAuthority:false`: template/match не установява lexical compatibility, граматическа правилност или corpus precision. `WordConstructionResult` отчита `productiveReplayAvailable:false` и `productiveReplayUnavailableReason:"OriginalGenerationSeedNotRetained"` означават, че оригиналният generation seed не е запазен; това не е отчет за неуспешна нова генерация. TextAnalysis вече има 15 сериализирани полета, като `wordConstructions` е additive nullable поле; старият positional constructor/deconstruction се запазва.

Публикуваният [preview 4](https://github.com/bgtnt/bglang/releases/tag/v0.1.0-preview.4) е от 19 октомври и не включва тази текуща source партида. Този раздел описва consumer договора; не заявява acceptance, review или test резултат за нея.


### Графова опора в основния Context/Syntax

Default AnalyzeText използва доказани nonconditional exact-root графови форми в старите двусловни nominal/pronominal Subject+finite Predicate клаузи и в затворено `ADJECTIVE* + един noun head + финален finite predicate`, без нов word-count cap. Всички определения споделят един и същ head tuple; number и singular gender трябва да съвпадат, plural gender може да липсва, а article equality не се изисква. Context ролите и Syntax supports имат optional `graphReadings` с `tokenIndex`, `graphCandidateIndex`, `fullTagIndex`; това са позиции в оригиналния отговор, различни от complete exact `readings`. `isKnown`, display32, inventory, редът и дубликатите не се променят. Nullable нови полета се пропускат, когато не се използват.

Expanded Context lane е `productive_nominal_phrase_graph.v1`. Syntax rules са `ProductiveNominalPhraseAttribute`, `ProductiveNominalPhraseSubject`, `ProductiveNominalPhrasePredicate`. Attribute target е original adjective token, а неговият Context support е действителният noun head. Subject target остава този head; Subject span покрива цялата contiguous nominal phrase. `Суперхубав супертрактор работи.` има непразни Attribute/head/predicate witnesses. Вътрешна пунктуация, няколко noun heads, coordination, pronoun+adjective chains и open input остават извън разширената структура.

`graphExactRootCandidatesComplete` се сериализира само при true и удостоверява точния root обхват. Глобалните `graphSearchIncomplete`/`graphCandidatesTruncated` запазват значението си: отрязана условна опашка не е загуба на exact-root кандидат, а incomplete или изгубен exact-root кандидат изключва graph selection. Външните graph/analogy/known-base хипотези остават отделни. Не се извежда вероятност от Confidence или ranking score.

Subject–predicate несъгласуването `productive_morphology_agreement_mismatch` и отделното attribute–head `productive_attribute_agreement_mismatch` пазят original UTF-16 span на засегната graph дума, optional exact `readings` и `graphReadings`, с `projectedText:null`. Diagnostic refs са само от действителното отношение; graph другаде не дава authority. `Суперхубав суперкнига работи.` няма predicate person/number mismatch. Демото обозначава такава опора като „Графова опора (хипотеза)“. При „Ти чете.“ съвместимият preview не избира единствена морфология и запазва трите лицензирани full tags. Article/construction проверките пазят exact-only authority.

Версиите са `bounded-context.v2`, `bounded-syntax-roles.v2`, `bounded-validation.v2`. v1 default raw, v2 default shared-analyses-v1 и explicit raw/shared-donors/shared-analyses запазват целия отговор при реконструкция.

## Finite продължения в Live

Live включва `{ "context": { "continuation": { "caret": 30, "limit": 12, "target": "Auto" } } }` в същия `analyze-text` request. Caret е действителната оригинална UTF-16 позиция, не примерна константа за всички входове. Единният queue/rate budget се запазва; отделната `lookup-prefix` заявка не се използва за контекстните предложения. След whitespace се показват реални форми от избрания lane, а частичният prefix ги стеснява. UI обозначава действителния lane, подкрепената person/number или number/gender/article интерпретация, exact/graph опорите, dictionary score и coverage.

При избор се заменя целият captured token. Text/caret/revision и избраният режим трябва още да съвпадат; range selection, IME composition, небезопасна surrogate/combining граница и out-of-order отговор не прилагат предложение. Смяната на режима обезсилва изчакващия snapshot и старите отговори. Документът и source-aligned анализът споделят един snapshot; десният текст се запазва, без claim за неговата граматика.

Nested limit използва текущия HTTP bound1–256. Невалидните опции връщат `invalid-continuation-limit` или `invalid-continuation-caret`; context за друга операция остава `option-not-applicable`. Старият top-level `limit` не се използва за `analyze-text`. Raw/shared-donors/shared-analyses връщат същия whole-envelope evidence, а без opt-in continuation полето липсва. Общо article-placement и граматика вдясно остават извън този локален договор.

### Локални Noun/Auto продължения в Live

Noun/Auto е прието и локално commit-нато в `44bb169b`; текущата Preview9 distribution остава кандидат, без нова публикация или hosting claim. Omitted `context.continuation.target` запазва API default `Finite`, съществуващия finite отговор и C# constructor/deconstruction. Live изпраща избрания `Auto`, `Finite` или `Noun` в същата caret-aware заявка; няма второ lookup или frontend tag grammar. Примерът е вход към локалния source, не записан резултат:

```http
POST /api/v2/analyze-text
{"text":"Нова ","context":{"continuation":{"caret":5,"limit":12,"target":"Noun"}}}
```

`Noun` допуска само `ADJECTIVE+` в същия whitespace-only ляв sentence fragment преди празен или частичен noun prefix. Всички определения трябва да подкрепят един `Number`/singular `Gender` tuple; plural gender може да липсва. Първото пази лицензирано `Indefinite`, generic `Definite` или masculine-singular `Full`/`Short`, като generic `Definite` не става `Full`/`Short`. Следващите определения и candidate common noun са `Indefinite`. Count, неподдържани Case/short-form markers и неизвестни или конфликтни axes не дават support. Няма нов word-count cap, inferred Subject или обща article/construction authority.

`Auto` избира finite, когато има лицензиран finite контекст, преди да започне dictionary discovery. Само при неподдържан finite контекст може да избере подкрепения noun контекст. Zero results, unavailable source, error, truncation и cancellation не са причина за второ търсене в друг lane. Изричният „Съществителни“ дава достъп до други adjective интерпретации в многозначен контекст; finite-first приоритетът не е unique parse.

Действителният noun отговор има `target:"Noun"`, `version:"nominal_continuation.v1"`, `candidateSource:"exact_package_nominal_vocabulary"`, `expectations:[]` и `nominalExpectations` с `number`, nullable `gender`, `firstAttributeArticle`, `nounArticle` и оригинални `attributes` refs. UI показва тези полета и exact/graph refs през DTO, без да повишава опорите в Context roles. `matches[].expectationIndices` адресира само активния nominal списък; пълните readings, duplicates и останалите homographs се пазят. Finite version/source/expectations са същите, default `target` и null `nominalExpectations` се пропускат. `available`, `truncated` и `searchComplete` остават отделни, ограничени до избрания vocabulary scope; cancellation не става complete-empty.
