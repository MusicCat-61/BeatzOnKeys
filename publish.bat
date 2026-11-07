@echo off
setlocal EnableExtensions DisableDelayedExpansion
chcp 65001 >nul

for %%I in ("%~dp0.") do set "ROOT=%%~fI"
set "PROJECT=%ROOT%\BeatzOnKeys.UI\BeatzOnKeys.UI.csproj"
for %%I in ("%ROOT%\Build_Output") do set "OUTPUT=%%~fI"
for %%I in ("%OUTPUT%") do set "OUTPUT_NAME=%%~nxI"
for %%I in ("%OUTPUT%\..") do set "OUTPUT_PARENT=%%~fI"
if not defined BOK_VERSION set "BOK_VERSION=1.3.1.3"

if /I not "%OUTPUT_NAME%"=="Build_Output" goto :unsafe_output
if /I not "%OUTPUT_PARENT%"=="%ROOT%" goto :unsafe_output
if not exist "%ROOT%\BeatzOnKeys.sln" goto :unsafe_output
if not exist "%PROJECT%" goto :unsafe_output

echo ========================================================
echo  BeatzOnKeys %BOK_VERSION% portable publisher (.NET 10)
echo ========================================================
echo.

if exist "%OUTPUT%" (
    echo [1/3] Removing verified output folder:
    echo       %OUTPUT%
    rmdir /s /q "%OUTPUT%"
    if exist "%OUTPUT%" goto :fail
) else (
    echo [1/3] Output folder is already clean:
    echo       %OUTPUT%
)

echo.
echo [2/3] Publishing single-file self-contained WPF application...
dotnet publish "%PROJECT%" -c Release -r win-x64 --self-contained true -p:Version=%BOK_VERSION% -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -p:DebugSymbols=false -o "%OUTPUT%"
if errorlevel 1 goto :fail
if not exist "%OUTPUT%\BeatzOnKeys.exe" goto :fail

echo.
echo [3/3] Copying documentation and licenses...
call :copy_file "%ROOT%\README.md" "%OUTPUT%\README.md"
if errorlevel 1 goto :fail
call :copy_file "%ROOT%\README.en.md" "%OUTPUT%\README.en.md"
if errorlevel 1 goto :fail
call :copy_file "%ROOT%\RELEASE_NOTES.md" "%OUTPUT%\RELEASE_NOTES.md"
if errorlevel 1 goto :fail
call :copy_file "%ROOT%\LICENSE" "%OUTPUT%\LICENSE"
if errorlevel 1 goto :fail
call :copy_file "%ROOT%\THIRD-PARTY-NOTICES.md" "%OUTPUT%\THIRD-PARTY-NOTICES.md"
if errorlevel 1 goto :fail

echo.
echo ========================================================
echo  DONE: %OUTPUT%
echo.
echo  BeatzOnKeys.exe is the whole application: no installer,
echo  administrator rights, or separate .NET runtime is needed.
echo  The documentation files next to it are optional.
echo ========================================================
exit /b 0

:copy_file
if not exist "%~1" (
    echo ERROR: Required file is missing: %~1
    exit /b 1
)
copy /y "%~1" "%~2" >nul
if errorlevel 1 (
    echo ERROR: Could not copy: %~1
    exit /b 1
)
exit /b 0

:copy_tree
if not exist "%~1\" (
    echo ERROR: Required directory is missing: %~1
    exit /b 1
)
xcopy "%~1\*" "%~2\" /e /i /y >nul
if errorlevel 1 (
    echo ERROR: Could not copy directory: %~1
    exit /b 1
)
exit /b 0

:unsafe_output
echo.
echo ========================================================
echo  PUBLISH ABORTED: unsafe output path.
echo  Expected: %ROOT%\Build_Output
echo  Actual:   %OUTPUT%
echo ========================================================
exit /b 1

:fail
echo.
echo ========================================================
echo  PUBLISH FAILED. Review the error above.
echo ========================================================
exit /b 1
