# Voice edition, for Windows: installs the speech models the ship's voice and spoken commands use.
# The twin of setup_voice.sh (macOS and Linux): the same packages and models from the same places, into the
# same folder layout. Run on Windows 11 on 2026-10-10 (with -Yes, the Microsoft Store's Python 3.13): everything
# installed, and both models worked (see wiki/ksp-ai-bridge/Voice-edition.md).
#
#   powershell -ExecutionPolicy Bypass -File .\setup_voice.ps1         asks before downloading
#   powershell -ExecutionPolicy Bypass -File .\setup_voice.ps1 -Yes    no questions
#
# (Windows will not run a script that came in a download unless told to; -ExecutionPolicy Bypass tells it so
# for this one run and changes no setting.)
#
# Everything goes into one folder (default %USERPROFILE%\.ksp-ai-bridge\voice, or $env:KSP_VOICE_DIR) that can
# simply be deleted to uninstall. Nothing is sent anywhere: both models run on this computer.
#   * Kokoro text-to-speech: kokoro-v1.0.int8.onnx (114 MB) and voices-v1.0.bin (28 MB)
#     from https://github.com/thewh1teagle/kokoro-onnx/releases (model-files-v1.1)
#   * Whisper speech recognition, base.en (about 145 MB) from https://huggingface.co/Systran/faster-whisper-base.en
#   * Python packages from PyPI in their own environment: kokoro-onnx, faster-whisper, sounddevice
#     and what they depend on (onnxruntime, numpy, ctranslate2, phonemizer ...), about 300 MB.
# Without it the voice edition still works with Windows' own voice and typed commands.
#
# Written for Windows PowerShell 5.1 (part of Windows 10 and 11); PowerShell 7 runs it too. Keep this file
# plain ASCII: Windows PowerShell reads a script without a byte-order mark in the system's own code page.

param(
    [switch]$Yes,
    [Parameter(ValueFromRemainingArguments = $true)] [string[]]$Rest
)
# (Errors are checked one by one below: with "Stop", Windows PowerShell would also stop at the first line a
# program writes to its error output, and curl and pip write their progress and warnings there.)
$ErrorActionPreference = "Continue"
$ProgressPreference = "SilentlyContinue"    # (Invoke-WebRequest is many times slower while it draws a progress bar)
if ($Rest -contains "--yes") { $Yes = $true }

