<#
    Starts the three local services in their own windows.

    Run this from your own terminal rather than asking the assistant to start them:
    processes launched inside an assistant session are killed when that turn ends,
    so they never survive long. Windows started here live until you close them.

        .\run.ps1            # start all three
        .\run.ps1 -Stop      # stop whatever is listening on the three ports

    Ports: 5258 .NET API, 8000 Python processor, 3000 Next.js frontend.
#>
param([switch]$Stop)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$services = @(
    @{ Name = 'API';       Port = 5258
       Dir  = Join-Path $root 'backend\ClinicalAI.Api\ClinicalAI.Api'
       Cmd  = 'dotnet run --launch-profile http' },
    @{ Name = 'Processor'; Port = 8000
       Dir  = Join-Path $root 'document-processor'
       Cmd  = '.\.venv\Scripts\python.exe -m uvicorn main:app --port 8000' },
    @{ Name = 'Frontend';  Port = 3000
       Dir  = Join-Path $root 'frontend\clinical-ai-ui'
       Cmd  = 'npm run dev' }
)

function Get-ListenerPid($port) {
    $c = Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue
    if ($c) { return $c[0].OwningProcess }
    return $null
}

if ($Stop) {
    foreach ($s in $services) {
        $processId = Get-ListenerPid $s.Port
        if ($processId) {
            Stop-Process -Id $processId -Force -ErrorAction SilentlyContinue
            Write-Host ("  stopped {0} on {1} (pid {2})" -f $s.Name, $s.Port, $processId)
        } else {
            Write-Host ("  {0} on {1} was not running" -f $s.Name, $s.Port)
        }
    }
    return
}

foreach ($s in $services) {
    if (Get-ListenerPid $s.Port) {
        Write-Host ("  {0} already listening on {1} - skipping" -f $s.Name, $s.Port)
        continue
    }
    if (-not (Test-Path $s.Dir)) { throw "missing directory: $($s.Dir)" }

    Start-Process powershell -ArgumentList @(
        '-NoExit', '-Command',
        "Set-Location '$($s.Dir)'; Write-Host '=== $($s.Name) :$($s.Port) ===' -ForegroundColor Cyan; $($s.Cmd)"
    ) | Out-Null
    Write-Host ("  starting {0} on {1}" -f $s.Name, $s.Port)
}

# The processor loads the embedding model at startup, so it is the slow one.
Write-Host "`nwaiting for all three (the processor loads the embedding model first)..."
$deadline = (Get-Date).AddSeconds(120)
while ((Get-Date) -lt $deadline) {
    $up = @($services | Where-Object { Get-ListenerPid $_.Port }).Count
    if ($up -eq $services.Count) { break }
    Start-Sleep -Seconds 1
}

Write-Host ''
foreach ($s in $services) {
    $processId = Get-ListenerPid $s.Port
    if ($processId) { Write-Host ("  {0,-10} :{1}  up (pid {2})" -f $s.Name, $s.Port, $processId) -ForegroundColor Green }
    else            { Write-Host ("  {0,-10} :{1}  did not start - check its window" -f $s.Name, $s.Port) -ForegroundColor Red }
}
Write-Host "`n  open http://localhost:3000  (doctor@test.com)"
