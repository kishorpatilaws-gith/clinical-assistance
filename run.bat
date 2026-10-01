@echo off
setlocal EnableDelayedExpansion

rem ---------------------------------------------------------------------------
rem  Starts the three local services, each in its own window.
rem
rem    run.bat          start everything that is not already running
rem    run.bat stop     stop whatever is listening on the three ports
rem
rem  Ports:  5258 .NET API   8000 Python processor   3000 Next.js frontend
rem
rem  Run this from your own machine rather than from an assistant session:
rem  processes started inside a session are killed when that session ends.
rem ---------------------------------------------------------------------------

set "ROOT=%~dp0"
@REM  set "API_DIR=%ROOT%backend\ClinicalAI.Api\ClinicalAI.Api"
set "PROC_DIR=%ROOT%document-processor"
set "WEB_DIR=%ROOT%frontend\clinical-ai-ui"

if /i "%~1"=="stop" goto :stop

rem --- sanity checks, so a missing piece fails here and not in a window that
rem     flashes past too fast to read -------------------------------------------
@REM  if not exist "%API_DIR%\ClinicalAI.Api.csproj" (
@REM    echo [X] API project not found at %API_DIR%
@REM    goto :fail
@REM  )
if not exist "%PROC_DIR%\main.py" (
  echo [X] Document processor not found at %PROC_DIR%
  goto :fail
)
if not exist "%PROC_DIR%\.venv\Scripts\python.exe" (
  echo [X] Python virtual environment missing.
  echo     Create it with:
  echo         cd document-processor
  echo         python -m venv .venv
  echo         .venv\Scripts\python.exe -m pip install -r requirements.txt
  goto :fail
)
if not exist "%PROC_DIR%\.env" (
  echo [X] document-processor\.env is missing - Qdrant credentials live there.
  echo     Copy .env.example to .env and fill it in.
  goto :fail
)
if not exist "%WEB_DIR%\node_modules" (
  echo [X] Frontend dependencies missing. Run: cd frontend\clinical-ai-ui ^&^& npm install
  goto :fail
)

echo.
echo  Clinical AI platform
echo  --------------------

@REM  call :start_one 5258 "Clinical API :5258"       "%API_DIR%"  "dotnet run --launch-profile http"
call :start_one 8000 "Doc Processor :8000"      "%PROC_DIR%" ".venv\Scripts\python.exe -m uvicorn main:app --port 8000"
call :start_one 3000 "Frontend :3000"           "%WEB_DIR%"  "npm run dev"

echo.
echo  Waiting for all three to come up.
echo  The processor loads its embedding model first, so it is the slow one.

set /a WAITED=0
:waitloop
set "ALL_UP=1"
@REM  call :is_up 5258 || set "ALL_UP=0"
call :is_up 8000 || set "ALL_UP=0"
call :is_up 3000 || set "ALL_UP=0"
if "!ALL_UP!"=="1" goto :ready
if !WAITED! GEQ 120 goto :ready
rem  ping is the portable one-second sleep: `timeout` refuses to run when
  rem  stdin is redirected, which happens whenever this is called from a script.
  ping -n 2 127.0.0.1 >nul 2>&1
set /a WAITED+=1
goto :waitloop

:ready
echo.
@REM  call :report 5258 "API      "
call :report 8000 "Processor"
call :report 3000 "Frontend "
echo.
if "!ALL_UP!"=="1" (
  echo  Open http://localhost:3000   sign in as doctor@test.com
  start "" http://localhost:3000
) else (
  echo  Something did not start. Check the windows that opened for the error.
)
echo.
echo  Stop everything later with:  run.bat stop
echo.
goto :eof

rem ---------------------------------------------------------------------------
:start_one
rem  %1 port   %2 window title   %3 working directory   %4 command
call :is_up %~1 && (
  echo  [=] %~2 already running, left alone
  goto :eof
)
echo  [^>] starting %~2
start "%~2" cmd /k "cd /d %~3 && echo === %~2 === && %~4"
goto :eof

rem ---------------------------------------------------------------------------
:is_up
rem  Sets errorlevel 0 when something is listening on port %1, 1 otherwise.
netstat -ano | findstr /r /c:":%~1 .*LISTENING" >nul 2>&1
exit /b %errorlevel%

rem ---------------------------------------------------------------------------
:report
call :is_up %~1 && (
  echo    %~2 :%~1  up
) || (
  echo    %~2 :%~1  DOWN
)
goto :eof

rem ---------------------------------------------------------------------------
:stop
echo.
echo  Stopping services
echo  -----------------
@REM  call :stop_one 5258 "API      "
call :stop_one 8000 "Processor"
call :stop_one 3000 "Frontend "
echo.
goto :eof

:stop_one
set "FOUND="
for /f "tokens=5" %%P in ('netstat -ano ^| findstr /r /c:":%~1 .*LISTENING"') do (
  if not "%%P"=="0" (
    taskkill /PID %%P /F >nul 2>&1
    set "FOUND=1"
  )
)
if defined FOUND (
  echo    %~2 :%~1  stopped
) else (
  echo    %~2 :%~1  was not running
)
goto :eof

rem ---------------------------------------------------------------------------
:fail
echo.
echo  Nothing was started.
exit /b 1
