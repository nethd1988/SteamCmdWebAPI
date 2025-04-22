@echo off
echo ===== Go cai dat dich vu SteamCmdWebAPI =====
echo.

REM Kiem tra quyen quan tri
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo Loi: Ban can chay bat file nay voi quyen quan tri!
    echo Hay click chuot phai va chon "Run as administrator"
    pause
    exit /b 1
)

echo Dang kiem tra SC...
where sc >nul 2>&1
if %errorLevel% neq 0 (
    echo Loi: Khong tim thay lenh SC. Ban dang chay tren Windows phai khong?
    pause
    exit /b 1
)

echo.
echo Dang dung dich vu...
sc stop SteamCmdWebAPI
timeout /t 2 >nul

echo.
echo Dang xoa dich vu...
sc delete SteamCmdWebAPI
if %errorLevel% neq 0 (
    echo Loi: Khong the xoa dich vu!
    pause
    exit /b 1
)

echo.
echo Dich vu SteamCmdWebAPI da duoc go cai dat thanh cong!
echo.

pause