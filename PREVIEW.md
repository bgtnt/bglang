# BgLang — публичен preview

Това е `v0.1.0-preview.1`: провереният библиотечен API и пакет от приетия source commit `7ab50285d3dd13c7e29d972e3820c6fa65cb28e1`, публикувани за употреба и независим преглед. Това още не е завършен Legacy паритет или корпусно валидирано издание.

## Изтегляне и изпълнение

Нужни са Windows, PowerShell 7 и .NET SDK **10.0.401**. [Preview изданието](https://github.com/bgtnt/bglang/releases/tag/v0.1.0-preview.1) доставя:

- `manifest.json`, `lexicon.v3.jsonl`, `word-formation.v0.json`: трите неизменни **входа за изграждане** (269 875 108 bytes).
- `bglang-native-9b1115996e1790af.zip`: **готовият runtime пакет**, съдържащ трите output файла (51 083 457 bytes преди компресия).
- `SHA256SUMS`: SHA-256 на четирите assets.

Входните и runtime файловете имат еднакви имена, но различни роли и hashes. Дръжте ги в различни директории. [Delivery contract](packaging/public-delivery.json) и [input lock](data/dictionary/public-input-lock.v1.json) определят точните bytes. Актуалният download адрес е в delivery contract. Input lock остава точният приет договор преди публикацията; неговите стари publication/status полета са исторически metadata, а не състоянието на GitHub доставката.

От checkout директорията:

```powershell
./packaging/Get-PublicInputs.ps1 -Destination ./public-inputs/corrected151-stored-v1
$run = Join-Path $env:TEMP ('bglang-preview-' + [Guid]::NewGuid().ToString('N'))
./packaging/Build-PublicPackage.ps1 -InputDirectory ./public-inputs/corrected151-stored-v1 `
    -OutputDirectory (Join-Path $run 'package') -Test -ResultsDirectory (Join-Path $run 'results')
```

За директно използване изтеглете runtime ZIP от изданието, проверете SHA-256 спрямо `SHA256SUMS`, разархивирайте го в нова директория и изпълнете:

```powershell
dotnet run --project examples/BgLang.PublicExample/BgLang.PublicExample.csproj `
    -c Release -- ./package/manifest.json
```

Самото API проверява runtime пакета. Примерът ползва само публичния API и отпечатва реалните резултати за български думи и текст.

## Проверки и граници

Двата приети локални чисти exports минават по 8 публични теста, 3 builds без warnings/errors и реалния API пример; runtime файловете им са byte-identical. Приетият предходен пълен вътрешен suite има 2850 PASS и 13 исторически SKIP. Това не е твърдение, че вътрешният suite е включен в този публичен repo. Новите hosted проверки се виждат в [GitHub Actions](https://github.com/bgtnt/bglang/actions/workflows/windows-public.yml).

Последната приета Legacy съпоставка съдържа 106 757 IDs: 106 185 equal, 482 unsupported, 76 Type151 mismatches, 13 reviewed Legacy defects и 1 refusal. Отложените случаи и stored съвместимостта не се броят за завършване. Текущата неприета работа по следващи групи не е включена тук. Известните грешки на Legacy не са граматичен стандарт.

Анализът може да връща хипотези, incomplete/clipped и work/evidence flags. Тежкият наблюдаван текст се анализира директно за около 13 секунди; отделният локален HTTP host връща контролиран 504 около 10 секунди и възстановява обслужването. Web demo и hosted HTTP endpoint не са част от този export. Няма обещание за peak-memory или cold-open паритет.

Пълните три корпуса и донастройването предстоят след Legacy етапа. Публичният preview не отменя тази работа.

## Промени и review

Issue или PR е добре дошъл с конкретни думи, пълни очаквани форми/тагове, източник и възпроизводими стъпки. Приети поправки се пренасят в основната разработка, проверяват се там и се изнасят обратно по [договора за синхронизация](PUBLIC-SYNC.md). Публичното repo има собствена история; частната история не се прехвърля.

Кодът и авторските езикови данни са [MIT](LICENSE). Зависимостите запазват [своите лицензи](THIRD-PARTY-NOTICES.md).
