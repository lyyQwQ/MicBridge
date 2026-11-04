param(
    [string]$JavaExecutable = 'java',
    [string]$GradleCache = (Join-Path $env:USERPROFILE '.gradle/caches/modules-2/files-2.1')
)
$ErrorActionPreference = 'Stop'

function Get-CachedJar([string]$Group, [string]$Artifact, [string]$Version) {
    $directory = Join-Path $GradleCache "$Group/$Artifact/$Version"
    $jars = @(Get-ChildItem -LiteralPath $directory -Filter "$Artifact-$Version.jar" -Recurse -File)
    if ($jars.Count -ne 1) { throw "Expected one cached jar for ${Group}:${Artifact}:${Version}" }
    return $jars[0].FullName
}

$stdlib = Get-CachedJar 'org.jetbrains.kotlin' 'kotlin-stdlib' '2.2.21'
$annotations = Get-CachedJar 'org.jetbrains' 'annotations' '13.0'
$compilerClasspath = @(
    (Get-CachedJar 'org.jetbrains.kotlin' 'kotlin-compiler-embeddable' '2.2.21')
    $stdlib
    (Get-CachedJar 'org.jetbrains.kotlin' 'kotlin-script-runtime' '2.2.21')
    (Get-CachedJar 'org.jetbrains.kotlin' 'kotlin-reflect' '1.6.10')
    (Get-CachedJar 'org.jetbrains.kotlin' 'kotlin-daemon-embeddable' '2.2.21')
    (Get-CachedJar 'org.jetbrains.kotlinx' 'kotlinx-coroutines-core-jvm' '1.8.0')
    $annotations
) -join [IO.Path]::PathSeparator
$source = Join-Path $PSScriptRoot '../app/src/main/kotlin/io/github/lyyqwq/micbridge/protocol/Protocol.kt'
$checks = Join-Path $PSScriptRoot 'VerifyServerProof.kt'
$discovery = Join-Path $PSScriptRoot '../app/src/main/kotlin/io/github/lyyqwq/micbridge/discovery/DiscoveryCache.kt'
$discoveryChecks = Join-Path $PSScriptRoot 'VerifyDiscovery.kt'
$discoveryLimits = Join-Path $PSScriptRoot '../app/src/main/kotlin/io/github/lyyqwq/micbridge/discovery/DiscoveryLimits.kt'
$candidatePolicy = Join-Path $PSScriptRoot '../app/src/main/kotlin/io/github/lyyqwq/micbridge/trust/CandidateFailurePolicy.kt'
$candidateChecks = Join-Path $PSScriptRoot 'VerifyCandidateFailure.kt'
$discoveryWorker = Join-Path $PSScriptRoot '../app/src/main/kotlin/io/github/lyyqwq/micbridge/discovery/DiscoveryWorker.kt'
$discoveryWorkerChecks = Join-Path $PSScriptRoot 'VerifyDiscoveryWorker.kt'
$audioRoot = Join-Path $PSScriptRoot '../app/src/main/kotlin/io/github/lyyqwq/micbridge/audio'
$audioSources = @('MicAudioSettings.kt', 'PcmGain.kt', 'PreprocessEffects.kt') | ForEach-Object { Join-Path $audioRoot $_ }
$audioChecks = Join-Path $PSScriptRoot 'VerifyAudioSettings.kt'
$output = Join-Path $PSScriptRoot '../build/protocol-check'
New-Item -ItemType Directory -Path $output -Force | Out-Null
$compileClasspath = @($stdlib, $annotations) -join [IO.Path]::PathSeparator
& $JavaExecutable '-cp' $compilerClasspath 'org.jetbrains.kotlin.cli.jvm.K2JVMCompiler' `
    '-no-stdlib' '-no-reflect' '-jvm-target' '17' '-classpath' $compileClasspath '-d' $output $source $checks $discovery $discoveryLimits $discoveryChecks $candidatePolicy $candidateChecks $discoveryWorker $discoveryWorkerChecks @audioSources $audioChecks
if ($LASTEXITCODE -ne 0) { throw "Protocol check compilation failed: $LASTEXITCODE" }
$runtimeClasspath = @($output, $stdlib) -join [IO.Path]::PathSeparator
& $JavaExecutable '-cp' $runtimeClasspath 'VerifyServerProofKt'
if ($LASTEXITCODE -ne 0) { throw "Protocol verification failed: $LASTEXITCODE" }
& $JavaExecutable '-cp' $runtimeClasspath 'VerifyDiscoveryKt'
if ($LASTEXITCODE -ne 0) { throw "Discovery verification failed: $LASTEXITCODE" }
& $JavaExecutable '-cp' $runtimeClasspath 'VerifyCandidateFailureKt'
if ($LASTEXITCODE -ne 0) { throw "Candidate failure verification failed: $LASTEXITCODE" }
& $JavaExecutable '-cp' $runtimeClasspath 'VerifyDiscoveryWorkerKt'
if ($LASTEXITCODE -ne 0) { throw "Discovery worker verification failed: $LASTEXITCODE" }
& $JavaExecutable '-cp' $runtimeClasspath 'VerifyAudioSettingsKt'
if ($LASTEXITCODE -ne 0) { throw "Audio settings verification failed: $LASTEXITCODE" }
