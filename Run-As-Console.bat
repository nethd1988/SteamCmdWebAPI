@echo off
echo ===== Chay SteamCmdWebAPI o che do Console =====
echo.

set INSTALL_DIR=%~dp0
cd /d "%INSTALL_DIR%"

echo Dang kiem tra file thuc thi...
if not exist "%INSTALL_DIR%SteamCmdWebAPI.exe" (
    echo Loi: Khong tim thay file SteamCmdWebAPI.exe!
    pause
    exit /b 1
)

echo.
echo Dang chay ung dung o che do Console...
echo Hay kiem tra loi xuat hien va gui lai cho nha phat trien.
echo Nhan Ctrl+C de dung.
echo.

"%INSTALL_DIR%SteamCmdWebAPI.exe"

echo.
if %errorlevel% neq 0 (
    echo Ung dung da dung voi ma loi: %errorlevel%
) else (
    echo Ung dung da dung binh thuong.
)

pause