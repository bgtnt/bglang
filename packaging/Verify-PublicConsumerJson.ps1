[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$InputJsonPath,
    [Parameter(Mandatory=$true)][ValidateSet('known-repeat','empty','context-default')][string]$SampleId,
    [Parameter(Mandatory=$true)][string]$SamplePath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# Parser only: caller owns sample creation, held native invocation and stdout/provider evidence.
# Expectations below are independent literals from PublicPackageTests and ContextCompositionHttpTests.
$script:consumerCheckCount = 0
function Assert-Consumer([bool]$Condition, [string]$Label) {
    if (-not $Condition) { throw ('Public consumer JSON assertion failed: ' + $Label) }
    $script:consumerCheckCount++
}
function Get-Field([System.Text.Json.JsonElement]$Value, [string]$Name) { return $Value.GetProperty($Name) }
function Assert-Kind([System.Text.Json.JsonElement]$Value, [string]$Kind, [string]$Label) {
    Assert-Consumer ($Value.ValueKind.ToString() -ceq $Kind) ($Label + ' kind=' + $Kind)
}
function Assert-String([System.Text.Json.JsonElement]$Value, [string]$Expected, [string]$Label) {
    Assert-Kind $Value 'String' $Label
    Assert-Consumer ($Value.GetString() -ceq $Expected) ($Label + ' literal string')
}
function Assert-Number([System.Text.Json.JsonElement]$Value, [double]$Expected, [string]$Label) {
    Assert-Kind $Value 'Number' $Label
    Assert-Consumer ($Value.GetDouble() -eq $Expected) ($Label + ' literal number')
}
function Assert-Bool([System.Text.Json.JsonElement]$Value, [bool]$Expected, [string]$Label) {
    Assert-Consumer ($Value.ValueKind -in @([System.Text.Json.JsonValueKind]::True,[System.Text.Json.JsonValueKind]::False)) ($Label + ' boolean type')
    Assert-Consumer ($Value.GetBoolean() -eq $Expected) ($Label + ' literal boolean')
}
function Assert-Count([System.Text.Json.JsonElement]$Value, [int]$Expected, [string]$Label) {
    Assert-Kind $Value 'Array' $Label
    Assert-Consumer ($Value.GetArrayLength() -eq $Expected) ($Label + ' array length=' + $Expected)
}
function Assert-Fields([System.Text.Json.JsonElement]$Value, [string[]]$Names, [string]$Label) {
    Assert-Kind $Value 'Object' $Label
    $actualNames = @($Value.EnumerateObject() | ForEach-Object { $_.Name })
    Assert-Consumer ($actualNames.Count -eq $Names.Count) ($Label + ' exact property count')
    foreach ($fieldName in $Names) { Assert-Consumer ($actualNames -ccontains $fieldName) ($Label + '.' + $fieldName) }
}
function Pin-ConsumerFile([string]$Path) {
    $data = [IO.File]::ReadAllBytes($Path)
    return [ordered]@{ path=[IO.Path]::GetFullPath($Path); bytes=$data.LongLength;
        sha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($data)).ToLowerInvariant() }
}
function Select-Observation([System.Text.Json.JsonElement]$Items, [string]$Method, [string]$Field, [string]$InputValue) {
    $selected = @($Items.EnumerateArray() | Where-Object {
        (Get-Field $_ 'method').GetString() -ceq $Method -and
        (Get-Field (Get-Field $_ 'input') $Field).GetString() -ceq $InputValue
    })
    Assert-Consumer ($selected.Count -eq 1) ('exactly one ' + $Method + ' for literal ' + $InputValue)
    return Get-Field $selected[0] 'result'
}
function Assert-Mark([System.Text.Json.JsonElement]$Marks, [int]$Ordinal, [int]$Position, [string]$Kind, [string]$Label) {
    Assert-Count $Marks 1 $Label
    Assert-Fields $Marks[0] @('VowelOrdinal','Utf16Position','Kind') $Label
    Assert-Number (Get-Field $Marks[0] 'VowelOrdinal') $Ordinal ($Label + '.ordinal')
    Assert-Number (Get-Field $Marks[0] 'Utf16Position') $Position ($Label + '.position')
    Assert-String (Get-Field $Marks[0] 'Kind') $Kind ($Label + '.kind')
}
function Select-OwnerRealization([System.Text.Json.JsonElement]$Token, [string]$Form, [string]$Tag, [string]$OwnerId) {
    $rows = @((Get-Field $Token 'Readings').EnumerateArray() | Where-Object {
        $reading = Get-Field $_ 'Reading'
        (Get-Field $reading 'Form').GetString() -ceq $Form -and (Get-Field $reading 'PartOfSpeech').GetString() -ceq $Tag
    })
    Assert-Consumer ($rows.Count -gt 0) 'literal exact form/tag reading present'
    $owners = @($rows | ForEach-Object { (Get-Field $_ 'Owners').EnumerateArray() } | Where-Object { (Get-Field $_ 'EntryId').GetString() -ceq $OwnerId })
    Assert-Consumer ($owners.Count -gt 0) 'literal opaque owner present'
    $realizations = Get-Field $owners[0] 'Realizations'; Assert-Count $realizations 1 'coherent captured realization'
    return $realizations[0]
}
$literalSamples = @{
    'known-repeat' = @{ text='знаме знаме.'; utf16Length=12; bytes=22; sha256='18db9d15a6b5d6914f9806b829928b5d6e7aa278abb47ed18641517c26e50e0a' }
    'empty' = @{ text=''; utf16Length=0; bytes=0; sha256='e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855' }
    'context-default' = @{ text='книга.'; utf16Length=6; bytes=11; sha256='8f023a907584645cf4b56ff760086f9fd1c74bf72bfa4b5c5b5a1c676f071d58' }
}
$literal = $literalSamples[$SampleId]
$strictUtf8 = [Text.UTF8Encoding]::new($false,$true)
$absoluteSample = [IO.Path]::GetFullPath($SamplePath)
$samplePin = Pin-ConsumerFile $absoluteSample
Assert-Consumer ($samplePin.bytes -eq $literal.bytes -and $samplePin.sha256 -ceq $literal.sha256) 'independent sample bytes/SHA256'
Assert-Consumer ($strictUtf8.GetString([IO.File]::ReadAllBytes($absoluteSample)) -ceq $literal.text) 'strict UTF8 literal, no BOM/newline'
Assert-Consumer ($literal.text.Length -eq $literal.utf16Length) 'literal UTF16 length'
$jsonPin = Pin-ConsumerFile $InputJsonPath
$document = [System.Text.Json.JsonDocument]::Parse($strictUtf8.GetString([IO.File]::ReadAllBytes($InputJsonPath)))
try {
    $root = $document.RootElement
    Assert-Fields $root @('schema','Provenance','environment','startup','measurementNote','observations') 'outer envelope'
    Assert-String (Get-Field $root 'schema') 'bglang-public-example.v1' 'schema'
    $provenance = Get-Field $root 'Provenance'
    Assert-String (Get-Field $provenance 'PackageId') 'bglang-neutral:e0de331f066ab7cf' 'package identity'
    Assert-String (Get-Field $provenance 'ManifestSha256') 'c137643e00ca568b2fcf249627c5c1396ab8a004dd63fed213f575a2faeedb12' 'matching accepted manifest'
    $observations = Get-Field $root 'observations'
    Assert-Kind $observations 'Array' 'observations'
    # Prospective literals from the accepted Public/Full/HTTP stress controls, never derived from this output.
    $stored = Select-Observation $observations 'GetStressEvidence' 'text' '😀 аванпост.'
    Assert-Fields $stored @('Text','RenderedText','OwnersAvailable','SelectionStatus','Tokens') 'stress projection'
    Assert-String (Get-Field $stored 'Text') '😀 аванпост.' 'stored original text'
    Assert-String (Get-Field $stored 'RenderedText') "😀 ава$([char]0x0300)нпост." 'stored conservative rendering'
    Assert-Bool (Get-Field $stored 'OwnersAvailable') $true 'stored owners available'
    Assert-String (Get-Field $stored 'SelectionStatus') 'Matched' 'stored exact owner selection'
    $storedTokens = Get-Field $stored 'Tokens'; Assert-Count $storedTokens 1 'stored word token'
    $storedToken = $storedTokens[0]
    Assert-Number (Get-Field $storedToken 'Start') 3 'stored absolute UTF16 start'
    Assert-Number (Get-Field $storedToken 'Length') 8 'stored raw length'
    Assert-String (Get-Field $storedToken 'Status') 'Supported' 'stored token status'
    $captureSource = 'legacy-stress-v1:ae70252c3e252d8e568cfa16cfde94a47c95f10553cfe72c567413bc4c9286ec'
    $capture = Select-OwnerRealization $storedToken 'аванпост' 'Съществително:1,Мъжки' 'legacy-base:64'
    Assert-String (Get-Field $capture 'Origin') 'LegacyStored' 'stored origin'
    Assert-String (Get-Field $capture 'SourceId') $captureSource 'stored capture provenance'
    Assert-Mark (Get-Field $capture 'Marks') 1 5 'Unspecified' 'stored absolute mark'
    $rawOffsets = Get-Field $capture 'LegacyUtf16Positions'; Assert-Count $rawOffsets 1 'raw Legacy offsets'
    Assert-Number $rawOffsets[0] 2 'raw Legacy local offset'
    Assert-Kind (Get-Field $capture 'LegacyDisplayUtf16Positions') 'Null' 'no fabricated display offsets'

    $mixedText = "абата кни$([char]0x0300)ги жжжабракадабрю."
    $mixed = Select-Observation $observations 'GetStressEvidence' 'text' $mixedText
    Assert-String (Get-Field $mixed 'SelectionStatus') 'Matched' 'display-inferred owner selection'
    $mixedTokens = Get-Field $mixed 'Tokens'; Assert-Count $mixedTokens 3 'mixed stress tokens'
    $displayToken = $mixedTokens[0]
    Assert-String (Get-Field $displayToken 'RenderedText') "аба$([char]0x0300)та" 'display-inferred rendering'
    Assert-Number (Get-Field $displayToken 'Start') 0 'display raw start'; Assert-Number (Get-Field $displayToken 'Length') 5 'display raw length'
    Assert-String (Get-Field $displayToken 'Status') 'Inferred' 'display remains inferred'
    $display = Select-OwnerRealization $displayToken 'абата' 'Съществително:41,Женски,Членуван' 'legacy-base:824640'
    Assert-String (Get-Field $display 'Origin') 'LegacyDisplayInferred' 'display provenance not promoted'
    Assert-String (Get-Field $display 'SourceId') $captureSource 'display capture provenance'
    Assert-Mark (Get-Field $display 'Marks') 1 2 'Unspecified' 'display mark'
    Assert-Kind (Get-Field $display 'LegacyUtf16Positions') 'Null' 'display raw getter stays null'
    $displayOffsets = Get-Field $display 'LegacyDisplayUtf16Positions'; Assert-Count $displayOffsets 1 'display offsets'
    Assert-Number $displayOffsets[0] 2 'display local offset'
    $writtenToken = $mixedTokens[1]
    Assert-Number (Get-Field $writtenToken 'Start') 6 'written raw start'; Assert-Number (Get-Field $writtenToken 'Length') 6 'written raw length'
    $observed = Get-Field $writtenToken 'Observations'; Assert-Count $observed 1 'written observation'
    Assert-String (Get-Field $observed[0] 'Origin') 'InputObservation' 'written origin'
    Assert-String (Get-Field $observed[0] 'Status') 'Unclassified' 'written mark is not normative authority'
    Assert-Kind (Get-Field $observed[0] 'SourceId') 'Null' 'written source absent'
    Assert-Mark (Get-Field $observed[0] 'Marks') 0 8 'Grave' 'written absolute mark'
    Assert-String (Get-Field $mixedTokens[2] 'Text') 'жжжабракадабрю' 'unknown literal'
    Assert-Number (Get-Field $mixedTokens[2] 'Start') 13 'unknown raw start'; Assert-Number (Get-Field $mixedTokens[2] 'Length') 14 'unknown raw length'
    Assert-String (Get-Field $mixedTokens[2] 'Status') 'Unknown' 'unknown stays unknown'
    Assert-String (Get-Field $mixedTokens[2] 'RenderedText') 'жжжабракадабрю' 'unknown unchanged'

    $generated = Select-Observation $observations 'GenerateStressForms' 'prototypeEntryId' 'legacy-base:64'
    Assert-Fields $generated @('Available','PrototypeEntryId','PrototypeLemma','Lemma','RuleId','EvidenceSourceId','GraphSourceId','Forms') 'prototype projection'
    Assert-Bool (Get-Field $generated 'Available') $true 'prototype available'
    Assert-String (Get-Field $generated 'PrototypeEntryId') 'legacy-base:64' 'explicit prototype identity'
    Assert-String (Get-Field $generated 'PrototypeLemma') 'аванпост' 'prototype lemma'
    Assert-String (Get-Field $generated 'Lemma') 'космоаванпост' 'novel lemma'
    Assert-String (Get-Field $generated 'GraphSourceId') 'word-formation-source-v0' 'prototype graph provenance'
    $fitSource = Get-Field $generated 'EvidenceSourceId'; Assert-Kind $fitSource 'String' 'qualified model provenance'
    Assert-Consumer ($fitSource.GetString().StartsWith('legacy-pattern-fit-v1:', [StringComparison]::Ordinal)) 'qualified fit retains provenance'
    $baseForms = @((Get-Field $generated 'Forms').EnumerateArray() | Where-Object { (Get-Field $_ 'Form').GetString() -ceq 'космоаванпост' -and (Get-Field $_ 'FullTag').GetString() -ceq 'Съществително:1,Мъжки' })
    Assert-Consumer ($baseForms.Count -eq 1) 'literal generated base row'
    Assert-Fields $baseForms[0] @('Form','FullTag','Status','Marks') 'generated row projection'
    Assert-String (Get-Field $baseForms[0] 'Status') 'Inferred' 'prototype is a prediction'
    Assert-Mark (Get-Field $baseForms[0] 'Marks') 3 7 'Unspecified' 'predicted local mark'
    $missing = Select-Observation $observations 'GenerateStressForms' 'prototypeEntryId' ' legacy-base:64 '
    Assert-Bool (Get-Field $missing 'Available') $false 'unconnected prototype unavailable'
    Assert-String (Get-Field $missing 'PrototypeEntryId') ' legacy-base:64 ' 'opaque prototype is not trimmed'
    Assert-Count (Get-Field $missing 'Forms') 0 'unavailable prototype forms empty'
    Assert-Count (Select-Observation $observations 'Lookup' 'word' 'космоаванпост') 0 'prediction is not exact storage'
    Assert-Count (Select-Observation $observations 'GenerateForms' 'lemma' 'космоаванпост') 0 'prediction does not add an ordinary paradigm'

    $derived = Select-Observation $observations 'GetDerivationalStressEvidence' 'word' 'играч'
    Assert-Fields $derived @('Decomposition','Status','Realization') 'derivational projection'
    Assert-String (Get-Field $derived 'Status') 'Inferred' 'derivational status'
    $decomposition = Get-Field $derived 'Decomposition'
    foreach ($pair in @(@('Word','играч'),@('NormalizedWord','играч'),@('Base','играя'),@('ApplicationId','a:играч'),@('RuleId','derivational_suffix.stem_family.ach.v0'),@('Transparency','transparent'))) {
        Assert-String (Get-Field $decomposition $pair[0]) $pair[1] ('derivational proof.' + $pair[0])
    }
    Assert-Bool (Get-Field $decomposition 'ProductiveGeneration') $false 'analysis does not grant forward generation'
    $prediction = Get-Field $derived 'Realization'
    Assert-String (Get-Field $prediction 'Origin') 'DerivationalRulePrediction' 'derivational origin'
    Assert-String (Get-Field $prediction 'Status') 'Inferred' 'derivational realization status'
    Assert-String (Get-Field $prediction 'SourceId') 'patseva-bulgarian-lexical-accent:section-5.3.1.1:agentive-ach' 'named accent source'
    Assert-Mark (Get-Field $prediction 'Marks') 1 3 'Unspecified' 'derivational local mark'
    Assert-Kind (Get-Field $prediction 'LegacyUtf16Positions') 'Null' 'no fabricated Legacy raw capture'
    Assert-Kind (Get-Field $prediction 'LegacyDisplayUtf16Positions') 'Null' 'no fabricated Legacy display capture'

    $selectedObservations = @($observations.EnumerateArray() | Where-Object {
        $item = $_
        if ((Get-Field $item 'method').GetString() -cne 'AnalyzeText') { return $false }
        $reportedFile = Get-Field (Get-Field $item 'input') 'file'
        return $reportedFile.ValueKind -eq [System.Text.Json.JsonValueKind]::Object -and
            (Get-Field $reportedFile 'path').GetString() -ceq $absoluteSample
    })
    Assert-Consumer ($selectedObservations.Count -eq 1) 'exactly one AnalyzeText observation for absolute sample path'
    $observation = $selectedObservations[0]
    Assert-Fields $observation @('method','input','result','measurement') 'selected observation'
    $reportedInput = Get-Field $observation 'input'
    Assert-String (Get-Field $reportedInput 'text') $literal.text 'input.text'
    Assert-Number (Get-Field $reportedInput 'utf16Length') $literal.utf16Length 'input.utf16Length'
    $reportedFile = Get-Field $reportedInput 'file'
    Assert-Number (Get-Field $reportedFile 'bytes') $literal.bytes 'input.file.bytes'
    Assert-String (Get-Field $reportedFile 'sha256') $literal.sha256 'input.file.sha256'
    $result = Get-Field $observation 'result'
    $textFields = @('Text','Sentences','Phonetics','Diagnostics','Tokens','Lexical','ReadingInventory','Metrics',
        'Spelling','Profile','ProperNames','Context','Syntax','Validation','WordConstructions')
    # Membership, not declaration order: the original console wrapper intentionally orders its five fields differently.
    Assert-Fields $result $textFields 'TextAnalysis projection'
    Assert-String (Get-Field $result 'Text') $literal.text 'result.Text'
    Assert-Kind (Get-Field $result 'WordConstructions') 'Null' 'default construction opt-in not run'
    foreach ($name in @('Lexical','ReadingInventory','Metrics','Spelling','Profile','ProperNames','Context','Syntax','Validation')) {
        Assert-Kind (Get-Field $result $name) 'Object' $name
    }
    $lexical = Get-Field $result 'Lexical'
    Assert-Fields $lexical @('MetadataAvailable','Tokens','Phrases','PackageHasUnresolvedRelations') 'Lexical'
    Assert-Kind (Get-Field $lexical 'Tokens') 'Array' 'Lexical.Tokens'
    Assert-Kind (Get-Field $lexical 'Phrases') 'Array' 'Lexical.Phrases'
    $tokens = Get-Field $result 'Tokens'
    Assert-Kind $tokens 'Array' 'Tokens'
    $diagnostics = Get-Field $result 'Diagnostics'
    Assert-Kind $diagnostics 'Array' 'Diagnostics'
    foreach ($diagnostic in $diagnostics.EnumerateArray()) {
        Assert-Fields $diagnostic @('Code','Start','Length','RuleId','ProjectedText','Classification') 'preserved DiagnosticValue enrichment'
    }
    $featureFields = @('RawTag','PartOfSpeech','ParadigmId','Person','Number','Gender','Tense','VerbForm','Article',
        'HasUnrecognizedFeatures','HasConflictingFeatures','NameKind','Case','IsShortForm','ParticipleKind')
    foreach ($token in $tokens.EnumerateArray()) {
        Assert-Fields $token @('Text','Kind','Start','Length','Analysis') 'token'
        $start = (Get-Field $token 'Start').GetInt32(); $length = (Get-Field $token 'Length').GetInt32()
        Assert-Consumer ($start -ge 0 -and $length -ge 0 -and $start + $length -le $literal.text.Length) 'raw token UTF16 bounds'
        Assert-String (Get-Field $token 'Text') $literal.text.Substring($start,$length) 'raw token UTF16 slice'
        $analysis = Get-Field $token 'Analysis'
        if ($analysis.ValueKind -ne [System.Text.Json.JsonValueKind]::Null) {
            Assert-Fields $analysis @('Word','NormalizedWord','IsKnown','GraphSearchIncomplete','GraphCandidatesTruncated',
                'CandidateSearchLimited','CandidateEvidenceLimited','KnownBaseSearchLimited','BaseTypeSearchLimited',
                'BaseTypeEvidenceLimited','Readings','BaseEvidence','GraphCandidates','Candidates','KnownBaseCandidates','BaseTypeCandidates') 'preserved WordValue enrichment'
            $readings = Get-Field $analysis 'Readings'; Assert-Kind $readings 'Array' 'WordValue.Readings'
            foreach ($reading in $readings.EnumerateArray()) {
                Assert-Fields $reading @('reading','Features') 'preserved ReadingValue enrichment'
                Assert-Kind (Get-Field $reading 'reading') 'Object' 'ReadingValue.reading'
                Assert-Fields (Get-Field $reading 'Features') $featureFields 'ReadingValue.Features'
            }
        }
    }
    $metrics = Get-Field $result 'Metrics'
    $metricFields = @('TokenCount','WordCount','SentenceCount','AverageWordsPerSentence','AverageWordLength',
        'TypeTokenRatio','PunctuationCount','NormalizedRepetitionExtraCount','UnknownWordRatio')
    Assert-Fields $metrics $metricFields 'Metrics'
    $inventory = Get-Field $result 'ReadingInventory'
    Assert-Fields $inventory @('Sets','Occurrences') 'ReadingInventory'
    $sets = Get-Field $inventory 'Sets'; $occurrences = Get-Field $inventory 'Occurrences'
    Assert-Kind $sets 'Array' 'inventory.Sets'; Assert-Kind $occurrences 'Array' 'inventory.Occurrences'
    $readingProfile = Get-Field $result 'Profile'; $names = Get-Field $result 'ProperNames'
    Assert-Bool (Get-Field $names 'UnknownContextEvaluated') $false 'default unknown-name opt-in not run'
    Assert-Kind (Get-Field $names 'UnresolvedContextTokens') 'Null' 'default unresolved names'
    Assert-Kind (Get-Field $names 'Candidates') 'Array' 'name candidates'; Assert-Kind (Get-Field $names 'Groups') 'Array' 'name groups'
    Assert-Kind (Get-Field (Get-Field $result 'Syntax') 'AdditionalGroups') 'Null' 'default additional roles'
    $validation = Get-Field $result 'Validation'
    foreach ($field in @('SpacingDiagnostics','SubordinateCommaDiagnostics','FragmentDiagnostics')) {
        Assert-Kind (Get-Field $validation $field) 'Null' ('default Validation.' + $field)
    }
    # Actual unavailable-check values remain exposed. Do not rewrite them into an invented empty/success set.
    Assert-Kind (Get-Field $validation 'UnavailableChecks') 'Array' 'Validation.UnavailableChecks'
    if ($SampleId -ceq 'known-repeat') {
        $metricValues = @(3,2,1,2,5,0.5,1,1,0)
        for ($index=0; $index -lt $metricFields.Count; $index++) { Assert-Number (Get-Field $metrics $metricFields[$index]) $metricValues[$index] ('literal Metrics.' + $metricFields[$index]) }
        Assert-Count $sets 1 'literal inventory sets'; Assert-Count $occurrences 2 'literal occurrences'
        $occurrenceLiterals = @(@(0,'знаме',0,5,0),@(1,'знаме',6,5,0))
        for ($index=0; $index -lt 2; $index++) {
            $occurrence = $occurrences[$index]; $expected = $occurrenceLiterals[$index]
            Assert-Number (Get-Field $occurrence 'TokenIndex') $expected[0] 'occurrence.TokenIndex'
            Assert-String (Get-Field $occurrence 'Text') $expected[1] 'occurrence.Text'
            Assert-Number (Get-Field $occurrence 'Start') $expected[2] 'occurrence.Start'
            Assert-Number (Get-Field $occurrence 'Length') $expected[3] 'occurrence.Length'
            Assert-Number (Get-Field $occurrence 'ReadingSetIndex') $expected[4] 'occurrence.ReadingSetIndex'
        }
        $set = $sets[0]
        Assert-String (Get-Field $set 'LookupText') 'знаме' 'LookupText'; Assert-String (Get-Field $set 'CanonicalKey') 'знаме' 'CanonicalKey'
        Assert-Kind (Get-Field $set 'SupportKey') 'Null' 'unused SupportKey'; Assert-Kind (Get-Field $set 'SupportReadings') 'Null' 'unused SupportReadings'
        Assert-Number (Get-Field $set 'DisplayedReadingCount') 1 'DisplayedReadingCount'; Assert-Bool (Get-Field $set 'DisplayTruncated') $false 'DisplayTruncated'
        $canonical = Get-Field $set 'CanonicalReadings'; Assert-Count $canonical 1 'canonical readings'
        $rawReading = Get-Field $canonical[0] 'Raw'; $features = Get-Field $canonical[0] 'Features'
        Assert-String (Get-Field $rawReading 'Form') 'знаме' 'Raw.Form'; Assert-String (Get-Field $rawReading 'Lemma') 'знаме' 'Raw.Lemma'
        Assert-String (Get-Field $rawReading 'PartOfSpeech') 'Съществително:69,Среден' 'literal full tag'; Assert-Number (Get-Field $rawReading 'Score') 1 'literal score'
        Assert-Fields $features $featureFields 'complete snapshot Features'
        foreach ($pair in @(@('PartOfSpeech','Noun'),@('Gender','Neuter'),@('Number','Singular'),@('Article','Indefinite'))) { Assert-String (Get-Field $features $pair[0]) $pair[1] ('literal Features.' + $pair[0]) }
        Assert-Kind (Get-Field $features 'NameKind') 'Null' 'literal Features.NameKind'
        Assert-String (Get-Field $readingProfile 'ProjectionVersion') 'typed-reading-profile.v1' 'projection version'
        foreach ($field in @('WordOccurrences','KnownWordOccurrences','DisplayedReadingOccurrences','FullReadingOccurrences','CompleteExactInventoryWordOccurrences','ProjectionAssessmentCompleteWordOccurrences')) { Assert-Number (Get-Field $readingProfile $field) 2 ('literal Profile.' + $field) }
        foreach ($field in @('UnknownWordOccurrences','UnmeasuredKnownnessWordOccurrences','ObservedProjectedAmbiguityWordOccurrences','ProjectionIssueWordOccurrences','DisplayTruncatedWordOccurrences')) { Assert-Number (Get-Field $readingProfile $field) 0 ('literal Profile.' + $field) }
        Assert-Number (Get-Field $readingProfile 'KnownWordRatio') 1 'known ratio'
        Assert-Bool (Get-Field $readingProfile 'AllWordAssessmentsCompleteForProjection') $true 'complete projection'
        Assert-Bool (Get-Field $readingProfile 'ExactReadingEvidencePresent') $true 'exact reading evidence'
        $profileWords = Get-Field $readingProfile 'Words'; Assert-Count $profileWords 2 'profile word occurrences'
        foreach ($word in $profileWords.EnumerateArray()) {
            Assert-Number (Get-Field $word 'FullReadingCount') 1 'full reading count'; Assert-Number (Get-Field $word 'ProjectedGroupCount') 1 'projected group count'
            Assert-Bool (Get-Field $word 'ObservedProjectedAmbiguity') $false 'projected ambiguity'; Assert-String (Get-Field $word 'Issues') 'None' 'projection issues'
            Assert-Bool (Get-Field $word 'ActualDisplayTruncated') $false 'display truncated'
        }
        foreach ($group in @(@('DisplayedPosCounts','Noun'),@('DisplayedLemmaCounts','знаме'))) {
            $values = Get-Field $readingProfile $group[0]; Assert-Count $values 1 $group[0]
            Assert-String (Get-Field $values[0] 'Key') $group[1] ($group[0] + '.Key'); Assert-Number (Get-Field $values[0] 'Value') 2 ($group[0] + '.Value')
        }
        $spelling = Get-Field $result 'Spelling'; Assert-Number (Get-Field $spelling 'ConfiguredMaxDistance') 2 'spelling configured distance'; Assert-Number (Get-Field $spelling 'ConfiguredLimit') 10 'spelling configured limit'
        $spellingWords = Get-Field $spelling 'Words'; Assert-Count $spellingWords 2 'spelling word occurrences'
        foreach ($word in $spellingWords.EnumerateArray()) { Assert-String (Get-Field $word 'Search') 'NotRequestedKnown' 'spelling search'; Assert-String (Get-Field $word 'Reason') 'exact-known' 'spelling reason'; Assert-Count (Get-Field $word 'Alternatives') 0 'spelling alternatives' }
        Assert-Count (Get-Field $names 'Candidates') 0 'known name candidates'; Assert-Count (Get-Field $names 'Groups') 0 'known name groups'
        Assert-Bool (Get-Field $names 'CompleteForEnabledScope') $true 'name scope completeness'
    } elseif ($SampleId -ceq 'empty') {
        foreach ($field in $metricFields) { Assert-Number (Get-Field $metrics $field) 0 ('empty Metrics.' + $field) }
        Assert-Count $sets 0 'empty inventory sets'; Assert-Count $occurrences 0 'empty inventory occurrences'; Assert-Count $tokens 0 'empty tokens'
        Assert-Kind (Get-Field $readingProfile 'KnownWordRatio') 'Null' 'empty known ratio'; Assert-Number (Get-Field $readingProfile 'FullReadingOccurrences') 0 'empty full readings'
        Assert-Bool (Get-Field $readingProfile 'AllWordAssessmentsCompleteForProjection') $true 'empty complete projection'; Assert-Bool (Get-Field $readingProfile 'ExactReadingEvidencePresent') $false 'empty exact evidence'
        Assert-Count (Get-Field $names 'Candidates') 0 'empty name candidates'
    } else {
        foreach ($pair in @(@('Context','bounded-context.v1'),@('Syntax','bounded-syntax-roles.v1'),@('Validation','bounded-validation.v1'))) {
            $evidence = Get-Field $result $pair[0]
            Assert-String (Get-Field $evidence 'Version') $pair[1] ($pair[0] + '.Version')
            Assert-Bool (Get-Field $evidence 'Available') $true ($pair[0] + '.Available')
            Assert-Kind (Get-Field $evidence 'UnavailableReason') 'Null' ($pair[0] + '.UnavailableReason')
        }
    }
    [pscustomobject]@{ schema='bglang.publication-console-literal-check.v1'; status='PASS_CONSOLE_DEFAULT_AND_STRESS_PROJECTION';
        sampleId=$SampleId; sample=$samplePin; inputJson=$jsonPin; textFieldCount=15; stressQueries=5; predictionStorageControls=2; passedAssertions=$script:consumerCheckCount;
        qualification='Default console projection and independent literals only. No CLI opt-in, corpus/grammar/resource/publication claim. Native/provider terminal belongs to caller.' }
} finally { $document.Dispose() }
