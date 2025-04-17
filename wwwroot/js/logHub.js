"use strict";

let pendingAuthRequests = new Set();

const connection = new signalR.HubConnectionBuilder()
    .withUrl("/logHub")
    .withAutomaticReconnect()
    .build();

connection.on("ReceiveLog", function (message) {
    const logContainer = document.getElementById("logContainer");

    if (logContainer) {
        appendLog(message);
        logContainer.scrollTop = logContainer.scrollHeight;
    }
});

connection.on("RequestTwoFactorCode", function (profileId) {
    console.log(`Nhận yêu cầu 2FA trực tiếp cho profile ID: ${profileId}`);

    if (!pendingAuthRequests.has(profileId)) {
        pendingAuthRequests.add(profileId);
        showSteamGuardPopup(profileId, function (code) {
            pendingAuthRequests.delete(profileId);
            if (code !== '') {
                connection.invoke("SubmitTwoFactorCode", profileId, code).catch(function (err) {
                    console.error(`Lỗi khi gửi mã 2FA: ${err.toString()}`);
                    appendLog(`Lỗi khi gửi mã 2FA: ${err.toString()}`, "error");
                });
            } else {
                connection.invoke("SubmitTwoFactorCode", profileId, "").catch(function (err) {
                    console.error(`Hủy nhập mã 2FA: ${err.toString()}`);
                    appendLog(`Hủy nhập mã 2FA`, "warning");
                });
            }
        });
    } else {
        console.log(`Đã có yêu cầu 2FA đang chờ cho profile ${profileId}, bỏ qua`);
    }
});

connection.on("CancelTwoFactorRequest", function (profileId) {
    console.log(`Hủy yêu cầu 2FA cho profile ID: ${profileId}`);
    if (pendingAuthRequests.has(profileId)) {
        const overlay = document.getElementById("steam-guard-popup");
        if (overlay) {
            document.body.removeChild(overlay);
        }
        pendingAuthRequests.delete(profileId);
        connection.invoke("SubmitTwoFactorCode", profileId, "").catch(function (err) {
            console.error(`Lỗi khi hủy yêu cầu 2FA: ${err.toString()}`);
            appendLog(`Lỗi khi hủy yêu cầu 2FA: ${err.toString()}`, "error");
        });
    }
});

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
                } else if (line.includes("Steam Guard") || line.includes("2FA") || line.includes("mã xác thực")) {
                    span.style.color = "#FF9900";
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
        } else if (message.includes("Steam Guard") || message.includes("2FA") || message.includes("mã xác thực")) {
            span.style.color = "#FF9900";
            span.style.fontWeight = "bold";
        }
        logContainer.appendChild(span);
    }
}

