@echo off
cd SPI.TWamp.Probe
@dotnet publish -c Release -r linux-x64  --self-contained true -v n -o ../publish/Probe
cd ..
cd SPI.TWamp.Server
@dotnet publish -c Release -r linux-x64  --self-contained true -v n -o ../publish/Server
cd ..
del /f publish\Probe\appsettings*.json
del /f publish\Server\appsettings*.json
@pause