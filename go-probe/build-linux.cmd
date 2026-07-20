@echo off
rem Сборка Go-пробы под Linux x86-64 (CentOS и любой другой дистрибутив).
rem Результат — папка dist\twamp-probe-go: скопировать на машину и запустить.
setlocal
set CGO_ENABLED=0
set GOOS=linux
set GOARCH=amd64
rmdir /s /q dist 2>nul
mkdir dist\twamp-probe-go
go build -trimpath -ldflags="-s -w" -o dist\twamp-probe-go\twamp-probe .
xcopy /e /i /q ..\SPI.TWamp.Probe\twampy dist\twamp-probe-go\twampy > nul
del /q dist\twamp-probe-go\twampy\__pycache__\* 2>nul
rmdir dist\twamp-probe-go\twampy\__pycache__ 2>nul
copy /y deploy\appsettings.json dist\twamp-probe-go\ > nul
copy /y deploy\README-DEPLOY.txt dist\twamp-probe-go\ > nul
echo Готово: dist\twamp-probe-go
@pause
