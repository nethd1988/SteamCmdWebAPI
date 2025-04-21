"use strict";

// Biến toàn cục để theo dõi các yêu cầu 2FA đang xử lý
let pendingAuthRequests = new Set();

// Thiết lập kết nối SignalR
const connection = new signalR.HubConnectionBuilder()
    .withUrl("/logHub")
    .withAutomaticReconnect()
    .build();

// Đăng ký sự kiện nhận log
connection.on("ReceiveLog", function (message) {
    const logContainer = document.getElementById("logContainer");

    if (logContainer) {
        // Thêm log mới
        appendLog(message);

        // Kiểm tra yêu cầu Steam Guard với điều kiện chính xác hơn
        const is2FARequest =
            message.includes("STEAMGUARD_REQUEST_") ||
            message.includes("Steam Guard code:") ||
            message.includes("Two-factor code:") ||
            message.includes("Enter the current code") ||
            message.toLowerCase().includes("mobile authenticator") ||
            message.toLowerCase().includes("email address") ||
            (message.toLowerCase().includes("steam guard") && !message.includes("thành công"));

        if (is2FARequest) {
            // Trích xuất profileId từ message nếu có
            let profileId = 1; // Mặc định là 1
            const profileIdMatch = message.match(/STEAMGUARD_REQUEST_(\d+)/);
            if (profileIdMatch && profileIdMatch[1]) {
                profileId = parseInt(profileIdMatch[1]);
            } else {
                // Nếu không tìm thấy trong message, lấy từ data attribute
                profileId = parseInt(logContainer.getAttribute("data-profile-id") || "1");
            }

            // Tránh hiển thị nhiều popup cùng lúc cho cùng một profile
            if (!pendingAuthRequests.has(profileId)) {
                pendingAuthRequests.add(profileId);
                showSteamGuardPopup(profileId, function (code) {
                    pendingAuthRequests.delete(profileId);
                    if (code !== '') {
                        connection.invoke("SubmitTwoFactorCode", profileId, code).catch(function (err) {
                            console.error("Lỗi khi gửi mã 2FA: " + err.toString());
                            appendLog("Lỗi khi gửi mã 2FA: " + err.toString(), "error");
                        });
                    }
                });
            }
        }

        // Cuộn xuống dưới
        logContainer.scrollTop = logContainer.scrollHeight;
    }
});

// Hàm thêm log vào container
function appendLog(message, type = "info") {
    const logContainer = document.getElementById("logContainer");
    if (!logContainer) return;

    if (message.includes("\n")) {
        const lines = message.split("\n");
        lines.forEach(line => {
            if (line.trim() !== "") {
                const span = document.createElement("div");
                span.textContent = line;
                if (type === "error") {
                    span.style.color = "red";
                } else if (type === "warning") {
                    span.style.color = "yellow";
                } else if (line.includes("Steam Guard") || line.includes("2FA") || line.includes("mã xác thực") ||
                    line.includes("STEAMGUARD_REQUEST_") || line.includes("email")) {
                    span.style.color = "#FF9900"; // Highlight 2FA messages
                    span.style.fontWeight = "bold";
                }
                logContainer.appendChild(span);
            }
        });
    } else {
        const span = document.createElement("div");
        span.textContent = message;
        if (type === "error") {
            span.style.color = "red";
        } else if (type === "warning") {
            span.style.color = "yellow";
        } else if (message.includes("Steam Guard") || message.includes("2FA") || message.includes("mã xác thực") ||
            message.includes("STEAMGUARD_REQUEST_") || message.includes("email")) {
            span.style.color = "#FF9900"; // Highlight 2FA messages
            span.style.fontWeight = "bold";
        }
        logContainer.appendChild(span);
    }
}

// Xử lý yêu cầu mã xác thực hai lớp (2FA)
connection.on("RequestTwoFactorCode", function (profileId) {
    console.log("Nhận yêu cầu 2FA trực tiếp cho profile ID: " + profileId);

    // Tránh nhiều popup cho cùng profile
    if (pendingAuthRequests.has(profileId)) {
        console.log("Đã có popup 2FA cho profile này, bỏ qua");
        return;
    }

    pendingAuthRequests.add(profileId);
    showSteamGuardPopup(profileId, function (code) {
        pendingAuthRequests.delete(profileId);
        if (code !== '') {
            connection.invoke("SubmitTwoFactorCode", profileId, code).catch(function (err) {
                console.error("Lỗi khi gửi mã 2FA: " + err.toString());
                appendLog("Lỗi khi gửi mã 2FA: " + err.toString(), "error");
            });
        } else {
            connection.invoke("SubmitTwoFactorCode", profileId, "").catch(function (err) {
                console.error("Hủy nhập mã 2FA: " + err.toString());
            });
        }
    });
});

