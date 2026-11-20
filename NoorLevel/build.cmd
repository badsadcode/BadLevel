@echo off
rem Builds NoorLevel into .\dist : the previewer EXE, the CLI EXE and NoorLevel.dll.
rem Needs the .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0
rem   build.cmd          small build, needs the .NET 8 Desktop Runtime to run
rem   build.cmd full     self-contained build, runs on any Windows 10/11 x64 without installing .NET
setlocal
cd /d "%~dp0"
set SC=--self-contained false
if /i "%1"=="full" set SC=--self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true
if exist dist rmdir /s /q dist
dotnet test tests\NoorLevel.Tests -c Release || goto :fail
dotnet publish src\NoorLevel.Previewer -c Release -r win-x64 %SC% -p:DebugType=none -o dist\Previewer || goto :fail
dotnet publish src\NoorLevel.Cli -c Release -r win-x64 %SC% -p:DebugType=none -o dist\Cli || goto :fail
dotnet build src\NoorLevel -c Release -o dist\Library || goto :fail
del /q dist\Previewer\*.xml 2>nul
echo.
echo Done:
echo   dist\Previewer\NoorLevelPreviewer.exe   the previewer app
echo   dist\Cli\noorlevel-cli.exe              the command line tool
echo   dist\Library\NoorLevel.dll              the library for your own projects
exit /b 0
:fail
echo Build failed.
exit /b 1
