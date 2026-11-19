@echo off
rem Локальная публикация сервера (проба собирается отдельно — см. go-probe\build-linux.cmd).
dotnet publish SPI.Twamp.Collector -c Release -r linux-x64 --self-contained true -o publish/Collector || exit /b 1
cd SPI.Twamp.Server
@dotnet publish -c Release -r linux-x64 --self-contained true -v n -o ../publish/Server
cd ..
@pause