// Hàm hiển thị popup Steam Guard 2FA
function showSteamGuardPopup(profileId, callback) {
    // Kiểm tra nếu popup đã tồn tại
    if (document.getElementById('steam-guard-popup')) {
        document.getElementById('steam-guard-popup').remove();
    }

    // Tạo overlay
    const overlay = document.createElement('div');
    overlay.id = 'steam-guard-popup';
    overlay.style.position = 'fixed';
    overlay.style.top = '0';
    overlay.style.left = '0';
    overlay.style.width = '100%';
    overlay.style.height = '100%';
    overlay.style.backgroundColor = 'rgba(0, 0, 0, 0.7)';
    overlay.style.zIndex = '10000';
    overlay.style.display = 'flex';
    overlay.style.justifyContent = 'center';
    overlay.style.alignItems = 'center';

    // Tạo popup container
    const popup = document.createElement('div');
    popup.style.backgroundColor = '#1b2838'; // Steam style dark blue
    popup.style.color = '#ffffff';
    popup.style.padding = '20px';
    popup.style.borderRadius = '5px';
    popup.style.width = '400px';
    popup.style.boxShadow = '0 0 20px rgba(0, 0, 0, 0.5)';
    popup.style.fontFamily = 'Arial, sans-serif';

    // Tạo nội dung
    const title = document.createElement('h3');
    title.textContent = 'Steam Guard Authentication';
    title.style.color = '#66c0f4'; // Steam light blue
    title.style.marginTop = '0';

    const message = document.createElement('p');
    message.textContent = `Vui lòng nhập mã xác thực Steam Guard cho profile ID: ${profileId}`;

    const note = document.createElement('p');
    note.textContent = 'Mã này đã được gửi đến email hoặc ứng dụng Steam Mobile Authenticator của bạn';
    note.style.fontSize = '12px';
    note.style.color = '#acb2b8'; // Steam light gray

    const input = document.createElement('input');
    input.type = 'text';
    input.style.width = '100%';
    input.style.padding = '10px';
    input.style.marginTop = '10px';
    input.style.marginBottom = '15px';
    input.style.boxSizing = 'border-box';
    input.style.backgroundColor = '#2a3f5a'; // Steam input field color
    input.style.border = '1px solid #4b6b8f';
    input.style.color = '#ffffff';
    input.style.fontSize = '16px';
    input.style.textAlign = 'center';
    input.style.letterSpacing = '4px';
    input.maxLength = 5;
    input.placeholder = 'XXXXX';

    input.addEventListener('input', function () {
        // Chỉ cho phép nhập ký tự chữ và số
        this.value = this.value.replace(/[^a-zA-Z0-9]/g, '').toUpperCase();
    });

    // Tạo button container
    const buttonContainer = document.createElement('div');
    buttonContainer.style.display = 'flex';
    buttonContainer.style.justifyContent = 'space-between';

    // Tạo nút hủy
    const cancelButton = document.createElement('button');
    cancelButton.textContent = 'Hủy';
    cancelButton.style.padding = '8px 15px';
    cancelButton.style.backgroundColor = '#32404e';
    cancelButton.style.color = '#acb2b8';
    cancelButton.style.border = 'none';
    cancelButton.style.borderRadius = '2px';
    cancelButton.style.cursor = 'pointer';

    // Tạo nút xác nhận
    const submitButton = document.createElement('button');
    submitButton.textContent = 'Xác nhận';
    submitButton.style.padding = '8px 15px';
    submitButton.style.backgroundColor = '#588a1b'; // Steam green button
    submitButton.style.color = '#ffffff';
    submitButton.style.border = 'none';
    submitButton.style.borderRadius = '2px';
    submitButton.style.cursor = 'pointer';

    // Thêm timeout để tự động đóng popup nếu không có phản hồi
    const autoCloseTimeout = setTimeout(() => {
        if (document.getElementById('steam-guard-popup')) {
            document.body.removeChild(overlay);
            callback('');
            console.log("2FA popup tự động đóng sau 60 giây không có phản hồi");
        }
    }, 60000); // 60 giây

    // Thêm sự kiện click cho nút hủy
    cancelButton.addEventListener('click', function () {
        clearTimeout(autoCloseTimeout);
        document.body.removeChild(overlay);
        callback('');
    });

    // Thêm sự kiện click cho nút xác nhận
    submitButton.addEventListener('click', function () {
        clearTimeout(autoCloseTimeout);
        const code = input.value.trim();
        document.body.removeChild(overlay);
        callback(code);
    });

    // Thêm sự kiện nhấn Enter để submit
    input.addEventListener('keypress', function (e) {
        if (e.key === 'Enter') {
            submitButton.click();
        }
    });

    // Thêm các phần tử vào popup
    buttonContainer.appendChild(cancelButton);
    buttonContainer.appendChild(submitButton);

    popup.appendChild(title);
    popup.appendChild(message);
    popup.appendChild(input);
    popup.appendChild(note);
    popup.appendChild(buttonContainer);

    // Thêm popup vào overlay và overlay vào body
    overlay.appendChild(popup);
    document.body.appendChild(overlay);

    // Phát tiếng beep để thông báo
    try {
        const beep = new Audio("data:audio/wav;base64,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");
        beep.play();
    } catch (e) {
        console.error("Không thể phát âm thanh thông báo", e);
    }

    // Focus vào input field
    setTimeout(() => {
        input.focus();
        input.select();
    }, 100);
}

// Khởi động kết nối
connection.start().catch(function (err) {
    console.error(err.toString());
    const errorElement = document.createElement("div");
    errorElement.className = "alert alert-danger mt-3";
    errorElement.textContent = "Không thể kết nối đến máy chủ: " + err.toString();
    document.body.appendChild(errorElement);
});

// Hàm gửi lệnh chạy profile
function runProfile(profileId) {
    // Đảm bảo profileId là số
    const id = parseInt(profileId, 10);

    if (isNaN(id)) {
        console.error("ID profile không hợp lệ");
        return;
    }

    fetch(`/api/Steam/Run/${id}`, {
        method: "POST"
    })
        .then(response => {
            if (!response.ok) {
                throw new Error("Lỗi khi chạy profile");
            }
            return response.json();
        })
        .catch(error => {
            console.error("Lỗi:", error);
            alert("Lỗi khi chạy profile: " + error);
        });
}

// Export các hàm để sử dụng từ bên ngoài
window.logHubHelpers = {
    showSteamGuardPopup,
    appendLog,
    pendingAuthRequests
};