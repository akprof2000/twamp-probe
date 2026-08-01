# Установка SPI TWamp Probe (Go) как службы Windows.
#
# Что делает:
#   1. копирует файлы в C:\Program Files\TwampProbe (или в -Path);
#   2. регистрирует службу TwampProbe с автозапуском;
#   3. открывает порт пробы в брандмауэре;
#   4. запускает службу и проверяет, что она отвечает.
#
# Запуск в PowerShell от имени администратора:
#   powershell -ExecutionPolicy Bypass -File .\install-windows.ps1
#
# Повторный запуск обновляет версию: служба останавливается, файлы заменяются,
# служба стартует снова. Ваш appsettings.json при этом не затирается.

[CmdletBinding()]
param(
    [string]$Path = "$env:ProgramFiles\TwampProbe",
    [string]$ServiceName = "TwampProbe"
)

$ErrorActionPreference = 'Stop'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Нужны права администратора: запустите PowerShell «от имени администратора»"
}

$source = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $Path 'twamp-probe.exe'

if (-not (Test-Path (Join-Path $source 'twamp-probe.exe'))) {
    throw "Рядом со скриптом нет twamp-probe.exe — запускайте его из распакованной папки"
}

Write-Host "=== 1. Файлы пробы -> $Path"
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne 'Stopped') {
    # Файл работающей службы заменить нельзя — Windows держит его заблокированным.
    Write-Host "    останавливаем работающую службу"
    Stop-Service -Name $ServiceName -Force
    $existing.WaitForStatus('Stopped', '00:00:30')
}

if (-not (Test-Path $Path)) { New-Item -ItemType Directory -Path $Path | Out-Null }

# Конфигурацию не затираем: на работающей пробе там свои настройки.
$keepConfig = Test-Path (Join-Path $Path 'appsettings.json')
Get-ChildItem -Path $source -Exclude 'install-windows.ps1' | ForEach-Object {
    if ($keepConfig -and $_.Name -eq 'appsettings.json') {
        Write-Host "    appsettings.json уже есть — оставляем"
        return
    }
    Copy-Item -Path $_.FullName -Destination $Path -Recurse -Force
}

Write-Host "=== 2. Служба $ServiceName"
if ($existing) {
    # Путь мог измениться — обновляем регистрацию, а не создаём вторую службу.
    & sc.exe config $ServiceName binPath= "`"$exe`"" start= auto | Out-Null
    Write-Host "    служба уже зарегистрирована, параметры обновлены"
} else {
    New-Service -Name $ServiceName -BinaryPathName "`"$exe`"" `
        -DisplayName 'SPI TWamp Probe' -StartupType Automatic `
        -Description 'Проба SPI TWamp: выполняет замеры по заданиям сервера' | Out-Null
    Write-Host "    служба создана"
}

# Рабочий каталог службы — папка с программой: рядом лежат appsettings.json,
# реестр задач и журнал. Служба Windows иначе стартовала бы в System32.
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Null

Write-Host "=== 3. Порт в брандмауэре"
$port = 8443
$configPath = Join-Path $Path 'appsettings.json'
if (Test-Path $configPath) {
    $urls = (Get-Content $configPath -Raw | ConvertFrom-Json).Urls
    if ($urls -match ':(\d+)') { $port = [int]$Matches[1] }
}
$ruleName = "SPI TWamp Probe ($port)"
if (-not (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Protocol TCP `
        -LocalPort $port -Action Allow | Out-Null
    Write-Host "    правило добавлено: TCP $port"
} else {
    Write-Host "    правило уже есть: TCP $port"
}

Write-Host "=== 4. Запуск"
Start-Service -Name $ServiceName
(Get-Service -Name $ServiceName).WaitForStatus('Running', '00:00:30')
Start-Sleep -Seconds 2

$service = Get-Service -Name $ServiceName
Write-Host "    состояние службы: $($service.Status)"

try {
    $answer = Invoke-WebRequest -Uri "http://127.0.0.1:$port/api/probeinterface/taskids" `
        -UseBasicParsing -TimeoutSec 10
    Write-Host "    ответ пробы: HTTP $($answer.StatusCode)"
} catch {
    Write-Warning "    проба не ответила на порту $port — смотрите журнал $Path\log\probe.log"
}

Write-Host ""
Write-Host "Готово. Журнал: $Path\log\probe.log"
Write-Host "Управление: Start-Service $ServiceName | Stop-Service $ServiceName"
Write-Host "На сервере: «Статус проб» -> «Опросить пробу» (http://<адрес>:$port) -> «Подтвердить»."
