@echo off
setlocal enabledelayedexpansion

REM Kiem tra quyen quan tri
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Loi: Ban can chay bat file nay voi quyen quan tri!
    echo Hay click chuot phai va chon "Run as administrator"
    pause
    exit /b 1
)

:menu
cls
echo ===== Quan ly dich vu SteamCmdWebAPI =====
echo.
echo 1. Khoi dong dich vu
echo 2. Dung dich vu
echo 3. Khoi dong lai dich vu
echo 4. Kiem tra trang thai
echo 5. Mo trang web quan ly (http://localhost:5288)
echo 0. Thoat
echo.

set /p choice=Nhap lua chon cua ban: 

if "%choice%"=="1" goto start
if "%choice%"=="2" goto stop
if "%choice%"=="3" goto restart
if "%choice%"=="4" goto status
if "%choice%"=="5" goto open_web
if "%choice%"=="0" goto end

echo.
echo Lua chon khong hop le, hay nhap lai...
timeout /t 2 >nul
goto menu

:start
echo.
echo Dang khoi dong dich vu SteamCmdWebAPI...
sc start SteamCmdWebAPI
if %errorLevel% neq 0 (
    echo Loi: Khong the khoi dong dich vu.
) else (
    echo Dich vu da duoc khoi dong thanh cong!
)
timeout /t 3 >nul
goto menu

:stop
echo.
echo Dang dung dich vu SteamCmdWebAPI...
sc stop SteamCmdWebAPI
if %errorLevel% neq 0 (
    echo Loi: Khong the dung dich vu.
) else (
    echo Dich vu da duoc dung thanh cong!
)
timeout /t 3 >nul
goto menu

:restart
echo.
echo Dang khoi dong lai dich vu SteamCmdWebAPI...
sc stop SteamCmdWebAPI >nul
timeout /t 2 >nul
sc start SteamCmdWebAPI
if %errorLevel% neq 0 (
    echo Loi: Khong the khoi dong lai dich vu.
) else (
    echo Dich vu da duoc khoi dong lai thanh cong!
)
timeout /t 3 >nul
goto menu

:status
echo.
echo Dang kiem tra trang thai dich vu SteamCmdWebAPI...
sc query SteamCmdWebAPI
echo.
pause
goto menu

:open_web
echo.
echo Dang mo trang web quan ly...
start http://localhost:5288
timeout /t 1 >nul
goto menu

:end
echo.
echo Cam on ban da su dung!
timeout /t 2 >nul
exit /b 0