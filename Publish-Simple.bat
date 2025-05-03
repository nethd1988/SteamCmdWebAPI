@echo off
chcp 65001 > nul
echo ===== Xuất bản SteamCmdWebAPI (Phiên bản tối giản) =====
echo.

REM Đường dẫn dự án và đích
set "PROJECT_DIR=%~dp0"
set "PUBLISH_DIR=%PROJECT_DIR%bin\Release\publish-simple"

REM Tạo thư mục xuất bản nếu chưa tồn tại
if not exist "%PUBLISH_DIR%" mkdir "%PUBLISH_DIR%"

echo 1. Đang xuất bản ứng dụng...
dotnet publish "%PROJECT_DIR%SteamCmdWebAPI.csproj" -c Release -o "%PROJECT_DIR%bin\Release\net9.0\publish" ^
    -p:DebugType=None ^
    -p:DebugSymbols=false

if %errorlevel% neq 0 (
    echo [LỖI] Không thể xuất bản ứng dụng.
    pause
    exit /b 1
)

echo.
echo 2. Đang tạo thư mục với số lượng file tối thiểu...

REM Xóa thư mục đích nếu đã tồn tại
if exist "%PUBLISH_DIR%" rd /s /q "%PUBLISH_DIR%"
mkdir "%PUBLISH_DIR%"

echo.
echo 3. Đang sao chép các file cần thiết...

REM Đường dẫn nguồn
set "SOURCE_DIR=%PROJECT_DIR%bin\Release\net9.0\publish"

REM Danh sách các file cần sao chép - ĐẢM BẢO SẼ SAO CHÉP CÁC FILE DLL CHÍNH
echo - File thực thi và thư viện chính
copy "%SOURCE_DIR%\SteamCmdWebAPI.exe" "%PUBLISH_DIR%\" /Y
copy "%SOURCE_DIR%\SteamCmdWebAPI.dll" "%PUBLISH_DIR%\" /Y
copy "%SOURCE_DIR%\SteamCmdWebAPI.runtimeconfig.json" "%PUBLISH_DIR%\" /Y

echo - Các thư viện phụ thuộc
copy "%SOURCE_DIR%\Newtonsoft.Json.dll" "%PUBLISH_DIR%\" /Y
copy "%SOURCE_DIR%\Serilog.dll" "%PUBLISH_DIR%\" /Y
copy "%SOURCE_DIR%\Serilog.Sinks.Console.dll" "%PUBLISH_DIR%\" /Y
copy "%SOURCE_DIR%\Serilog.Sinks.File.dll" "%PUBLISH_DIR%\" /Y

echo - Các thư viện Windows Service
copy "%SOURCE_DIR%\Microsoft.Extensions.Hosting.dll" "%PUBLISH_DIR%\" /Y
copy "%SOURCE_DIR%\Microsoft.Extensions.Hosting.WindowsServices.dll" "%PUBLISH_DIR%\" /Y
copy "%SOURCE_DIR%\Microsoft.Extensions.Hosting.Abstractions.dll" "%PUBLISH_DIR%\" /Y
copy "%SOURCE_DIR%\Microsoft.Extensions.Logging.dll" "%PUBLISH_DIR%\" /Y
copy "%SOURCE_DIR%\Microsoft.Extensions.Logging.EventLog.dll" "%PUBLISH_DIR%\" /Y
copy "%SOURCE_DIR%\Microsoft.Extensions.Logging.Abstractions.dll" "%PUBLISH_DIR%\" /Y
copy "%SOURCE_DIR%\Microsoft.Extensions.Configuration.dll" "%PUBLISH_DIR%\" /Y
copy "%SOURCE_DIR%\Microsoft.Extensions.Configuration.Abstractions.dll" "%PUBLISH_DIR%\" /Y
copy "%SOURCE_DIR%\Microsoft.Extensions.Configuration.UserSecrets.dll" "%PUBLISH_DIR%\" /Y
copy "%SOURCE_DIR%\Microsoft.Extensions.DependencyInjection.dll" "%PUBLISH_DIR%\" /Y
copy "%SOURCE_DIR%\Microsoft.Extensions.DependencyInjection.Abstractions.dll" "%PUBLISH_DIR%\" /Y
copy "%SOURCE_DIR%\Microsoft.Extensions.FileProviders.Physical.dll" "%PUBLISH_DIR%\" /Y
copy "%SOURCE_DIR%\Microsoft.Extensions.FileProviders.Abstractions.dll" "%PUBLISH_DIR%\" /Y
copy "%SOURCE_DIR%\Microsoft.Extensions.Options.dll" "%PUBLISH_DIR%\" /Y
copy "%SOURCE_DIR%\Microsoft.Extensions.Primitives.dll" "%PUBLISH_DIR%\" /Y
copy "%SOURCE_DIR%\System.ServiceProcess.ServiceController.dll" "%PUBLISH_DIR%\" /Y