$HomeDir = $env:USERPROFILE                 # (what Python takes "~" to mean on Windows)
if (-not $HomeDir) { $HomeDir = $HOME }
if ($env:KSP_VOICE_DIR) { $Dir = $env:KSP_VOICE_DIR } else { $Dir = Join-Path $HomeDir ".ksp-ai-bridge\voice" }
$Dir = $Dir.TrimEnd("\")                    # (a path ending in \ would swallow its closing quote on a program's command line)
$Models = "https://github.com/thewh1teagle/kokoro-onnx/releases/download/model-files-v1.1"

function Fail([string]$Message) {
    Write-Host "error: $Message" -ForegroundColor Red
    exit 1
}

if (-not $Yes) {
    Write-Host "This downloads about 0.6 GB of speech models and Python packages into:"
    Write-Host "  $Dir"
    $answer = Read-Host "Go ahead? [y/N]"
    if ($answer -notmatch '^(y|yes)$') { Write-Host "Nothing was downloaded."; exit 0 }
}

# Kokoro's package needs Python 3.10 to 3.13, and its libraries come for 64-bit Python only. The Python
# launcher (py) knows every Python installed from python.org; "python" on the PATH may be one too, or only
# the Microsoft Store's stand-in, which fails the test below like any other unsuitable one.
$Check = "import sys; sys.exit(0 if (3, 10) <= sys.version_info[:2] <= (3, 13) and sys.maxsize > 2**32 else 1)"
$Python = $null
foreach ($candidate in @("py -3.13", "py -3.12", "py -3.11", "py -3.10", "python3.13", "python3.12", "python3.11", "python3.10", "python", "python3")) {
    $words = $candidate -split " "
    $program = $words[0]
    $options = @($words | Select-Object -Skip 1)
    if (-not (Get-Command $program -ErrorAction SilentlyContinue)) { continue }
    & $program @options -c $Check 2>$null
    if ($LASTEXITCODE -eq 0) {
        $Python = (& $program @options -c "import sys; print(sys.executable)" | Select-Object -First 1)
        if ($Python) { $Python = $Python.Trim() }
        if ($Python) { break }
    }
}

New-Item -ItemType Directory -Force -Path $Dir -ErrorAction Stop | Out-Null
$Venv = Join-Path $Dir "venv"
$Py = Join-Path $Venv "Scripts\python.exe"
if ($Python) {
    if (-not (Test-Path $Py)) {
        & $Python -m venv $Venv
        if ($LASTEXITCODE -ne 0) { Fail "could not make a Python environment in $Venv." }
    }
} elseif (Get-Command uv -ErrorAction SilentlyContinue) {
    Write-Host "No Python 3.10-3.13 found; asking uv for Python 3.12."
    if (-not (Test-Path $Py)) {
        & uv venv --python 3.12 --seed $Venv
        if ($LASTEXITCODE -ne 0) { Fail "uv could not make a Python environment in $Venv." }
    }
} else {
    Write-Host "error: need a 64-bit Python 3.10 to 3.13 (the Kokoro package does not support older or newer ones yet)." -ForegroundColor Red
    Write-Host "       Install one (python.org, 'winget install Python.Python.3.12', or 'uv') and run this again." -ForegroundColor Red
    exit 1
}

Write-Host "== Python packages"
& $Py -m pip install --quiet --upgrade pip
if ($LASTEXITCODE -ne 0) { Fail "pip could not update itself." }
& $Py -m pip install --quiet "kokoro-onnx>=0.6" "faster-whisper>=1.1" sounddevice
if ($LASTEXITCODE -ne 0) { Fail "the Python packages did not install (see pip's messages above)." }

Write-Host "== Kokoro voice model"
foreach ($file in @("kokoro-v1.0.int8.onnx", "voices-v1.0.bin")) {
    $target = Join-Path $Dir $file
    if ((Test-Path $target) -and ((Get-Item $target).Length -gt 0)) { continue }
    $part = "$target.part"
    if (Get-Command curl.exe -ErrorAction SilentlyContinue) {
        # (curl.exe comes with Windows 10 from 1803 on; plain "curl" in Windows PowerShell is another command)
        & curl.exe -L --fail --progress-bar -o $part "$Models/$file"
        if ($LASTEXITCODE -ne 0) { Fail "could not download $Models/$file" }
    } else {
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        try { Invoke-WebRequest -Uri "$Models/$file" -OutFile $part -UseBasicParsing -ErrorAction Stop }
        catch { Fail "could not download $Models/$file ($($_.Exception.Message))" }
    }
    Move-Item -Force -Path $part -Destination $target -ErrorAction Stop
}

Write-Host "== Whisper speech recognition model"
& $Py -c "import os, sys; from faster_whisper import WhisperModel; WhisperModel('base.en', device='cpu', compute_type='int8', download_root=os.path.join(sys.argv[1], 'whisper')); print('ok')" $Dir
if ($LASTEXITCODE -ne 0) { Fail "the Whisper model did not download." }

& $Py -c "import sounddevice" 2>$null
if ($LASTEXITCODE -ne 0) { Write-Host "note: the microphone library (sounddevice) would not load, so spoken commands will not work yet." }

Write-Host "== Standard call-outs"
$Base = $Python
if (-not $Base) { $Base = $Py }
# (as in setup_voice.sh, a failure here stops nothing: a call-out not made now is made the first time it is needed)
& $Base (Join-Path $PSScriptRoot "mcp\ksp_voice.py") prepare

Write-Host 'Done. Try:  py mcp\ksp_voice.py say "All systems nominal."'
