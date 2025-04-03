"use strict";

// Thiết lập kết nối SignalR
const connection = new signalR.HubConnectionBuilder()
    .withUrl("/logHub")
    .withAutomaticReconnect()
    .build();

// Đăng ký sự kiện nhận log
connection.on("ReceiveLog", function (message) {
    const logElement = document.getElementById("log-container");
    
    // Nếu không tìm thấy phần tử, tạo mới
    if (!logElement) {
        const container = document.createElement("div");
        container.id = "log-container";
        container.className = "log-container bg-dark text-light p-3 rounded mt-3";
        container.style.height = "300px";
        container.style.overflow = "auto";
        container.style.whiteSpace = "pre-wrap";
        container.style.fontFamily = "monospace";
        
        document.body.appendChild(container);
    }
    
    // Thêm log vào container
    const logContainer = document.getElementById("log-container") || container;
    
    // Thêm log mới
    if (message.includes("\n")) {
        const lines = message.split("\n");
        lines.forEach(line => {
            if (line.trim() !== "") {
                const span = document.createElement("div");
                span.textContent = line;
                logContainer.appendChild(span);
            }
        });
    } else {
        const span = document.createElement("div");
        span.textContent = message;
        logContainer.appendChild(span);
    }
    
    // Cuộn xuống dưới
    logContainer.scrollTop = logContainer.scrollHeight;
});

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