echo - Các thư viện Serilog
copy "%SOURCE_DIR%\Serilog.Extensions.Hosting.dll" "%PUBLISH_DIR%\" /Y
copy "%SOURCE_DIR%\Serilog.Extensions.Logging.dll" "%PUBLISH_DIR%\" /Y
copy "%SOURCE_DIR%\Serilog.Extensions.Logging.File.dll" "%PUBLISH_DIR%\" /Y
copy "%SOURCE_DIR%\Serilog.Settings.Configuration.dll" "%PUBLISH_DIR%\" /Y

echo - File cấu hình
if exist "%SOURCE_DIR%\appsettings.json" (
    copy "%SOURCE_DIR%\appsettings.json" "%PUBLISH_DIR%\" /Y
)

echo - Thư mục data (nếu có)
if exist "%SOURCE_DIR%\data" (
    xcopy "%SOURCE_DIR%\data" "%PUBLISH_DIR%\data\" /E /I /Y > nul
)

echo.
echo 4. Kiểm tra kết quả sao chép...
if not exist "%PUBLISH_DIR%\SteamCmdWebAPI.dll" (
    echo [CẢNH BÁO] Không tìm thấy SteamCmdWebAPI.dll! Đang sao chép lại...
    copy "%SOURCE_DIR%\SteamCmdWebAPI.dll" "%PUBLISH_DIR%\" /Y
)

echo.
echo 5. Đang tạo các file script hỗ trợ...

REM Tạo file RunHidden.vbs
echo Option Explicit > "%PUBLISH_DIR%\RunHidden.vbs"
echo Dim WshShell, appPath, fso >> "%PUBLISH_DIR%\RunHidden.vbs"
echo Set WshShell = CreateObject("WScript.Shell") >> "%PUBLISH_DIR%\RunHidden.vbs"
echo Set fso = CreateObject("Scripting.FileSystemObject") >> "%PUBLISH_DIR%\RunHidden.vbs"
echo appPath = fso.GetParentFolderName(WScript.ScriptFullName) ^& "\SteamCmdWebAPI.exe" >> "%PUBLISH_DIR%\RunHidden.vbs"
echo If Not fso.FileExists(appPath) Then >> "%PUBLISH_DIR%\RunHidden.vbs"
echo     MsgBox "Không tìm thấy file SteamCmdWebAPI.exe", vbExclamation, "Lỗi" >> "%PUBLISH_DIR%\RunHidden.vbs"
echo     WScript.Quit >> "%PUBLISH_DIR%\RunHidden.vbs"
echo End If >> "%PUBLISH_DIR%\RunHidden.vbs"
echo WshShell.Run Chr(34) ^& appPath ^& Chr(34) ^& " --quiet", 0, False >> "%PUBLISH_DIR%\RunHidden.vbs"
echo MsgBox "Ứng dụng đang chạy trong nền!", vbInformation, "Thành công" >> "%PUBLISH_DIR%\RunHidden.vbs"

