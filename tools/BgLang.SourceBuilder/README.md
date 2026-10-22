# Изграждане на neutral source пакет

Този отделен инструмент за Windows/.NET 10 превръща редактируем v1 JSONL или v2 JSON документ
и изрично подаден reviewed graph в пакет, който се отваря с
`BulgarianLanguageSession.Open`. Той използва съществуващия graph и strict loader.
Няма автоматично одобрение на лингвистичните данни или зависимост от Legacy.

От корена на repository, с нова изходна директория и съществуващ родител:

```powershell
dotnet run --project tools/BgLang.SourceBuilder -c Release -- build tools/BgLang.SourceBuilder/example.source.jsonl data/word-formation/word-formation-source.v0.json artifacts/my-new-package
```

## v1: съществуващият JSONL формат

Командата извежда JSON с manifestPath, sourceId, packageId, lexemes и readings.
Изходът съдържа `lexicon.v3.jsonl`, точно копие на graph като
`word-formation.v0.json` и `manifest.json`. За пренасяне копирайте цялата build
директория на инструмента и подайте абсолютни пътища към source, graph и target.
Инструментът и проверките му не са част от default solution.

`retain-lemmas <manifest.json> <source-bound-selection.json> <new-package-directory>`
коригира избрани explicit записи от unannotated v1/v2 пакет. Selection е strict
`bglang-retain-lemmas.v1` с `manifestSha256`, `lexiconSourceId`, `lexiconSha256`
и непразен `targets`: всеки target задава точните `lexemeKey`, `lemma`,
`readingCount`, `rowSha256`. Последният hash включва финалния LF на оригиналния
UTF-8 ред. Повторени, липсващи или различни anchors се отхвърлят.
Избраният запис трябва да е stored, без productive rule и с налична собствена
базова форма. Той се заменя с един reading със същите form/lemma, `fullTag:null`
и `score:1`. Другите записи, хомоними, scores и routing се запазват.
Примерната reviewed selection е
`data/morphology/reviewed-stored-lemma-selection.v1.json`.

Командата проверява целия predecessor и държи входовете заключени за четене.
Малък converter подава explicit authoring v1 към същия builder; изходът е
unannotated `lexicon.v3.jsonl`, graph byte-for-byte и нов manifest с нови source/
package identities. Не се добавят generated declarations. Старият пакет остава
неизменен; output трябва да е нов. Това е версия на данните, а не runtime забрана
на думи. Annotations и generated predecessor декларации не се поддържат от тази
операция. Големите проверки и build използват паметта на съществуващия builder;
source compression или успешен Open не доказват resource parity.

`promote-native <manifest.json> <current-graph.json> <new-package-directory>`
преобразува всички допустими explicit stored записи от unannotated v1/v2/v3
lexicon към native generated декларации през същия builder. Вход с вече generated
редове не се поддържа. Кандидатите идват от paradigm и lexical-clitic bindings
в подадения graph; census mapping не е необходим. Изискват се пълно равенство на
суровите form/lemma/tag, кратността и битовете на score. Непразен оригинален
productiveRuleId не се заменя с друг; при null и няколко равни bindings изборът
е детерминиран по ordinal rule/tag ред. Routed, неподдържани и неканонични native
записи остават stored. Групи с конкуриращи се сурови изписвания между lexemes
се запазват, за да не се промени представителят или редът на `GenerateForms`.
Непромотираните JSON редове се запазват; крайният файл остава подреден по key
и с LF. Няма голям временен authoring файл. JSON резултатът съдържа identities
на входовете, promoted/retained lexeme и reading counts и причините за запазване.
Пакетът е нова версия; source/package identities и reading provenance се променят.

`measure-open <manifest.json>` измерва едно публично `Open` и връща JSON с
provenance, време, наличните runtime фази и peak working set на целия процес.
За process-cold сравнение стартирайте командата в нов процес за всеки пакет.
Няма заявки или принудителен GC в измерването; OS disk cache не се изчиства.
Малкият размер на generated source не означава по-малка runtime материализация.

Всеки непразен ред е един от три варианта с `schemaVersion` равен на
`bglang-neutral-authoring.v1`:

| kind | Полета след schemaVersion/kind | Значение |
|---|---|---|
| productive | lemma, productiveRuleId, baseFullTag, score | Пълно генериране през изрично посочения accepted rule и тип |
| stored | lemma, productiveRuleId (string или null), readings | Изрично зададени неправилни, готови или хомонимни readings |
| routed | Полетата на stored плюс route=`exact_prefix_only` | Readings само за exact/prefix lookup |

