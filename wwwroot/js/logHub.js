"use strict";

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