REM Tạo file Cài đặt Service
echo @echo off > "%PUBLISH_DIR%\Install-Service.bat"
echo chcp 65001 ^> nul >> "%PUBLISH_DIR%\Install-Service.bat"
echo net session ^>nul 2^>^&1 >> "%PUBLISH_DIR%\Install-Service.bat"
echo if %%ERRORLEVEL%% NEQ 0 ( >> "%PUBLISH_DIR%\Install-Service.bat"
echo     echo [LỖI] Bạn cần chạy script này với quyền Administrator. >> "%PUBLISH_DIR%\Install-Service.bat"
echo     pause >> "%PUBLISH_DIR%\Install-Service.bat"
echo     exit /b 1 >> "%PUBLISH_DIR%\Install-Service.bat"
echo ) >> "%PUBLISH_DIR%\Install-Service.bat"
echo sc stop SteamCmdWebAPI ^>nul 2^>^&1 >> "%PUBLISH_DIR%\Install-Service.bat"
echo sc delete SteamCmdWebAPI ^>nul 2^>^&1 >> "%PUBLISH_DIR%\Install-Service.bat"
echo echo Đang cài đặt dịch vụ... >> "%PUBLISH_DIR%\Install-Service.bat"
echo sc create SteamCmdWebAPI binPath= "\"%%~dp0SteamCmdWebAPI.exe\" --quiet" DisplayName= "SteamCmdWebAPI Service" start= auto >> "%PUBLISH_DIR%\Install-Service.bat"
echo sc description SteamCmdWebAPI "SteamCmdWebAPI service - chạy ở chế độ ẩn không hiển thị logs." >> "%PUBLISH_DIR%\Install-Service.bat"
echo echo Đang tạo file log để chẩn đoán lỗi... >> "%PUBLISH_DIR%\Install-Service.bat"
echo echo. ^> "%%~dp0service_debug.log" >> "%PUBLISH_DIR%\Install-Service.bat"
echo echo Thời gian cài đặt: %%date%% %%time%% ^>^> "%%~dp0service_debug.log" >> "%PUBLISH_DIR%\Install-Service.bat"
echo echo Đường dẫn cài đặt: %%~dp0 ^>^> "%%~dp0service_debug.log" >> "%PUBLISH_DIR%\Install-Service.bat"
echo echo Kiểm tra các file cần thiết: ^>^> "%%~dp0service_debug.log" >> "%PUBLISH_DIR%\Install-Service.bat"
echo echo - SteamCmdWebAPI.exe: %%~dp0SteamCmdWebAPI.exe ^>^> "%%~dp0service_debug.log" >> "%PUBLISH_DIR%\Install-Service.bat"
echo if exist "%%~dp0SteamCmdWebAPI.exe" (echo   Tìm thấy) else (echo   [LỖI] Không tìm thấy!) ^>^> "%%~dp0service_debug.log" >> "%PUBLISH_DIR%\Install-Service.bat"
echo echo - SteamCmdWebAPI.dll: %%~dp0SteamCmdWebAPI.dll ^>^> "%%~dp0service_debug.log" >> "%PUBLISH_DIR%\Install-Service.bat" 
echo if exist "%%~dp0SteamCmdWebAPI.dll" (echo   Tìm thấy) else (echo   [LỖI] Không tìm thấy!) ^>^> "%%~dp0service_debug.log" >> "%PUBLISH_DIR%\Install-Service.bat"
echo echo. ^>^> "%%~dp0service_debug.log" >> "%PUBLISH_DIR%\Install-Service.bat"
echo echo Thử khởi động dịch vụ... ^>^> "%%~dp0service_debug.log" >> "%PUBLISH_DIR%\Install-Service.bat"
echo sc start SteamCmdWebAPI ^>^> "%%~dp0service_debug.log" 2^>^&1 >> "%PUBLISH_DIR%\Install-Service.bat"
echo timeout /t 3 ^> nul >> "%PUBLISH_DIR%\Install-Service.bat"
echo echo. ^>^> "%%~dp0service_debug.log" >> "%PUBLISH_DIR%\Install-Service.bat"
echo echo Trạng thái dịch vụ sau khi cài đặt: ^>^> "%%~dp0service_debug.log" >> "%PUBLISH_DIR%\Install-Service.bat"
echo sc query SteamCmdWebAPI ^>^> "%%~dp0service_debug.log" 2^>^&1 >> "%PUBLISH_DIR%\Install-Service.bat"
echo echo. ^>^> "%%~dp0service_debug.log" >> "%PUBLISH_DIR%\Install-Service.bat"
echo echo Thử chạy ứng dụng trực tiếp để kiểm tra lỗi (sẽ tự động tắt sau 5 giây): ^>^> "%%~dp0service_debug.log" >> "%PUBLISH_DIR%\Install-Service.bat"
echo start /b cmd /c "\"%%~dp0SteamCmdWebAPI.exe\" --quiet ^>^> \"%%~dp0direct_run.log\" 2^>^&1 ^& timeout /t 5 ^> nul ^& taskkill /f /im SteamCmdWebAPI.exe ^>nul 2^>^&1" >> "%PUBLISH_DIR%\Install-Service.bat"
echo timeout /t 6 ^> nul >> "%PUBLISH_DIR%\Install-Service.bat"
echo if exist "%%~dp0direct_run.log" type "%%~dp0direct_run.log" ^>^> "%%~dp0service_debug.log" >> "%PUBLISH_DIR%\Install-Service.bat"
echo echo. ^>^> "%%~dp0service_debug.log" >> "%PUBLISH_DIR%\Install-Service.bat"
echo if %%ERRORLEVEL%% NEQ 0 ( >> "%PUBLISH_DIR%\Install-Service.bat"
echo     echo [CẢNH BÁO] Không thể khởi động dịch vụ tự động. >> "%PUBLISH_DIR%\Install-Service.bat"
echo     echo Vui lòng kiểm tra file service_debug.log để biết chi tiết lỗi. >> "%PUBLISH_DIR%\Install-Service.bat"
echo     type "%%~dp0service_debug.log" >> "%PUBLISH_DIR%\Install-Service.bat"
echo ) else ( >> "%PUBLISH_DIR%\Install-Service.bat"
echo     echo Dịch vụ đã được cài đặt và khởi động thành công! >> "%PUBLISH_DIR%\Install-Service.bat"
echo ) >> "%PUBLISH_DIR%\Install-Service.bat"
echo echo Để biết thêm chi tiết, mở file service_debug.log >> "%PUBLISH_DIR%\Install-Service.bat"
echo echo. >> "%PUBLISH_DIR%\Install-Service.bat"
echo pause >> "%PUBLISH_DIR%\Install-Service.bat"

