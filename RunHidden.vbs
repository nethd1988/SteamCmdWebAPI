Option Explicit

' Script để chạy SteamCmdWebAPI hoàn toàn ẩn, không hiển thị cửa sổ console hoặc logs
' Lưu file này trong cùng thư mục với SteamCmdWebAPI.exe

Dim WshShell, appPath, fso, logPath
Set WshShell = CreateObject("WScript.Shell")
Set fso = CreateObject("Scripting.FileSystemObject")

' Lấy đường dẫn của thư mục chứa script này
appPath = fso.GetParentFolderName(WScript.ScriptFullName) & "\SteamCmdWebAPI.exe"

' Kiểm tra xem file exe có tồn tại không
If Not fso.FileExists(appPath) Then
    MsgBox "Không tìm thấy file SteamCmdWebAPI.exe trong thư mục " & fso.GetParentFolderName(WScript.ScriptFullName), vbExclamation, "Lỗi"
    WScript.Quit
End If

' Tạo thư mục để chứa log file thay vì hiển thị trên console
logPath = fso.GetParentFolderName(WScript.ScriptFullName) & "\logs"
If Not fso.FolderExists(logPath) Then
    fso.CreateFolder(logPath)
End If

' Chạy ứng dụng ở chế độ hoàn toàn ẩn (0 = hidden)
WshShell.Run Chr(34) & appPath & Chr(34), 0, False

' Thông báo đã chạy thành công
MsgBox "Ứng dụng SteamCmdWebAPI đang chạy trong nền!", vbInformation, "Thành công"

Set WshShell = Nothing
Set fso = Nothing 