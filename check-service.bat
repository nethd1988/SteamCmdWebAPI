@echo off
chcp 65001 > nul
title Kiểm tra trạng thái SteamCmdWebAPI

echo ================================================
echo     KIỂM TRA TRẠNG THÁI DỊCH VỤ STEAMCMDWEBAPI
echo ================================================
echo.

:: Kiểm tra xem dịch vụ có tồn tại không
sc query SteamCmdWebAPI >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo [THÔNG BÁO] Dịch vụ SteamCmdWebAPI chưa được cài đặt.
    echo Vui lòng chạy script "install-service.bat" hoặc "build-and-install.bat" để cài đặt dịch vụ.
    goto end
)

:: Bật tính năng delayed expansion để sử dụng biến trong vòng lặp
setlocal enabledelayedexpansion

:: Lấy thông tin dịch vụ
echo [THÔNG TIN DỊCH VỤ]
echo.
for /f "tokens=2 delims=:" %%a in ('sc query SteamCmdWebAPI ^| find "STATE"') do (
    set state=%%a
    set state=!state:~1!
)

:: Kiểm tra trạng thái
set isRunning=0
echo !state! | findstr /i "RUNNING" >nul
if not errorlevel 1 (
    set isRunning=1
    echo Trạng thái: ĐANG CHẠY
) else (
    echo !state! | findstr /i "STOPPED" >nul
    if not errorlevel 1 (
        echo Trạng thái: ĐÃ DỪNG
    ) else (
        echo !state! | findstr /i "PAUSED" >nul
        if not errorlevel 1 (
            echo Trạng thái: TẠM DỪNG
        ) else (
            echo Trạng thái: !state!
        )
    )
)

echo.
echo [CHI TIẾT]
sc qc SteamCmdWebAPI | find "BINARY_PATH_NAME" 
sc qc SteamCmdWebAPI | find "START_TYPE"
sc qc SteamCmdWebAPI | find "DISPLAY_NAME"

echo.
if %isRunning%==1 (
    echo [THÔNG BÁO] Ứng dụng SteamCmdWebAPI đang chạy trong nền.
    echo Bạn có thể truy cập ứng dụng qua trình duyệt: http://localhost:5288
    
    echo.
    echo [TÙY CHỌN]
    echo 1. Khởi động lại dịch vụ
    echo 2. Dừng dịch vụ
    echo 3. Thoát
    echo.
    set /p CHOICE="Nhập lựa chọn của bạn (1-3): "
    
    if "%CHOICE%"=="1" (
        echo.
        echo Đang khởi động lại dịch vụ...
        sc stop SteamCmdWebAPI >nul
        timeout /t 2 /nobreak >nul
        sc start SteamCmdWebAPI >nul
        echo Dịch vụ đã được khởi động lại.
    ) else if "%CHOICE%"=="2" (
        echo.
        echo Đang dừng dịch vụ...
        sc stop SteamCmdWebAPI >nul
        echo Dịch vụ đã được dừng.
    )
) else (
    echo [THÔNG BÁO] Ứng dụng SteamCmdWebAPI hiện đang dừng.
    
    echo.
    echo [TÙY CHỌN]
    echo 1. Khởi động dịch vụ
    echo 2. Thoát
    echo.
    set /p CHOICE="Nhập lựa chọn của bạn (1-2): "
    
    if "%CHOICE%"=="1" (
        echo.
        echo Đang khởi động dịch vụ...
        sc start SteamCmdWebAPI >nul
        echo Dịch vụ đã được khởi động.
    )
)

:end
echo.
echo ================================================

pause 