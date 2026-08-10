@echo off
rem Установка пробы SPI TWamp службой Windows — одной командой.
rem
rem Зачем поверх install.ps1: запуск скрипта PowerShell требует и обхода
rem политики выполнения, и прав администратора. Набирать это руками каждый раз
rem незачем — здесь оно уже прописано, а сам файл открывается двойным щелчком.
rem
rem Запуск:  install.cmd
rem Аргументы передаются дальше, например:  install.cmd -Port 9443

setlocal
set SCRIPT=%~dp0install.ps1

rem Права администратора: без них не поставить службу и не открыть порт.
net session >nul 2>&1
if errorlevel 1 (
    echo Нужны права администратора — перезапускаю с запросом...
    powershell -NoProfile -ExecutionPolicy Bypass -Command ^
        "Start-Process -Verb RunAs -FilePath '%ComSpec%' -ArgumentList '/c','""%~f0"" %*','&&','pause'"
    exit /b
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%SCRIPT%" %*
exit /b %errorlevel%
