@echo off
cd SPI.TWamp.Probe
@dotnet publish -c Release -r linux-x64  --self-contained true -v n -o ../publish/Probe
cd ..
cd SPI.TWamp.Server
@dotnet publish -c Release -r linux-x64  --self-contained true -v n -o ../publish/Server
cd ..
rem del /f publish\Probe\appsettings*.json
rem del /f publish\Server\appsettings*.json
@pause