Всяко reading съдържа точно `form`, `lemma`, `fullTag` (string или null) и
`score`. Запазват се суровите стойности и хомонимите. Не задавайте keys, counts
или digests: инструментът ги изчислява по runtime договора. Неизвестни полета,
повторени JSON properties, невалидни scores и semantic/routing collisions са
грешки. Productive lemma следва текущата ограничена Bulgarian-token политика.
Декларираният rule/type трябва да има пълна поддържана генерация до 256 readings.

За добавяне по подобие копирайте подходящ productive ред и сменете lemma.
За редакция сменете изричните полета, а за премахване изтрийте целия ред.
Проверете промяната и построете нов target. Предишните пакети и отворени сесии
остават неизменни. v1 не поддържа stress annotations, Meaning/Related или
прехвърляне на връзки. Празен v1 source, премахване на последния v1 lexeme
и v1 source само с routed readings се отхвърлят. За тези операции използвайте v2.

v1 source е strict UTF-8 с optional UTF-8 BOM и LF/CRLF; празни редове и UTF-16/32
се отхвърлят. Graph е подаденият UTF-8 JSON snapshot и се копира byte-for-byte.
При еднакви graph bytes редът на lexemes/readings и source BOM/line endings не
променят output bytes. Source и manifest са UTF-8 без BOM с финален LF.
`createdAtUtc` е фиксиран на Unix epoch за възпроизводимост; не е време на build.

## v2: записи, ударения, Meaning и Related

Един JSON документ с `schemaVersion: "bglang-neutral-authoring.v2"` съдържа
`entries`. Всеки запис има точно `entryId`, `lexeme`, `meaning`, `stress`,
`related`; вложеният `lexeme` използва същата v1 декларация от таблицата.
Например запишете следното като `artifacts/my-source.json`:

```json
{
  "schemaVersion": "bglang-neutral-authoring.v2",
  "entries": [{
    "entryId": "bridge-1",
    "lexeme": {
      "schemaVersion": "bglang-neutral-authoring.v1",
      "kind": "stored", "lemma": "мост", "productiveRuleId": null,
      "readings": [{"form": "мост", "lemma": "мост", "fullTag": null, "score": 1}]
    },
    "meaning": ["Примерен редакционен запис."],
    "stress": [{"form": "мост", "fullTag": null, "vowelOrdinals": [0]}],
    "related": []
  }]
}
```

Това е пример за формата на данните, не reviewed лингвистичен източник.
`entryId` е уникален непразен редакционен идентификатор без околни интервали.
Запазете го при промяна на lemma/type/metadata; за нов запис задайте нов ID.
Пълните еднакви lexical декларации могат да имат отделни собственици с различно
Meaning/Related. Съхранява се един lexical inventory и metadata веднъж на собственик.

`meaning` пази подредени текстови редове, включително повторения и празни редове;
вътрешни CR/LF в един ред не се разрешават. `stress` сочи точна налична двойка
`form`/`fullTag` на собствения запис. `vowelOrdinals` е непразен подреден списък
без повторения на индекси на гласни, започващи от 0. Това не са UTF-16 позиции.
`carry-stress` затваря редакционното пренасяне/отпадане на ударения за
productive v2 entries с точен base seed. Той запазва базовото ударение и в двата
режима, проектира само единодушни оригинални гласни преди реална замяна и оставя
противоречивите пътища unresolved. По подразбиране старите non-base корекции се
запазват; `--replace-form-stress` премахва всички non-base overrides, включително
ръчните, и ги изчислява наново. Same-surface/different-fullTag е обикновена
non-base проекция, а не автоматично unresolved. Runtime връща отделно lexical
stress evidence само когато всички readings и собственици на повърхността са
единодушни; липса или различие води до въздържане. Explicit, inferred и
ReviewedExact evidence запазват отделните си значения.

`related` е подреден списък от `{"kind":"синоним","phrases":[...]}` групи.
Всяка непразна фраза е списък от `{"entryId":"bridge-1","form":"мост","fullTag":null}`
препратки към точни налични readings на посочения собственик. Празни групи/фрази
и неизвестни етикети се отхвърлят. Поддържат се 27 точни Legacy етикета, включително
`unknown` за неуточнена връзка; това не предоставя inference authority. Редът,
повторенията, forward/self/cyclic препратките се запазват. При редакция на форма
или tag актуализирайте засегнатите stress/Related записи; невалидните връзки се отхвърлят.

