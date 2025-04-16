"use strict";

// Thiết lập kết nối SignalR
const connection = new signalR.HubConnectionBuilder()
    .withUrl("/logHub")
    .withAutomaticReconnect()
    .build();

// Đăng ký sự kiện nhận log
connection.on("ReceiveLog", function (message) {
    const logContainer = document.getElementById("log-container");
    
    // Nếu không tìm thấy phần tử, tạo mới
    if (!logContainer) {
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
    const logElement = document.getElementById("log-container") || container;
    
    // Thêm log mới
    if (message.includes("\n")) {
        const lines = message.split("\n");
        lines.forEach(line => {
            if (line.trim() !== "") {
                const span = document.createElement("div");
                span.textContent = line;
                logElement.appendChild(span);
            }
        });
    } else {
        const span = document.createElement("div");
        span.textContent = message;
        logElement.appendChild(span);
    }
    
    // Cuộn xuống dưới
    logElement.scrollTop = logElement.scrollHeight;
});

// Đăng ký sự kiện yêu cầu mã 2FA
connection.on("RequestTwoFactorCode", function (profileId) {
    console.log("Nhận yêu cầu mã 2FA cho profile ID:", profileId);
    
    // Tìm hoặc tạo modal 2FA
    let twoFactorModal = document.getElementById('twoFactorModal');
    
    if (!twoFactorModal) {
        // Tạo modal nếu không tồn tại
        twoFactorModal = document.createElement('div');
        twoFactorModal.id = 'twoFactorModal';
        twoFactorModal.className = 'modal fade';
        twoFactorModal.innerHTML = `
            <div class="modal-dialog">
                <div class="modal-content">
                    <div class="modal-header">
                        <h5 class="modal-title">Nhập mã xác thực Steam Guard</h5>
                        <button type="button" class="btn-close" data-bs-dismiss="modal" aria-label="Close"></button>
                    </div>
                    <div class="modal-body">
                        <p>Vui lòng kiểm tra ứng dụng Steam hoặc email của bạn để lấy mã xác thực.</p>
                        <div class="form-group">
                            <label for="twoFactorCodeInput">Mã xác thực:</label>
                            <input type="text" class="form-control" id="twoFactorCodeInput" placeholder="Nhập mã xác thực">
                            <input type="hidden" id="profileIdInput" value="${profileId}">
                        </div>
                    </div>
                    <div class="modal-footer">
                        <button type="button" class="btn btn-secondary" data-bs-dismiss="modal">Hủy</button>
                        <button type="button" class="btn btn-primary" id="submitTwoFactorCodeBtn">Xác nhận</button>
                    </div>
                </div>
            </div>
        `;
        document.body.appendChild(twoFactorModal);
        
        // Thêm xử lý sự kiện cho nút xác nhận
        document.getElementById('submitTwoFactorCodeBtn').addEventListener('click', function() {
            submitTwoFactorCode();
        });
        
        // Thêm xử lý sự kiện cho phím Enter
        document.getElementById('twoFactorCodeInput').addEventListener('keypress', function(e) {
            if (e.key === 'Enter') {
                e.preventDefault();
                submitTwoFactorCode();
            }
        });
   } else {
        // Cập nhật profileId nếu modal đã tồn tại
        document.getElementById('profileIdInput').value = profileId;
    }
    
    // Hiển thị modal
    const modal = new bootstrap.Modal(twoFactorModal);
    modal.show();
    
    // Focus vào ô nhập mã
    document.getElementById('twoFactorCodeInput').focus();
});

// Khởi động kết nối
connection.start().catch(function (err) {
    console.error(err.toString());
    const errorElement = document.createElement("div");
    errorElement.className = "alert alert-danger mt-3";
    errorElement.textContent = "Không thể kết nối đến máy chủ: " + err.toString();
    document.body.appendChild(errorElement);
});

// Hàm gửi mã xác thực 2FA
function submitTwoFactorCode() {
    const profileId = document.getElementById('profileIdInput').value;
    const twoFactorCode = document.getElementById('twoFactorCodeInput').value.trim();
    
    if (!twoFactorCode) {
        alert('Vui lòng nhập mã xác thực!');
        return;
    }
    
    // Gửi mã 2FA thông qua SignalR
    connection.invoke("SubmitTwoFactorCode", parseInt(profileId, 10), twoFactorCode)
        .then(() => {
            console.log('Đã gửi mã 2FA thành công');
            const modal = bootstrap.Modal.getInstance(document.getElementById('twoFactorModal'));
            modal.hide();
        })
        .catch(err => {
            console.error('Lỗi khi gửi mã 2FA:', err);
            alert('Lỗi khi gửi mã xác thực: ' + err);
        });
}

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
    .then(data => {
        if (data.success) {
            console.log("Đã gửi lệnh chạy profile thành công");
        } else {
            console.error("Lỗi:", data.error);
            alert("Lỗi khi chạy profile: " + data.error);
        }
    })
    .catch(error => {
        console.error("Lỗi:", error);
        alert("Lỗi khi chạy profile: " + error);
    });
}

// Hàm dừng profile
function stopProfile(profileId) {
    // Đảm bảo profileId là số
    const id = parseInt(profileId, 10);
    
    if (isNaN(id)) {
        console.error("ID profile không hợp lệ");
        return;
    }
    
    fetch(`/api/Steam/Stop/${id}`, {
        method: "POST"
    })
    .then(response => {
        if (!response.ok) {
            throw new Error("Lỗi khi dừng profile");
        }
        return response.json();
    })
    .then(data => {
        if (data.success) {
            console.log("Đã gửi lệnh dừng profile thành công");
        } else {
            console.error("Lỗi:", data.error);
            alert("Lỗi khi dừng profile: " + data.error);
        }
    })
    .catch(error => {
        console.error("Lỗi:", error);
        alert("Lỗi khi dừng profile: " + error);
    });
}