REM Tạo file Gỡ cài đặt Service  
echo @echo off > "%PUBLISH_DIR%\Uninstall-Service.bat"
echo chcp 65001 ^> nul >> "%PUBLISH_DIR%\Uninstall-Service.bat"
echo net session ^>nul 2^>^&1 >> "%PUBLISH_DIR%\Uninstall-Service.bat"
echo if %%ERRORLEVEL%% NEQ 0 ( >> "%PUBLISH_DIR%\Uninstall-Service.bat"
echo     echo [LỖI] Bạn cần chạy script này với quyền Administrator. >> "%PUBLISH_DIR%\Uninstall-Service.bat"
echo     pause >> "%PUBLISH_DIR%\Uninstall-Service.bat"
echo     exit /b 1 >> "%PUBLISH_DIR%\Uninstall-Service.bat"
echo ) >> "%PUBLISH_DIR%\Uninstall-Service.bat"
echo echo Đang dừng và gỡ cài đặt dịch vụ SteamCmdWebAPI... >> "%PUBLISH_DIR%\Uninstall-Service.bat"
echo sc stop SteamCmdWebAPI >> "%PUBLISH_DIR%\Uninstall-Service.bat"
echo sc delete SteamCmdWebAPI >> "%PUBLISH_DIR%\Uninstall-Service.bat"
echo echo Dịch vụ đã được gỡ cài đặt thành công! >> "%PUBLISH_DIR%\Uninstall-Service.bat"
echo pause >> "%PUBLISH_DIR%\Uninstall-Service.bat"