```powershell
dotnet run --project tools/BgLang.SourceBuilder -c Release -- build artifacts/my-source.json data/word-formation/word-formation-source.v0.json artifacts/my-annotated-package
dotnet run --project tools/BgLang.SourceBuilder -c Release -- export artifacts/my-annotated-package/manifest.json artifacts/my-export
dotnet run --project tools/BgLang.SourceBuilder -c Release -- build artifacts/my-export/lexemes.source.json artifacts/my-export/word-formation.v0.json artifacts/my-rebuilt-package
dotnet run --project tools/BgLang.SourceBuilder -c Release -- remove artifacts/my-annotated-package/manifest.json bridge-1 artifacts/my-empty-package
```

v2 изходът е `bglang-neutral-runtime-package.v2` с точно четири файла:
`manifest.json`, `lexicon.v3.jsonl`, `word-formation.v0.json` и
`lexeme-annotations.v1.json`. Build резултатът добавя броя `entries`.
`{"schemaVersion":"bglang-neutral-authoring.v2","entries":[]}` и source само с
routed записи са валидни; празният речник запазва независимите текстови анализатори.
v2 документът е strict UTF-8 с optional BOM и LF/CRLF; форматирането може да е
многоредово. Редът на entries/stress се канонизира, а Meaning/Related редът има значение.

Export приема само валиден v2 пакет и възстановява редактиращ source плюс точните
graph bytes без първоначалния input. Пакетите от SourceBuilder v2 се възстановяват
байт по байт. Други валидни пакети със собствен JSON формат или timestamp запазват
lexical/annotation съдържанието и graph bytes, но output/timestamp се канонизират;
package ID и bytes могат да се променят. Export на v1 не се поддържа.

Remove по подразбиране отказва, ако запазен запис сочи към изтривания. За явна политика:

```text
remove <manifest.json> <entryId> <new-package-directory> --drop-inbound-phrases
remove <manifest.json> <entryId> <new-package-directory> --transfer-to <targetEntryId>
```

Drop премахва цялата засегната фраза, след това празните групи. Transfer изисква друг
съществуващ получател с действителна форма, съвпадаща с изтриваната lemma без разлика
в регистъра. Добавя Meaning и Related в края, запазва target stress и пренасочва всички
запазени/добавени препратки. Всяка пренасочена точна form/tag двойка трябва да съществува
при получателя, иначе цялата операция отказва. Двете политики не се комбинират.
Изтриване на един собственик запазва общите форми при оставащ собственик, но не разрешава
висящи entry IDs. Изтриването на последния запис създава валиден празен v2 пакет.


### Carry/drop за productive v2 запис

Източникът трябва да е `kind: "productive"`, а точният base reading трябва да
има една stress annotation. Запишете този пример като
`artifacts/stress-source.json`; той задава base seed за `барабаня` и оставя
графа да изчисли non-base редовете:

```json
{
  "schemaVersion": "bglang-neutral-authoring.v2",
  "entries": [{
    "entryId": "entry-00",
    "lexeme": {
      "schemaVersion": "bglang-neutral-authoring.v1",
      "kind": "productive",
      "lemma": "барабаня",
      "productiveRuleId": "productive.verb.type174ii.full_mini_forms.v0",
      "baseFullTag": "Глагол:174ii,1л.,Сегашно",
      "score": 1
    },
    "meaning": [],
    "stress": [{
      "form": "барабаня",
      "fullTag": "Глагол:174ii,1л.,Сегашно",
      "vowelOrdinals": [1]
    }],
    "related": []
  }]
}
```

Постройте v2 пакета, след което изпълнете carry в нови target директории. Този
пример има точно един exact base reading и 56 non-base readings при изход от
57 readings; отчетът carry използва тези точни `(form,fullTag)` двойки.

```powershell
dotnet run --project tools/BgLang.SourceBuilder -c Release -- build artifacts/stress-source.json data/word-formation/word-formation-source.v0.json artifacts/stress-package
dotnet run --project tools/BgLang.SourceBuilder -c Release -- carry-stress artifacts/stress-package/manifest.json entry-00 artifacts/stress-carried
dotnet run --project tools/BgLang.SourceBuilder -c Release -- carry-stress artifacts/stress-package/manifest.json entry-00 artifacts/stress-replaced --replace-form-stress
```

