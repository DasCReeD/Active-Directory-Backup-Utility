@echo off
:: ==============================================================================
:: AD Shield Network Backup Control Center Launch Bootstrapper
:: Designed for Windows Task Scheduler / Startup Scripts execution
:: ==============================================================================

:: Force working directory to the directory where this launch script is located
cd /d "%~dp0"

:: Log execution attempt
echo [%date% %time%] Launching AD Shield Service Control Box...

:: Verify Node.js is available in the path
where node >nul 2>nul
if %errorlevel% neq 0 (
    echo ERROR: Node.js was not found in the system PATH.
    echo Please install Node.js (v18+) and make sure it is added to system PATH variables.
    pause
    exit /b 1
)

:: Auto-install dependencies if node_modules are deleted or missing
if not exist "node_modules\" (
    echo node_modules folder not found. Auto-installing dependencies...
    call npm install --omit=dev
)

:: Set Execution Environment Flags
set PORT=3000
set NODE_ENV=production

:: Set local PowerShell environment flags to bypass execution restrictions in spawned subprocesses
set PSExecutionPolicyPreference=Bypass

:: Launch the main Orchestrator Express engine
node server.js