REM Tạo file Service-Control
echo @echo off > "%PUBLISH_DIR%\Service-Control.bat"
echo chcp 65001 ^> nul >> "%PUBLISH_DIR%\Service-Control.bat"
echo net session ^>nul 2^>^&1 >> "%PUBLISH_DIR%\Service-Control.bat"
echo if %%ERRORLEVEL%% NEQ 0 ( >> "%PUBLISH_DIR%\Service-Control.bat"
echo     echo [LỖI] Bạn cần chạy script này với quyền Administrator. >> "%PUBLISH_DIR%\Service-Control.bat"
echo     pause >> "%PUBLISH_DIR%\Service-Control.bat"
echo     exit /b 1 >> "%PUBLISH_DIR%\Service-Control.bat"
echo ) >> "%PUBLISH_DIR%\Service-Control.bat"
echo :menu >> "%PUBLISH_DIR%\Service-Control.bat"
echo cls >> "%PUBLISH_DIR%\Service-Control.bat"
echo echo ==== QUẢN LÝ DỊCH VỤ STEAMCMDWEBAPI ==== >> "%PUBLISH_DIR%\Service-Control.bat"
echo echo. >> "%PUBLISH_DIR%\Service-Control.bat"
echo echo  1. Khởi động dịch vụ >> "%PUBLISH_DIR%\Service-Control.bat"
echo echo  2. Dừng dịch vụ >> "%PUBLISH_DIR%\Service-Control.bat"
echo echo  3. Khởi động lại dịch vụ >> "%PUBLISH_DIR%\Service-Control.bat"
echo echo  4. Kiểm tra trạng thái >> "%PUBLISH_DIR%\Service-Control.bat"
echo echo  5. Thoát >> "%PUBLISH_DIR%\Service-Control.bat"
echo echo. >> "%PUBLISH_DIR%\Service-Control.bat"
echo set /p choice=Chọn một tùy chọn (1-5): >> "%PUBLISH_DIR%\Service-Control.bat"
echo. >> "%PUBLISH_DIR%\Service-Control.bat"
echo if %%choice%% == 1 goto start >> "%PUBLISH_DIR%\Service-Control.bat"
echo if %%choice%% == 2 goto stop >> "%PUBLISH_DIR%\Service-Control.bat"
echo if %%choice%% == 3 goto restart >> "%PUBLISH_DIR%\Service-Control.bat"
echo if %%choice%% == 4 goto status >> "%PUBLISH_DIR%\Service-Control.bat"
echo if %%choice%% == 5 goto end >> "%PUBLISH_DIR%\Service-Control.bat"
echo goto menu >> "%PUBLISH_DIR%\Service-Control.bat"
echo. >> "%PUBLISH_DIR%\Service-Control.bat"
echo :start >> "%PUBLISH_DIR%\Service-Control.bat"
echo echo Đang khởi động dịch vụ... >> "%PUBLISH_DIR%\Service-Control.bat"
echo sc start SteamCmdWebAPI >> "%PUBLISH_DIR%\Service-Control.bat"
echo timeout /t 2 > nul >> "%PUBLISH_DIR%\Service-Control.bat"
echo goto menu >> "%PUBLISH_DIR%\Service-Control.bat"
echo. >> "%PUBLISH_DIR%\Service-Control.bat"
echo :stop >> "%PUBLISH_DIR%\Service-Control.bat"
echo echo Đang dừng dịch vụ... >> "%PUBLISH_DIR%\Service-Control.bat"
echo sc stop SteamCmdWebAPI >> "%PUBLISH_DIR%\Service-Control.bat"
echo timeout /t 2 > nul >> "%PUBLISH_DIR%\Service-Control.bat"
echo goto menu >> "%PUBLISH_DIR%\Service-Control.bat"
echo. >> "%PUBLISH_DIR%\Service-Control.bat"
echo :restart >> "%PUBLISH_DIR%\Service-Control.bat"
echo echo Đang khởi động lại dịch vụ... >> "%PUBLISH_DIR%\Service-Control.bat"
echo sc stop SteamCmdWebAPI >> "%PUBLISH_DIR%\Service-Control.bat"
echo timeout /t 2 > nul >> "%PUBLISH_DIR%\Service-Control.bat"
echo sc start SteamCmdWebAPI >> "%PUBLISH_DIR%\Service-Control.bat"
echo timeout /t 2 > nul >> "%PUBLISH_DIR%\Service-Control.bat"
echo goto menu >> "%PUBLISH_DIR%\Service-Control.bat"
echo. >> "%PUBLISH_DIR%\Service-Control.bat"
echo :status >> "%PUBLISH_DIR%\Service-Control.bat"
echo echo Trạng thái dịch vụ: >> "%PUBLISH_DIR%\Service-Control.bat"
echo sc query SteamCmdWebAPI >> "%PUBLISH_DIR%\Service-Control.bat"
echo echo. >> "%PUBLISH_DIR%\Service-Control.bat"
echo pause >> "%PUBLISH_DIR%\Service-Control.bat"
echo goto menu >> "%PUBLISH_DIR%\Service-Control.bat"
echo. >> "%PUBLISH_DIR%\Service-Control.bat"
echo :end >> "%PUBLISH_DIR%\Service-Control.bat"
echo exit /b 0 >> "%PUBLISH_DIR%\Service-Control.bat"

