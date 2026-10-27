@echo off
setlocal
rem ============================================================
rem  Build a self-contained single-file exe for distribution.
rem  Usage:  tools\publish-release.bat [win-x64|win-arm64]
rem  Output: dist\Fangcun.exe
rem  Note:   ASCII only on purpose (cmd.exe uses the OEM codepage).
rem ============================================================

set "RID=%~1"
if "%RID%"=="" set "RID=win-x64"

pushd "%~dp0.." || goto :fail

echo [1/3] Cleaning previous output ...
if exist dist rmdir /s /q dist

echo [2/3] Publishing (%RID%, self-contained, single file) ...
dotnet publish "src\Fangcun\Fangcun.csproj" -c Release -r %RID% --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -p:PublishReadyToRun=true ^
  -p:DebugType=none ^
  -o dist
if errorlevel 1 goto :fail

echo [3/3] Done. Output:
dir /b dist
echo.
echo Distribute the files in "dist" (Fangcun.exe is the program).
popd
exit /b 0

:fail
echo.
echo PUBLISH FAILED - see the errors above.
popd
exit /b 1
