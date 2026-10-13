# Синхронизация на публичния snapshot

Частното хранилище е източникът на приетите промени. Публичното хранилище
получава само файловете от `packaging/public-source-allowlist.v1.json` в
изрично избран, вече проверен commit. Частната Git история не се прехвърля.
Инструментът не прави build, commit, push или обратна синхронизация.

## Нормален цикъл

1. Приеми и commit-ни завършената промяна в изходното хранилище.
2. Използвай отделен чист публичен checkout. При клониране за синхронизация:

   ```powershell
   git -c core.autocrlf=false clone <public-repository-url> <public-checkout>
   git -C <public-checkout> config core.autocrlf false
   ```

   Така обикновените файлове запазват Git blob bytes. Изричният `eol=crlf`
   за графа продължава да действа. Синхронизацията отказва редактирани файлове,
   включително различен EOL, вместо да ги нормализира скрито.
3. От публичния checkout изпълни с Python 3 и Git в PATH:

   ```powershell
   python tools/sync_public_snapshot.py --source <private-checkout> --commit <full-accepted-commit-id> --target <public-checkout>
   ```

   Първото изпълнение с preview надпис задава допълнително:

   ```powershell
   --readme-prefix-file packaging/public-readme-prefix.md
   ```

   Следващите изпълнения наследяват същия prefix. Ако го редактираш, прегледай
   промяната и подай опцията отново. Неговите точни UTF-8 bytes се добавят пред
   upstream README; upstream файлът и останалите договори не се редактират.
4. Прегледай публичния diff и новия `public-source-manifest.json`. Изпълни
   проверките, които съответстват на промяната, и commit/push само след review.
   Ако има публична поправка по managed файл, пренеси я първо в изходното
   хранилище за нормалното му приемане; после възстанови публичния checkout до
   предишния snapshot и синхронизирай приетия commit. Няма автоматичен merge.

Не използвай `git add .`: upstream `.gitignore` изключва `/data/`. Stage-ни
точно имената от manifest, включително разрешените ignored файлове:

```powershell
$snapshot = Get-Content -LiteralPath public-source-manifest.json -Raw | ConvertFrom-Json
$paths = @($snapshot.files.path) + @($snapshot.publicOnlyFiles.path) + @('public-source-manifest.json')
git --literal-pathspecs add -f -- $paths
```

Ако snapshot премахва стар managed файл, прегледай `git diff --name-status` и
stage-ни отделно точно неговия deleted path с `git --literal-pathspecs add -u -- <path>`.
Синхронизацията отказва всякакви вече staged промени в target index; приключи
или unstage-ни съществуващата работа преди следващо изпълнение.

## Какво се запазва и проверява

- `--commit` приема пълен object ID; `HEAD`, branch и съкратени SHA се отказват.
  Четат се само commit/tree/blob обекти, с изключени replacement refs.
  Работните файлове и index на източника не участват в export-а.
- Manifest записва source commit, allowlist SHA, Git blob ID и SHA/bytes на
  всеки upstream и материализиран файл. Няма абсолютни локални пътища или
  копирана Git история. Еднакъв snapshot дава еднакъв manifest.
- Graph RAW SHA/bytes и independent literal fixture SHA/bytes трябва да
  съвпаднат точно с input lock-а от същия commit. Единственото EOL
  преобразуване е LF→CRLF за графа, ако committed `.gitattributes` го задава
  изрично и резултатът съвпада с заключения RAW hash.
- Преди запис всеки стар managed файл трябва да съвпада с предишния manifest.
  Липсващ или редактиран файл спира цялата операция. Преди запис се проверяват
  и опасни пътища, case collisions, symlinks/junctions/reparse points и
  припокриване между source и target.
- Публичните допълнения са `PREVIEW.md`, `PUBLIC-SYNC.md`,
  `tools/sync_public_snapshot.py`, `packaging/public-readme-prefix.md`,
  `packaging/Get-PublicInputs.ps1` и `packaging/public-delivery.json`.
  Те се запазват и техните текущи hashes се записват в manifest. Друг
  целенасочен файл се допуска с повтаряемо `--public-only <relative-path>`.
  Неизвестни файлове, включително build/cache/input outputs, спират sync-а:
  използвай чист checkout, без да изтриваш непозната работа автоматично.
- Стар managed файл, отпаднал от новия allowlist, се премахва само след
  проверка на старите му bytes. С `--public-only` може изрично да се запази
  като публично допълнение. Не се променят public-only файлове автоматично.

Всички планирани bytes се подготвят и проверяват преди запис. Индивидуалните
файлове се заменят атомарно, manifest се записва последен. При обикновена
грешка при запис инструментът възстановява вече променените файлове.
Това не е filesystem транзакция срещу спиране на процеса, отказ на диска или
едновременен външен writer. При такова прекъсване прегледай checkout-а и
възстанови от публичния commit; не заобикаляй конфликтите с нов manifest.
Самият инструмент не проверява дали избраният source commit е преминал
review — това остава изричен избор на човека/координатора.

Големите source inputs и готовият runtime пакет се доставят отделно чрез
versioned release assets и `packaging/public-delivery.json`. Промяна на
техните bytes изисква нова version/location и точни hashes; sync инструментът
не изтегля, не опакова и не публикува тези файлове.

## Проверка на самия sync инструмент

Публичният `tools/test_sync_public_snapshot.py` се запазва като изрично
public-only допълнение. При първата синхронизация, която го добавя, използвай
`--public-only tools/test_sync_public_snapshot.py`. Изпълнение:

```powershell
python -B tools/test_sync_public_snapshot.py
```

Тестовете използват само Python stdlib и Git. Създават временни synthetic Git
хранилища до test файла и ги премахват след проверката; не изпълняват BgLang,
не използват реалния частен checkout и не осъществяват мрежова комуникация.
На Windows проверката за reparse point използва локален PowerShell junction.