REM Tạo README.txt
echo SteamCmdWebAPI - Phiên bản tối giản > "%PUBLISH_DIR%\README.txt"
echo ================================= >> "%PUBLISH_DIR%\README.txt"
echo. >> "%PUBLISH_DIR%\README.txt"
echo Đây là phiên bản tối giản của SteamCmdWebAPI, với số lượng file giảm thiểu >> "%PUBLISH_DIR%\README.txt"
echo nhưng vẫn giữ nguyên tính năng và khả năng chạy dịch vụ. >> "%PUBLISH_DIR%\README.txt"
echo. >> "%PUBLISH_DIR%\README.txt"
echo ĐẶC ĐIỂM: >> "%PUBLISH_DIR%\README.txt"
echo - Ít file hơn so với phiên bản thông thường >> "%PUBLISH_DIR%\README.txt"
echo - Đã loại bỏ thông tin debug và ký hiệu >> "%PUBLISH_DIR%\README.txt"
echo - Tối ưu hóa file >> "%PUBLISH_DIR%\README.txt"
echo - Vẫn chạy như dịch vụ Windows một cách ổn định >> "%PUBLISH_DIR%\README.txt"
echo. >> "%PUBLISH_DIR%\README.txt"
echo CÁCH SỬ DỤNG: >> "%PUBLISH_DIR%\README.txt"
echo 1. Cài đặt dịch vụ: Chạy Install-Service.bat với quyền Admin >> "%PUBLISH_DIR%\README.txt"
echo 2. Chạy ẩn (không cài dịch vụ): Chạy RunHidden.vbs >> "%PUBLISH_DIR%\README.txt"
echo 3. Quản lý dịch vụ: Chạy Service-Control.bat với quyền Admin >> "%PUBLISH_DIR%\README.txt"
echo 4. Gỡ cài đặt dịch vụ: Chạy Uninstall-Service.bat với quyền Admin >> "%PUBLISH_DIR%\README.txt"
echo 5. Kiểm tra ứng dụng: Chạy Test-Run.bat để chẩn đoán lỗi >> "%PUBLISH_DIR%\README.txt"
echo. >> "%PUBLISH_DIR%\README.txt"
echo LƯU Ý: >> "%PUBLISH_DIR%\README.txt"
echo - Vẫn yêu cầu có .NET Runtime 9.0 cài đặt trên máy >> "%PUBLISH_DIR%\README.txt"
echo - Nếu gặp lỗi, hãy thử sử dụng bản đầy đủ >> "%PUBLISH_DIR%\README.txt"
echo. >> "%PUBLISH_DIR%\README.txt"
echo KHẮC PHỤC SỰ CỐ: >> "%PUBLISH_DIR%\README.txt"
echo 1. Nếu dịch vụ không thể khởi động: >> "%PUBLISH_DIR%\README.txt"
echo    - Chạy Test-Run.bat để kiểm tra ứng dụng có chạy được trực tiếp không >> "%PUBLISH_DIR%\README.txt"
echo    - Kiểm tra file service_debug.log để xem chi tiết lỗi >> "%PUBLISH_DIR%\README.txt"
echo    - Kiểm tra Event Viewer (Windows Logs > Application) >> "%PUBLISH_DIR%\README.txt"
echo 2. Nếu thiếu file DLL: >> "%PUBLISH_DIR%\README.txt"
echo    - Kiểm tra files_copied.log để xem danh sách file đã được sao chép >> "%PUBLISH_DIR%\README.txt"
echo    - Cài đặt .NET Runtime 9.0 từ trang chủ Microsoft >> "%PUBLISH_DIR%\README.txt"
echo    - Chạy lại Publish-Simple.bat với quyền Admin >> "%PUBLISH_DIR%\README.txt"
echo 3. Lỗi khác: >> "%PUBLISH_DIR%\README.txt"
echo    - Xem chi tiết lỗi trong direct_run.log >> "%PUBLISH_DIR%\README.txt"

