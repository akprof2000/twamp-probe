@echo off
rem Локальная публикация сервера (проба собирается отдельно — см. go-probe\build-linux.cmd).
cd SPI.Twamp.Server
@dotnet publish -c Release -r linux-x64 --self-contained true -v n -o ../publish/Server
cd ..
@pause