Резултатът отчита `N/C/P/U` по точни двойки `(form,fullTag)`:
`N = C + P + U`, където `N` (`nonBaseReadings`) изключва само точния base
`(lemma,baseFullTag)`; `C` (`carriedReadings`) са пренесените, `P`
(`preservedReadings`) са запазените, а `U` (`unresolvedReadings`) са нерешените
non-base readings. В режим replace `P = 0`. Base annotation се запазва и в двата
режима. По подразбиране старите non-base annotations остават, а
`--replace-form-stress` изтрива всички non-base overrides, включително manual
корекциите, преди да приложи единодушните graph projections. Same-surface с
различен `fullTag` е обикновена non-base проекция; unresolved означава липсващ
surviving stress или противоречиви пътища. Source файлът, входният пакет и
отворените сесии остават непроменени.

## Публикуване и проверки

Всички build/export/remove/carry-stress/retain-lemmas/promote-native/correct-stored команди изискват нов target със съществуващ родител.
Source, предишният пакет и вече отворените сесии остават неизменни.

Публикуването използва изключително придобита sibling staging директория,
валидира пакета чрез public Open и го преименува към новия target. Съществуващ
target никога не се заменя. Отказаното придобиване не разрешава cleanup.
Reparse ancestry се отхвърля и се проверява повторно преди запис/publish/cleanup.
Гаранцията е атомарна видимост на Windows; Unix се отхвърля преди filesystem
промени. Няма обещание за power-loss durability, cleanup при принудително
прекратяване или защита от непрекъсната злонамерена подмяна на filesystem paths.
След успешно rename повреден stdout може да върне грешка, но пакетът остава.

Самостоятелни bounded process проверки (Windows със symbolic-link права):

```powershell
dotnet build tools/BgLang.SourceBuilder -c Release
dotnet build tools/BgLang.SourceBuilder.Checks -c Release
dotnet tools/BgLang.SourceBuilder.Checks/bin/Release/net10.0/BgLang.SourceBuilder.Checks.dll tools/BgLang.SourceBuilder/bin/Release/net10.0/BgLang.SourceBuilder.dll .
```

Проверките използват замразения независим oracle 31 roots/1215 readings и
собствени временни директории под `.tmp`, включително ACL и symlink случаи.
Те не строят външни пакети и не изпълняват broad corpus.

## Точни корекции на complete stored source

`correct-stored <manifest.json> <source-bound-replacements.json> <new-package-directory>`
прилага предварително обосновани пълни корекции на избрани v1 stored owners.
Входът е точният unannotated пакет с три файла и само explicit stored/routed
редове. Generated/native вход се отказва. Лингвистичната обосновка и независимо
проверените следващи публични очаквания остават задължение на автора на selection.

Selection има `schemaVersion: "bglang-correct-stored.v1"`, `manifestSha256`,
`lexiconSourceId`, `lexiconSha256` и непразен `targets`. Всеки target съдържа
`lexemeKey`, `rowSha256` на целия оригинален RAW ред **включително LF**,
`readingCount` и `replacement`: пълна `bglang-neutral-authoring.v1` декларация
с `kind: "stored"`, същите `lemma`/`productiveRuleId` и всички коригирани
`readings`. Частични patches и повторени/липсващи owners не се приемат.

Само избраните редове минават през съществуващия Materialize. Publish запазва
оригиналния текст на останалите редове, graph bytes/sourceId, strict identities,
public Open и атомарния fresh-target протокол. Новите keys се сортират ordinal:
абсолютните позиции може да се сменят, но RAW редовете и относителният ред на
неизбраните owners остават точни. Owner count е постоянен; reading count може
да се промени. Резултатът съдържа `package`, `correctedOwners`, `beforeReadings`
и `afterReadings`. Следващото `promote-native` получава този пълен stored
successor и отделно текущия graph.

Фокусираните CLI controls се изпълняват с `--stored-correction-only` към
`BgLang.SourceBuilder.Checks`. Те проверяват точни preimages, пълна корекция,
foreign-owner unions/route, RAW/order preservation, reproducible twins,
отказ на грешни maps/generated вход и create-only publication.

## Compact stress source за preview.6

Release recipe използва exact locked `bglang-neutral-authoring.v5` source с required `stressPattern.bindingId` и qualified `accentParadigm`. Подайте `lexemes.source.json` и exact locked graph към съществуващия `build`. Runtime annotations source е v4, а manifest schema е v2; тези версии имат различни роли.

`export` пази bindingId. Редакция на complete form/full-tag inventory, owner/source, captured reading index или WordId с остарял binding се отказва при build/open; physical reorder се канонизира. Не пресмятайте нов binding върху променени данни, за да прехвърлите стар sparse capture. Липсващ capture остава Unknown. Legacy import/compact-stress са offline authoring операции и не са нужни за публичното reproduction на вече подготвения input. Qualified fit не е нормативна гаранция за нова дума.
