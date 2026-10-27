@echo off
setlocal
rem ============================================================
rem  Build distributable single-file exes.
rem
rem  Usage:  tools\publish-release.bat [all|full|lite] [win-x64|win-arm64]
rem    all  - build both (default)
rem    full - self-contained, no .NET needed on target (~78 MB)
rem    lite - framework-dependent, needs .NET 10 Desktop Runtime (~2 MB)
rem  Output: dist\
rem  Note:   ASCII only on purpose (cmd.exe uses the OEM codepage).
rem ============================================================

set "MODE=%~1"
if "%MODE%"=="" set "MODE=all"
set "RID=%~2"
if "%RID%"=="" set "RID=win-x64"

pushd "%~dp0.." || goto :fail

if /i "%MODE%"=="all" goto :run_all
if /i "%MODE%"=="full" goto :run_full
if /i "%MODE%"=="lite" goto :run_lite
echo Unknown mode "%MODE%" - use all, full or lite.
goto :fail

:run_all
call :build full
if errorlevel 1 goto :fail
call :build lite
if errorlevel 1 goto :fail
goto :done

:run_full
call :build full
if errorlevel 1 goto :fail
goto :done

:run_lite
call :build lite
if errorlevel 1 goto :fail
goto :done

rem ------------------------------------------------------------
rem  :build  %1 = full | lite
rem ------------------------------------------------------------
:build
if /i "%~1"=="full" goto :build_full

echo.
echo === Publishing LITE ^(framework-dependent^) ===
rem  Note: EnableCompressionInSingleFile / PublishReadyToRun are only
rem  valid for self-contained publish (NETSDK1176), so they are omitted here.
dotnet publish "src\Fangcun\Fangcun.csproj" -c Release -r %RID% --self-contained false ^
  -p:PublishSingleFile=true ^
  -p:DebugType=none ^
  -o "dist\_lite"
if errorlevel 1 exit /b 1
if not exist dist mkdir dist
move /y "dist\_lite\Fangcun.exe" "dist\Fangcun-%RID%-lite.exe" >nul
rmdir /s /q "dist\_lite"
exit /b 0

:build_full
echo.
echo === Publishing FULL ^(self-contained^) ===
dotnet publish "src\Fangcun\Fangcun.csproj" -c Release -r %RID% --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -p:PublishReadyToRun=true ^
  -p:DebugType=none ^
  -o "dist\_full"
if errorlevel 1 exit /b 1
if not exist dist mkdir dist
move /y "dist\_full\Fangcun.exe" "dist\Fangcun-%RID%-full.exe" >nul
rmdir /s /q "dist\_full"
exit /b 0

:done
echo.
echo === Done. Output in dist\ ===
dir /b dist
echo.
echo   -full.exe : self-contained, runs anywhere ^(no .NET install needed^)
echo   -lite.exe : needs .NET 10 Desktop Runtime on the target machine
popd
exit /b 0

:fail
echo.
echo PUBLISH FAILED - see the errors above.
popd
exit /b 1
