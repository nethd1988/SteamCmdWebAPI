@echo off
echo ===== Cai dat dich vu SteamCmdWebAPI =====
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
echo Dang kiem tra thu muc cai dat...
set INSTALL_DIR=%~dp0
cd /d "%INSTALL_DIR%"

echo Dang kiem tra file thuc thi...
if not exist "%INSTALL_DIR%SteamCmdWebAPI.exe" (
    echo Loi: Khong tim thay file SteamCmdWebAPI.exe!
    echo Dam bao ban dang chay file bat nay tu thu muc cai dat.
    pause
    exit /b 1
)

echo.
echo Dang xoa dich vu cu neu ton tai...
sc stop SteamCmdWebAPI >nul 2>&1
sc delete SteamCmdWebAPI >nul 2>&1
timeout /t 2 >nul

echo.
echo Dang cai dat dich vu moi...
sc create SteamCmdWebAPI binPath= "\"%INSTALL_DIR%SteamCmdWebAPI.exe\"" start= auto DisplayName= "SteamCmdWebAPI Service"
if %errorLevel% neq 0 (
    echo Loi: Khong the tao dich vu!
    pause
    exit /b 1
)

echo.
echo Dang cau hinh dich vu...
sc description SteamCmdWebAPI "Dich vu SteamCmdWebAPI - Quan ly cau hinh va cap nhat game Steam tu dong"
sc failure SteamCmdWebAPI reset= 86400 actions= restart/60000/restart/60000/restart/60000
sc config SteamCmdWebAPI obj= "LocalSystem" password= ""

REM Dat quyen cho thu muc
echo.
echo Dang dat quyen cho thu muc cai dat...
icacls "%INSTALL_DIR%" /grant "LocalSystem":(OI)(CI)F /T

echo.
echo Dang khoi dong dich vu...
sc start SteamCmdWebAPI
if %errorLevel% neq 0 (
    echo Canh bao: Khong the khoi dong dich vu tu dong.
    echo Ban co the can khoi dong thu cong tu Services Manager
) else (
    echo Dich vu da duoc khoi dong thanh cong!
)

echo.
echo Dich vu SteamCmdWebAPI da duoc cai dat thanh cong!
echo Ban co the truy cap dich vu tai http://localhost:5288
echo.
echo Thong tin dich vu:
echo - Ten dich vu: SteamCmdWebAPI
echo - Thu muc cai dat: %INSTALL_DIR%
echo - Che do khoi dong: Tu dong
echo.
echo De quan ly dich vu, hay mo Services Manager (services.msc)
echo.

pause