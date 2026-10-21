@echo off
rem Builds Lumen into dist\Lumen.exe
rem   build.bat          standalone exe (~70 MB, runs anywhere)
rem   build.bat small    small exe (~300 KB, needs the .NET 8 Desktop Runtime on the PC)

if /i "%~1"=="small" (
  dotnet publish Lumen.csproj -c Release --self-contained false -o dist
) else (
  dotnet publish Lumen.csproj -c Release --self-contained true -p:EnableCompressionInSingleFile=true -o dist
)

if errorlevel 1 (
  echo.
  echo BUILD FAILED - see errors above.
  pause
  exit /b 1
)
echo.
echo Done: dist\Lumen.exe
pause
