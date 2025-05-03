@echo off
chcp 65001 > nul
echo ===== Xuất bản SteamCmdWebAPI (Phiên bản tối giản) =====
echo.

REM Kiểm tra dotnet
where dotnet >nul 2>&1
if %errorLevel% neq 0 (
    echo Lỗi: Không tìm thấy .NET SDK!
    echo Hãy cài đặt .NET SDK từ https://dotnet.microsoft.com/download
    pause
    exit /b 1
)

echo Đang kiểm tra thư mục dự án...
set PROJECT_DIR=%~dp0
cd /d "%PROJECT_DIR%"

REM Tạo thư mục xuất bản nếu chưa tồn tại
set PUBLISH_DIR=%PROJECT_DIR%bin\Release\publish-min
if not exist "%PUBLISH_DIR%" mkdir "%PUBLISH_DIR%"

echo.
echo Đang chuẩn bị tệp .runtimeconfig đặc biệt để giảm số lượng file...
echo {> "optimized.runtimeconfig.json"
echo   "runtimeOptions": {>> "optimized.runtimeconfig.json"
echo     "tfm": "net9.0",>> "optimized.runtimeconfig.json"
echo     "includedFrameworks": [>> "optimized.runtimeconfig.json"
echo       {>> "optimized.runtimeconfig.json"
echo         "name": "Microsoft.NETCore.App",>> "optimized.runtimeconfig.json"
echo         "version": "9.0.0">> "optimized.runtimeconfig.json"
echo       }>> "optimized.runtimeconfig.json"
echo     ],>> "optimized.runtimeconfig.json"
echo     "configProperties": {>> "optimized.runtimeconfig.json"
echo       "System.Reflection.Metadata.MetadataUpdater.IsSupported": false,>> "optimized.runtimeconfig.json"
echo       "System.Runtime.TieredCompilation": false,>> "optimized.runtimeconfig.json"
echo       "System.GC.Server": true,>> "optimized.runtimeconfig.json"
echo       "System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported": false,>> "optimized.runtimeconfig.json"
echo       "System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeCompiled": false,>> "optimized.runtimeconfig.json"
echo       "System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization": false>> "optimized.runtimeconfig.json"
echo     }>> "optimized.runtimeconfig.json"
echo   }>> "optimized.runtimeconfig.json"
echo }>> "optimized.runtimeconfig.json"

echo.
echo Xuất bản ứng dụng SteamCmdWebAPI (tối giản)...
dotnet publish -c Release ^
    -p:DebugType=None ^
    -p:DebugSymbols=false ^
    -p:PublishReadyToRun=true ^
    -p:TrimMode=copyused ^
    -p:UseAppHost=true ^
    -p:Deterministic=true ^
    -p:EnableUnsafeBinaryFormatterSerialization=false ^
    -p:EnableUnsafeUTF7Encoding=false ^
    -p:InvariantGlobalization=true ^
    -p:HttpActivityPropagationSupport=false

if %errorLevel% neq 0 (
    echo.
    echo Lỗi: Không thể xuất bản ứng dụng!
    pause
    exit /b 1
)

echo.
echo Đang mã hóa chuỗi, vô hiệu hóa debug và tối giản hóa files...

REM Tạo thư mục tạm
set TEMP_DIR=%PROJECT_DIR%bin\Release\temp
if not exist "%TEMP_DIR%" mkdir "%TEMP_DIR%"

REM Các file thiết yếu cần giữ lại
set REQUIRED_FILES=SteamCmdWebAPI.dll SteamCmdWebAPI.exe SteamCmdWebAPI.runtimeconfig.json Newtonsoft.Json.dll Serilog.dll

REM Sao chép file thiết yếu vào thư mục tạm
for %%F in (%REQUIRED_FILES%) do (
    copy "%PROJECT_DIR%bin\Release\net9.0\publish\%%F" "%TEMP_DIR%\" > nul
)

REM Sao chép tệp cấu hình tùy chỉnh để giảm số lượng file
copy "optimized.runtimeconfig.json" "%TEMP_DIR%\SteamCmdWebAPI.runtimeconfig.json" /Y > nul

