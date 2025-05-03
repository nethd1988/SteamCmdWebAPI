@echo off
chcp 65001 > nul
title Cài đặt SteamCmdWebAPI như một dịch vụ Windows ẩn

echo ================================================
echo    CÀI ĐẶT DỊCH VỤ STEAMCMDWEBAPI (CHẾ ĐỘ ẨN)
echo ================================================
echo.

:: Kiểm tra quyền admin
net session >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo [LỖI] Bạn cần chạy script này với quyền Administrator.
    echo Vui lòng nhấp chuột phải vào file và chọn "Run as administrator".
    pause
    exit /b 1
)

:: Xác định đường dẫn đến thư mục chứa script
set "SCRIPT_DIR=%~dp0"
set "EXE_PATH=%SCRIPT_DIR%publish\SteamCmdWebAPI.exe"

:: Kiểm tra xem file exe có tồn tại không
if not exist "%EXE_PATH%" (
    echo [LỖI] Không tìm thấy file SteamCmdWebAPI.exe trong thư mục publish
    echo Vui lòng chạy file build.bat trước khi cài đặt dịch vụ
    pause
    exit /b 1
)

:: Dừng và xóa dịch vụ cũ nếu đã tồn tại
sc query SteamCmdWebAPI >nul 2>&1
if %ERRORLEVEL% EQU 0 (
    echo [THÔNG BÁO] Dịch vụ SteamCmdWebAPI đã tồn tại. Đang dừng và xóa dịch vụ...
    sc stop SteamCmdWebAPI >nul 2>&1
    timeout /t 2 /nobreak >nul
    sc delete SteamCmdWebAPI >nul 2>&1
    timeout /t 2 /nobreak >nul
)

echo [THÔNG BÁO] Đang cài đặt SteamCmdWebAPI như một dịch vụ Windows...

:: Tạo tham số cho dịch vụ để chạy ở chế độ ẩn
set "SERVICE_PARAMS=binPath=\"\"%EXE_PATH%\"\" --quiet\" DisplayName= \"SteamCmdWebAPI Service\" start= auto"

:: Tạo dịch vụ
sc create SteamCmdWebAPI %SERVICE_PARAMS%
if %ERRORLEVEL% NEQ 0 (
    echo [LỖI] Không thể tạo dịch vụ!
    pause
    exit /b 1
)

:: Cấu hình dịch vụ
sc description SteamCmdWebAPI "SteamCmdWebAPI là dịch vụ quản lý và tự động hóa các tác vụ Steam, chạy ở chế độ ẩn không hiển thị logs."

:: Tạo thư mục logs nếu chưa tồn tại
if not exist "%SCRIPT_DIR%publish\logs" mkdir "%SCRIPT_DIR%publish\logs"

:: Bắt đầu dịch vụ
echo [THÔNG BÁO] Đang khởi động dịch vụ...
sc start SteamCmdWebAPI >nul 2>&1

:: Kiểm tra xem dịch vụ đã khởi động chưa
sc query SteamCmdWebAPI | find "RUNNING" >nul
if %ERRORLEVEL% NEQ 0 (
    echo [CẢNH BÁO] Không thể khởi động dịch vụ. Vui lòng khởi động thủ công.
) else (
    echo [THÀNH CÔNG] Dịch vụ SteamCmdWebAPI đã được cài đặt và khởi động thành công!
    echo Dịch vụ đang chạy trong nền và không hiển thị logs trên màn hình.
)

echo.
echo Thông tin dịch vụ:
echo - Tên: SteamCmdWebAPI
echo - Đường dẫn: %EXE_PATH%
echo - Kiểu khởi động: Tự động (khởi động cùng Windows)
echo - Chế độ: Ẩn, không hiển thị logs
echo.
echo Để quản lý dịch vụ:
echo - Mở Services.msc
echo - Tìm "SteamCmdWebAPI Service"
echo.
echo ================================================

pause 