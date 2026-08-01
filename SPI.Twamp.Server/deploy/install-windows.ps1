# Установка SPI TWamp Server как службы Windows.
#
# Что делает:
#   1. копирует файлы в C:\Program Files\TwampServer (или в -Path);
#   2. регистрирует службу TwampServer с автозапуском;
#   3. открывает порт сервера в брандмауэре;
#   4. запускает службу и проверяет, что веб-интерфейс отвечает.
#
# Запуск в PowerShell от имени администратора:
#   powershell -ExecutionPolicy Bypass -File .\install-windows.ps1
#
# Повторный запуск обновляет версию: служба останавливается, файлы заменяются,
# служба стартует снова. Конфигурация и база данных при этом не затираются.

[CmdletBinding()]
param(
    [string]$Path = "$env:ProgramFiles\TwampServer",
    [string]$ServiceName = "TwampServer"
)

$ErrorActionPreference = 'Stop'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Нужны права администратора: запустите PowerShell «от имени администратора»"
}

$source = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $Path 'SPI.Twamp.Server.exe'

if (-not (Test-Path (Join-Path $source 'SPI.Twamp.Server.exe'))) {
    throw "Рядом со скриптом нет SPI.Twamp.Server.exe — запускайте его из распакованной папки"
}

# Вариант framework требует установленного .NET; самодостаточному он не нужен.
if (-not (Test-Path (Join-Path $source 'hostpolicy.dll')) -and
    -not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw @"
Это сборка framework — нужен .NET 10 Runtime (ASP.NET Core):
  https://dotnet.microsoft.com/download/dotnet/10.0
Либо возьмите архив selfcontained — ему .NET не нужен.
"@
}

Write-Host "=== 1. Файлы сервера -> $Path"
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne 'Stopped') {
    # Файл работающей службы заменить нельзя — Windows держит его заблокированным.
    Write-Host "    останавливаем работающую службу"
    Stop-Service -Name $ServiceName -Force
    $existing.WaitForStatus('Stopped', '00:01:00')
}

if (-not (Test-Path $Path)) { New-Item -ItemType Directory -Path $Path | Out-Null }

# Конфигурацию и базу не трогаем: на работающем сервере там боевые данные.
$keep = @('appsettings.json', 'TWamp.db', 'TWamp-log.db', 'spool')
Get-ChildItem -Path $source -Exclude 'install-windows.ps1' | ForEach-Object {
    if ($keep -contains $_.Name -and (Test-Path (Join-Path $Path $_.Name))) {
        Write-Host "    $($_.Name) уже есть — оставляем"
        return
    }
    Copy-Item -Path $_.FullName -Destination $Path -Recurse -Force
}

Write-Host "=== 2. Служба $ServiceName"
if ($existing) {
    & sc.exe config $ServiceName binPath= "`"$exe`"" start= auto | Out-Null
    Write-Host "    служба уже зарегистрирована, параметры обновлены"
} else {
    New-Service -Name $ServiceName -BinaryPathName "`"$exe`"" `
        -DisplayName 'SPI TWamp Server' -StartupType Automatic `
        -Description 'Сервер SPI TWamp: задания пробам, отчёты, веб-интерфейс' | Out-Null
    Write-Host "    служба создана"
}

# Перед выходом сервер дописывает буфер результатов — даём ему время
# и поднимаем службу обратно, если она упала.
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null

Write-Host "=== 3. Порт в брандмауэре"
$port = 9000
$configPath = Join-Path $Path 'appsettings.json'
if (Test-Path $configPath) {
    $urls = (Get-Content $configPath -Raw | ConvertFrom-Json).Urls
    if ($urls -match ':(\d+)') { $port = [int]$Matches[1] }
}
$ruleName = "SPI TWamp Server ($port)"
if (-not (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Protocol TCP `
        -LocalPort $port -Action Allow | Out-Null
    Write-Host "    правило добавлено: TCP $port"
} else {
    Write-Host "    правило уже есть: TCP $port"
}

Write-Host "=== 4. Запуск"
Start-Service -Name $ServiceName
(Get-Service -Name $ServiceName).WaitForStatus('Running', '00:01:00')
Start-Sleep -Seconds 3

Write-Host "    состояние службы: $((Get-Service -Name $ServiceName).Status)"
try {
    $answer = Invoke-WebRequest -Uri "http://127.0.0.1:$port/" -UseBasicParsing -TimeoutSec 15
    Write-Host "    ответ веб-интерфейса: HTTP $($answer.StatusCode)"
} catch {
    Write-Warning "    сервер не ответил на порту $port — смотрите журнал в $Path\log"
}

Write-Host ""
Write-Host "Готово. Веб-интерфейс: http://localhost:$port/"
Write-Host "Управление: Start-Service $ServiceName | Stop-Service $ServiceName"