REM Tạo script Test-Run.bat
echo @echo off > "%PUBLISH_DIR%\Test-Run.bat"
echo chcp 65001 ^> nul >> "%PUBLISH_DIR%\Test-Run.bat"
echo echo ===== KIỂM TRA TRỰC TIẾP STEAMCMDWEBAPI ===== >> "%PUBLISH_DIR%\Test-Run.bat"
echo echo. >> "%PUBLISH_DIR%\Test-Run.bat"
echo echo Script này sẽ kiểm tra xem ứng dụng có thể khởi động bình thường hay không >> "%PUBLISH_DIR%\Test-Run.bat"
echo echo. >> "%PUBLISH_DIR%\Test-Run.bat"
echo echo 1. Kiểm tra tồn tại của các file chính... >> "%PUBLISH_DIR%\Test-Run.bat"
echo. >> "%PUBLISH_DIR%\Test-Run.bat"
echo if not exist "%~dp0SteamCmdWebAPI.exe" ( >> "%PUBLISH_DIR%\Test-Run.bat"
echo     echo [LỖI] Không tìm thấy file SteamCmdWebAPI.exe! >> "%PUBLISH_DIR%\Test-Run.bat"
echo     goto :error >> "%PUBLISH_DIR%\Test-Run.bat"
echo ) else ( >> "%PUBLISH_DIR%\Test-Run.bat"
echo     echo - SteamCmdWebAPI.exe: OK >> "%PUBLISH_DIR%\Test-Run.bat"
echo ) >> "%PUBLISH_DIR%\Test-Run.bat"
echo. >> "%PUBLISH_DIR%\Test-Run.bat"
echo if not exist "%~dp0SteamCmdWebAPI.dll" ( >> "%PUBLISH_DIR%\Test-Run.bat"
echo     echo [LỖI] Không tìm thấy file SteamCmdWebAPI.dll! >> "%PUBLISH_DIR%\Test-Run.bat"
echo     goto :error >> "%PUBLISH_DIR%\Test-Run.bat"
echo ) else ( >> "%PUBLISH_DIR%\Test-Run.bat"
echo     echo - SteamCmdWebAPI.dll: OK >> "%PUBLISH_DIR%\Test-Run.bat"
echo ) >> "%PUBLISH_DIR%\Test-Run.bat"
echo. >> "%PUBLISH_DIR%\Test-Run.bat"
echo echo 2. Kiểm tra file cấu hình... >> "%PUBLISH_DIR%\Test-Run.bat"
echo if not exist "%~dp0SteamCmdWebAPI.runtimeconfig.json" ( >> "%PUBLISH_DIR%\Test-Run.bat"
echo     echo [CẢNH BÁO] Không tìm thấy file SteamCmdWebAPI.runtimeconfig.json! >> "%PUBLISH_DIR%\Test-Run.bat"
echo ) else ( >> "%PUBLISH_DIR%\Test-Run.bat"
echo     echo - SteamCmdWebAPI.runtimeconfig.json: OK >> "%PUBLISH_DIR%\Test-Run.bat"
echo ) >> "%PUBLISH_DIR%\Test-Run.bat"
echo. >> "%PUBLISH_DIR%\Test-Run.bat"
echo if not exist "%~dp0appsettings.json" ( >> "%PUBLISH_DIR%\Test-Run.bat"
echo     echo [CẢNH BÁO] Không tìm thấy file appsettings.json! >> "%PUBLISH_DIR%\Test-Run.bat"
echo ) else ( >> "%PUBLISH_DIR%\Test-Run.bat"
echo     echo - appsettings.json: OK >> "%PUBLISH_DIR%\Test-Run.bat"
echo ) >> "%PUBLISH_DIR%\Test-Run.bat"
echo. >> "%PUBLISH_DIR%\Test-Run.bat"
echo echo 3. Kiểm tra thư viện Windows Service cần thiết... >> "%PUBLISH_DIR%\Test-Run.bat"
echo if not exist "%~dp0Microsoft.Extensions.Hosting.WindowsServices.dll" ( >> "%PUBLISH_DIR%\Test-Run.bat"
echo     echo [CẢNH BÁO] Không tìm thấy file Microsoft.Extensions.Hosting.WindowsServices.dll! >> "%PUBLISH_DIR%\Test-Run.bat"
echo     echo Dịch vụ có thể sẽ không khởi động được! >> "%PUBLISH_DIR%\Test-Run.bat"
echo ) else ( >> "%PUBLISH_DIR%\Test-Run.bat"
echo     echo - Microsoft.Extensions.Hosting.WindowsServices.dll: OK >> "%PUBLISH_DIR%\Test-Run.bat"
echo ) >> "%PUBLISH_DIR%\Test-Run.bat"
echo. >> "%PUBLISH_DIR%\Test-Run.bat"
echo echo 4. Đang thử chạy ứng dụng trực tiếp... >> "%PUBLISH_DIR%\Test-Run.bat"
echo echo --------------------------------------------------------- >> "%PUBLISH_DIR%\Test-Run.bat"
echo echo Nhấn Ctrl+C để dừng ứng dụng sau khi kiểm tra! >> "%PUBLISH_DIR%\Test-Run.bat"
echo timeout /t 3 ^> nul >> "%PUBLISH_DIR%\Test-Run.bat"
echo. >> "%PUBLISH_DIR%\Test-Run.bat"
echo "%~dp0SteamCmdWebAPI.exe" >> "%PUBLISH_DIR%\Test-Run.bat"
echo goto :eof >> "%PUBLISH_DIR%\Test-Run.bat"
echo. >> "%PUBLISH_DIR%\Test-Run.bat"
echo :error >> "%PUBLISH_DIR%\Test-Run.bat"
echo echo. >> "%PUBLISH_DIR%\Test-Run.bat"
echo echo [LỖI] Không thể khởi động ứng dụng do thiếu file cần thiết! >> "%PUBLISH_DIR%\Test-Run.bat"
echo echo Vui lòng chạy lại Publish-Simple.bat và kiểm tra lỗi. >> "%PUBLISH_DIR%\Test-Run.bat"
echo pause >> "%PUBLISH_DIR%\Test-Run.bat"
echo exit /b 1 >> "%PUBLISH_DIR%\Test-Run.bat"

