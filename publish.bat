@echo off
setlocal EnableExtensions
chcp 65001 >nul

set "ROOT=%~dp0"
set "OUTPUT=%ROOT%Build_Output"
set "PROJECT=%ROOT%BeatzOnKeys.UI\BeatzOnKeys.UI.csproj"
if not defined BOK_VERSION set "BOK_VERSION=1.0.0"

echo ========================================================
echo  BeatzOnKeys portable publisher (.NET 10 + Basic Pitch)
echo ========================================================
echo.

if exist "%OUTPUT%" (
    echo Removing previous Build_Output...
    rmdir /s /q "%OUTPUT%"
    if exist "%OUTPUT%" goto :fail
)

echo [1/3] Publishing self-contained WPF application...
dotnet publish "%PROJECT%" -c Release -r win-x64 --self-contained true -p:Version=%BOK_VERSION% -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false -o "%OUTPUT%"
if errorlevel 1 goto :fail

echo.
echo [2/3] Building bundled Python and Basic Pitch runtime...
powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%build\build-python.ps1" -Destination "%OUTPUT%\python"
if errorlevel 1 goto :fail

echo.
echo [3/3] Copying documentation and licenses...
copy /y "%ROOT%README.md" "%OUTPUT%\README.md" >nul
copy /y "%ROOT%README.ru.md" "%OUTPUT%\README.ru.md" >nul
copy /y "%ROOT%LICENSE" "%OUTPUT%\LICENSE" >nul
copy /y "%ROOT%THIRD-PARTY-NOTICES.md" "%OUTPUT%\THIRD-PARTY-NOTICES.md" >nul
xcopy "%ROOT%third-party" "%OUTPUT%\third-party\" /e /i /y >nul
xcopy "%ROOT%assets" "%OUTPUT%\assets\" /e /i /y >nul

echo.
echo ========================================================
echo  DONE: %OUTPUT%
echo.
echo  BeatzOnKeys.exe and the python folder are one portable set.
echo  Copy the whole Build_Output folder. No installer, Python,
echo  administrator rights, or separate .NET runtime is required.
echo ========================================================
exit /b 0

:fail
echo.
echo ========================================================
echo  PUBLISH FAILED. Review the error above.
echo ========================================================
exit /b 1
