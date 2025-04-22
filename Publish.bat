@echo off
echo ===== Xuat ban SteamCmdWebAPI =====
echo.

REM Kiem tra dotnet
where dotnet >nul 2>&1
if %errorLevel% neq 0 (
    echo Loi: Khong tim thay .NET SDK!
    echo Hay cai dat .NET SDK tu https://dotnet.microsoft.com/download
    pause
    exit /b 1
)

echo Dang kiem tra thu muc du an...
set PROJECT_DIR=%~dp0
cd /d "%PROJECT_DIR%"

echo.
echo Xuat ban ung dung SteamCmdWebAPI...
dotnet publish -c Release -p:PublishProfile=FolderProfile

if %errorLevel% neq 0 (
    echo.
    echo Loi: Khong the xuat ban ung dung!
    pause
    exit /b 1
)

echo.
echo Dang sao chep cac file bat...
copy "%PROJECT_DIR%Install-Service.bat" "%PROJECT_DIR%bin\Release\net9.0\publish\" /Y
copy "%PROJECT_DIR%Uninstall-Service.bat" "%PROJECT_DIR%bin\Release\net9.0\publish\" /Y
copy "%PROJECT_DIR%Service-Control.bat" "%PROJECT_DIR%bin\Release\net9.0\publish\" /Y

echo.
echo Xuat ban thanh cong!
echo.
echo Duong dan den file da xuat ban: %PROJECT_DIR%bin\Release\net9.0\publish\
echo.
echo Ban co the sao chep thu muc publish den server va chay Install-Service.bat de cai dat dich vu.
echo.

pause