function showSteamGuardPopup(profileId, callback) {
    if (document.getElementById("steam-guard-popup")) {
        document.getElementById("steam-guard-popup").remove();
    }

    const overlay = document.createElement("div");
    overlay.id = "steam-guard-popup";
    overlay.style.position = "fixed";
    overlay.style.top = "0";
    overlay.style.left = "0";
    overlay.style.width = "100%";
    overlay.style.height = "100%";
    overlay.style.backgroundColor = "rgba(0, 0, 0, 0.7)";
    overlay.style.zIndex = "10000";
    overlay.style.display = "flex";
    overlay.style.justifyContent = "center";
    overlay.style.alignItems = "center";

    const popup = document.createElement("div");
    popup.style.backgroundColor = "#1b2838";
    popup.style.color = "#ffffff";
    popup.style.padding = "20px";
    popup.style.borderRadius = "5px";
    popup.style.width = "400px";
    popup.style.boxShadow = "0 0 20px rgba(0, 0, 0, 0.5)";
    popup.style.fontFamily = "Arial, sans-serif";

    const title = document.createElement("h3");
    title.textContent = "Steam Guard Authentication";
    title.style.color = "#66c0f4";
    title.style.marginTop = "0";

    const message = document.createElement("p");
    message.textContent = `Vui lòng nhập mã xác thực Steam Guard cho profile ID: ${profileId}`;

    const note = document.createElement("p");
    note.textContent = "Mã này đã được gửi đến email hoặc ứng dụng Steam Mobile Authenticator của bạn";
    note.style.fontSize = "12px";
    note.style.color = "#acb2b8";

    const input = document.createElement("input");
    input.type = "text";
    input.style.width = "100%";
    input.style.padding = "10px";
    input.style.marginTop = "10px";
    input.style.marginBottom = "15px";
    input.style.boxSizing = "border-box";
    input.style.backgroundColor = "#2a3f5a";
    input.style.border = "1px solid #4b6b8f";
    input.style.color = "#ffffff";
    input.style.fontSize = "16px";
    input.style.textAlign = "center";
    input.style.letterSpacing = "4px";
    input.maxLength = 5;
    input.placeholder = "XXXXX";

    input.addEventListener("input", function () {
        this.value = this.value.replace(/[^a-zA-Z0-9]/g, "").toUpperCase();
    });

    const buttonContainer = document.createElement("div");
    buttonContainer.style.display = "flex";
    buttonContainer.style.justifyContent = "space-between";

    const cancelButton = document.createElement("button");
    cancelButton.textContent = "Hủy";
    cancelButton.style.padding = "8px 15px";
    cancelButton.style.backgroundColor = "#32404e";
    cancelButton.style.color = "#acb2b8";
    cancelButton.style.border = "none";
    cancelButton.style.borderRadius = "2px";
    cancelButton.style.cursor = "pointer";

    const submitButton = document.createElement("button");
    submitButton.textContent = "Xác nhận";
    submitButton.style.padding = "8px 15px";
    submitButton.style.backgroundColor = "#588a1b";
    submitButton.style.color = "#ffffff";
    submitButton.style.border = "none";
    submitButton.style.borderRadius = "2px";
    submitButton.style.cursor = "pointer";

    const autoCloseTimeout = setTimeout(() => {
        if (document.getElementById("steam-guard-popup")) {
            document.body.removeChild(overlay);
            callback("");
            console.log("2FA popup tự động đóng sau 120 giây không có phản hồi");
        }
    }, 120000);

    cancelButton.addEventListener("click", function () {
        clearTimeout(autoCloseTimeout);
        document.body.removeChild(overlay);
        callback("");
    });

    submitButton.addEventListener("click", function () {
        clearTimeout(autoCloseTimeout);
        const code = input.value.trim();
        document.body.removeChild(overlay);
        callback(code);
    });

    input.addEventListener("keypress", function (e) {
        if (e.key === "Enter") {
            submitButton.click();
        }
    });

    buttonContainer.appendChild(cancelButton);
    buttonContainer.appendChild(submitButton);

    popup.appendChild(title);
    popup.appendChild(message);
    popup.appendChild(input);
    popup.appendChild(note);
    popup.appendChild(buttonContainer);

    overlay.appendChild(popup);
    document.body.appendChild(overlay);

    setTimeout(() => {
        input.focus();
        input.select();
    }, 100);
}

connection.start().catch(function (err) {
    console.error(err.toString());
    const errorElement = document.createElement("div");
    errorElement.className = "alert alert-danger mt-3";
    errorElement.textContent = `Không thể kết nối đến máy chủ: ${err.toString()}`;
    document.body.appendChild(errorElement);
});

function runProfile(profileId) {
    const id = parseInt(profileId, 10);

    if (isNaN(id)) {
        console.error("ID profile không hợp lệ");
        appendLog("ID profile không hợp lệ", "error");
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
            console.error(`Lỗi: ${error}`);
            appendLog(`Lỗi khi chạy profile: ${error}`, "error");
        });
}

window.logHubHelpers = {
    showSteamGuardPopup,
    appendLog,
    pendingAuthRequests
};