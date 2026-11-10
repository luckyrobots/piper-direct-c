param([string]$Compiler='gcc', [string]$Make='make')
$ErrorActionPreference='Stop'
$taskRepo=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
Push-Location $taskRepo
try {
    # Every fixture replaces USB/process boundaries with callbacks. None
    # enumerates or opens an adapter, starts a server, or issues a robot command.
    # The Makefile owns the fixture list and build rules; it needs a POSIX sh on
    # PATH (Git for Windows provides one). Fails with a nonzero exit code.
    & $Make "CC=$Compiler" test-windows
    if ($LASTEXITCODE) { throw 'Offline Windows transport fixtures failed.' }
} finally { Pop-Location }