echo.
echo 6. Liệt kê nội dung thư mục xuất bản...
dir "%PUBLISH_DIR%\*.dll" "%PUBLISH_DIR%\*.exe" "%PUBLISH_DIR%\*.json"

REM Tạo file danh sách tất cả các DLL để debug
echo ===== DANH SÁCH TẤT CẢ CÁC FILE ĐƯỢC SAO CHÉP ===== > "%PUBLISH_DIR%\files_copied.log"
echo Thời gian: %date% %time% >> "%PUBLISH_DIR%\files_copied.log"
echo. >> "%PUBLISH_DIR%\files_copied.log"
echo === FILE THỰC THI === >> "%PUBLISH_DIR%\files_copied.log"
dir /b "%PUBLISH_DIR%\*.exe" >> "%PUBLISH_DIR%\files_copied.log" 2>nul
echo. >> "%PUBLISH_DIR%\files_copied.log"
echo === THƯ VIỆN === >> "%PUBLISH_DIR%\files_copied.log"
dir /b "%PUBLISH_DIR%\*.dll" >> "%PUBLISH_DIR%\files_copied.log" 2>nul
echo. >> "%PUBLISH_DIR%\files_copied.log"
echo === FILE CẤU HÌNH === >> "%PUBLISH_DIR%\files_copied.log"
dir /b "%PUBLISH_DIR%\*.json" >> "%PUBLISH_DIR%\files_copied.log" 2>nul
echo. >> "%PUBLISH_DIR%\files_copied.log"
echo === FILE HỖ TRỢ === >> "%PUBLISH_DIR%\files_copied.log"
dir /b "%PUBLISH_DIR%\*.bat" "%PUBLISH_DIR%\*.vbs" "%PUBLISH_DIR%\*.txt" >> "%PUBLISH_DIR%\files_copied.log" 2>nul
echo. >> "%PUBLISH_DIR%\files_copied.log"
echo === SO SÁNH VỚI THƯ MỤC GỐC === >> "%PUBLISH_DIR%\files_copied.log"
echo Files được sao chép: >> "%PUBLISH_DIR%\files_copied.log"
for %%i in (SteamCmdWebAPI.dll Newtonsoft.Json.dll Serilog.dll Microsoft.Extensions.Hosting.dll Microsoft.Extensions.Hosting.WindowsServices.dll) do (
    if exist "%PUBLISH_DIR%\%%i" (
        echo - %%i: OK >> "%PUBLISH_DIR%\files_copied.log"
    ) else (
        echo - %%i: THIẾU >> "%PUBLISH_DIR%\files_copied.log"
    )
)
echo. >> "%PUBLISH_DIR%\files_copied.log"
echo === CÁC DLL TRONG THƯ MỤC GỐC NHƯNG KHÔNG ĐƯỢC SAO CHÉP === >> "%PUBLISH_DIR%\files_copied.log"
for /f "delims=" %%i in ('dir /b "%SOURCE_DIR%\*.dll" 2^>nul') do (
    if not exist "%PUBLISH_DIR%\%%i" echo - %%i >> "%PUBLISH_DIR%\files_copied.log"
)

echo.
echo 7. So sánh số lượng file...
echo   - Thư mục publish đầy đủ:
for /f %%a in ('dir /a-d /b "%SOURCE_DIR%\" ^| find /c /v ""') do set FULL_COUNT=%%a
echo     %FULL_COUNT% files

echo   - Thư mục publish tối giản:
for /f %%a in ('dir /a-d /b "%PUBLISH_DIR%\" ^| find /c /v ""') do set MIN_COUNT=%%a
echo     %MIN_COUNT% files

echo.
echo HOÀN THÀNH! Ứng dụng đã được xuất bản thành công tới:
echo %PUBLISH_DIR%
echo.

pause 