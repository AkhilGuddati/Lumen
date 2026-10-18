@echo off
rem Small exe (~300 KB). Needs the .NET 8 Desktop Runtime on the PC.
rem dotnet publish Lumen.csproj -c Release -o dist
if errorlevel 1 (
  echo.
  echo BUILD FAILED - see errors above.
  pause
  exit /b 1
)
rem Standalone exe (~70 MB, runs anywhere) - replace the line above with:
dotnet publish Lumen.csproj -c Release --self-contained true -p:EnableCompressionInSingleFile=true -o dist
echo.
echo Done: dist\Lumen.exe
pause