REM Tạo thư mục xuất bản tối giản
if exist "%PUBLISH_DIR%" rd /s /q "%PUBLISH_DIR%"
mkdir "%PUBLISH_DIR%"

REM Sao chép từ thư mục tạm vào thư mục xuất bản tối giản
xcopy "%TEMP_DIR%\*" "%PUBLISH_DIR%\" /E /Y > nul

REM Xóa thư mục tạm
rd /s /q "%TEMP_DIR%"

REM Sao chép appsettings.json nếu có
if exist "%PROJECT_DIR%bin\Release\net9.0\publish\appsettings.json" (
    copy "%PROJECT_DIR%bin\Release\net9.0\publish\appsettings.json" "%PUBLISH_DIR%\" /Y > nul
)

REM Sao chép thư mục data nếu có
if exist "%PROJECT_DIR%bin\Release\net9.0\publish\data" (
    xcopy "%PROJECT_DIR%bin\Release\net9.0\publish\data" "%PUBLISH_DIR%\data\" /E /I /Y > nul
)

REM Xóa file tạm
del "optimized.runtimeconfig.json" > nul

echo.
echo Đang tạo các file hỗ trợ...

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
echo sc start SteamCmdWebAPI >> "%PUBLISH_DIR%\Install-Service.bat"
echo if %%ERRORLEVEL%% NEQ 0 ( >> "%PUBLISH_DIR%\Install-Service.bat"
echo     echo [CẢNH BÁO] Không thể khởi động dịch vụ tự động. >> "%PUBLISH_DIR%\Install-Service.bat"
echo     echo Vui lòng kiểm tra Event Viewer để biết chi tiết lỗi. >> "%PUBLISH_DIR%\Install-Service.bat"
echo ) else ( >> "%PUBLISH_DIR%\Install-Service.bat"
echo     echo Dịch vụ đã được cài đặt và khởi động thành công! >> "%PUBLISH_DIR%\Install-Service.bat"
echo ) >> "%PUBLISH_DIR%\Install-Service.bat"
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
echo - Tối ưu hóa cấu hình runtime >> "%PUBLISH_DIR%\README.txt"
echo - Vẫn chạy như dịch vụ Windows một cách ổn định >> "%PUBLISH_DIR%\README.txt"
echo. >> "%PUBLISH_DIR%\README.txt"
echo CÁCH SỬ DỤNG: >> "%PUBLISH_DIR%\README.txt"
echo 1. Cài đặt dịch vụ: Chạy Install-Service.bat với quyền Admin >> "%PUBLISH_DIR%\README.txt"
echo 2. Chạy ẩn (không cài dịch vụ): Chạy RunHidden.vbs >> "%PUBLISH_DIR%\README.txt"
echo 3. Quản lý dịch vụ: Chạy Service-Control.bat với quyền Admin >> "%PUBLISH_DIR%\README.txt"
echo 4. Gỡ cài đặt dịch vụ: Chạy Uninstall-Service.bat với quyền Admin >> "%PUBLISH_DIR%\README.txt"
echo. >> "%PUBLISH_DIR%\README.txt"
echo LƯU Ý: >> "%PUBLISH_DIR%\README.txt"
echo - Vẫn yêu cầu có .NET Runtime cài đặt trên máy >> "%PUBLISH_DIR%\README.txt"
echo - Nếu gặp lỗi, hãy thử sử dụng bản đầy đủ >> "%PUBLISH_DIR%\README.txt"

echo.
echo Đếm số lượng file:
echo - Thư mục publish đầy đủ:
for /f %%a in ('dir /a-d /b "%PROJECT_DIR%bin\Release\net9.0\publish\" ^| find /c /v ""') do set FULL_COUNT=%%a
echo   %FULL_COUNT% files

echo - Thư mục publish tối giản:
for /f %%a in ('dir /a-d /b "%PUBLISH_DIR%\" ^| find /c /v ""') do set MIN_COUNT=%%a
echo   %MIN_COUNT% files

echo.
echo Xuất bản thành công! File đã được tối giản.
echo.
echo Đường dẫn đến file đã xuất bản: %PUBLISH_DIR%
echo.
echo Bạn có thể sao chép thư mục publish-min đến server và chạy Install-Service.bat để cài đặt dịch vụ.
echo.

pause 