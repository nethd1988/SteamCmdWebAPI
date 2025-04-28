"use strict";

// Thiết lập kết nối SignalR
const connection = new signalR.HubConnectionBuilder()
    .withUrl("/logHub")
    .withAutomaticReconnect()
    .build();

// Đăng ký sự kiện nhận log
connection.on("ReceiveLog", function (logData) {
    const logContainer = document.getElementById("logContainer");

    if (logContainer) {
        let message = "";
        let profileName = "";
        let status = "";
        let timestamp = new Date();
        let colorClass = "text-info";
        
        // Xử lý dữ liệu log
        try {
            const logObject = typeof logData === 'string' ? JSON.parse(logData) : logData;
            if (logObject && typeof logObject === 'object') {
                message = logObject.message || "";
                profileName = logObject.profileName || "System";
                status = logObject.status || "Info";
                timestamp = logObject.timestamp ? new Date(logObject.timestamp) : new Date();
                colorClass = logObject.colorClass || "text-info";
            } else {
                message = logData;
            }
        } catch {
            message = logData;
        }

        // Tạo element mới cho log
        const logEntry = document.createElement("div");
        logEntry.className = `log-entry ${colorClass}`;

        // Format thời gian
        const timeStr = timestamp.toLocaleTimeString('vi-VN', {
            hour: '2-digit',
            minute: '2-digit',
            second: '2-digit'
        });

        // Tạo nội dung log với định dạng nhất quán
        const logContent = `[${timeStr}] [${profileName}] [${status}] ${message}`;
        logEntry.textContent = logContent;

        // Thêm vào container
        logContainer.appendChild(logEntry);

        // Giới hạn số lượng log hiển thị để tránh quá tải
        while (logContainer.children.length > 1000) {
            logContainer.removeChild(logContainer.firstChild);
        }

        // Cuộn xuống dưới nếu đang ở cuối
        if (logContainer.scrollTop + logContainer.clientHeight >= logContainer.scrollHeight - 100) {
            logContainer.scrollTop = logContainer.scrollHeight;
        }
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
        }
        logContainer.appendChild(span);
    }
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
    appendLog
};