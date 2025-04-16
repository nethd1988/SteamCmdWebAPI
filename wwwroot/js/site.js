// Khởi tạo kết nối SignalR khi trang được tải
document.addEventListener('DOMContentLoaded', function() {
    // Thiết lập kết nối SignalR
    const connection = new signalR.HubConnectionBuilder()
        .withUrl("/logHub")
        .withAutomaticReconnect()
        .build();

    // Đăng ký sự kiện nhận log
    connection.on("ReceiveLog", function (message) {
        const logContainer = document.getElementById("logContainer");
        if (logContainer) {
            const newLine = document.createElement("div");
            newLine.textContent = message;
            logContainer.appendChild(newLine);
            logContainer.scrollTop = logContainer.scrollHeight;
        }
    });

    // Đăng ký sự kiện yêu cầu mã 2FA
    connection.on("RequestTwoFactorCode", function (profileId) {
        console.log("Nhận yêu cầu mã 2FA cho profile ID:", profileId);
        $("#profileId").val(profileId);
        const modal = new bootstrap.Modal(document.getElementById('twoFactorModal'));
        modal.show();
        $("#twoFactorCode").val("").focus();
    });

    // Khởi động kết nối SignalR
    connection.start()
        .then(() => {
            console.log("Kết nối SignalR thành công");
            // Thêm log khi kết nối thành công
            const logContainer = document.getElementById("logContainer");
            if (logContainer) {
                logContainer.innerHTML += "<div>Đã kết nối tới server</div>";
            }
        })
        .catch(err => {
            console.error("Lỗi kết nối SignalR:", err);
            const logContainer = document.getElementById("logContainer");
            if (logContainer) {
                logContainer.innerHTML += "<div style='color:red'>Lỗi kết nối: " + err + "</div>";
            }
        });

    // Phát hiện và xử lý hộp thoại 2FA
    const submitTwoFactorBtn = document.getElementById("submitTwoFactorBtn");
    if (submitTwoFactorBtn) {
        submitTwoFactorBtn.addEventListener("click", function() {
            submitTwoFactorCode();
        });
    }

    // Xử lý khi nhấn Enter trong form 2FA
    const twoFactorCodeInput = document.getElementById("twoFactorCode");
    if (twoFactorCodeInput) {
        twoFactorCodeInput.addEventListener("keypress", function(e) {
            if (e.key === "Enter") {
                e.preventDefault();
                submitTwoFactorCode();
            }
        });
    }

    function submitTwoFactorCode() {
        const profileId = parseInt($("#profileId").val(), 10);
        const twoFactorCode = $("#twoFactorCode").val().trim();
        const token = $('input[name="__RequestVerificationToken"]').val();

        if (!twoFactorCode) {
            alert("Vui lòng nhập mã xác thực Steam Guard!");
            return;
        }

        $("#submitTwoFactorBtn").prop("disabled", true).html('<span class="spinner-border spinner-border-sm" role="status" aria-hidden="true"></span> Đang xử lý...');

        $.ajax({
            url: "/Index?handler=SubmitTwoFactorCode",
            method: "POST",
            data: { profileId: profileId, twoFactorCode: twoFactorCode },
            headers: { "RequestVerificationToken": token },
            success: function (response) {
                if (response.success) {
                    const modal = bootstrap.Modal.getInstance(document.getElementById('twoFactorModal'));
                    modal.hide();
                    $("#twoFactorCode").val("");
                    $("#logContainer").append("<div>Đã gửi mã xác thực Steam Guard</div>");
                    $("#logContainer").scrollTop($("#logContainer")[0].scrollHeight);
                } else {
                    alert("Lỗi: " + response.error);
                }
            },
            error: function (xhr) {
                alert("Lỗi khi gửi mã xác thực: " + (xhr.responseText || "Không thể gửi yêu cầu."));
            },
            complete: function() {
                $("#submitTwoFactorBtn").prop("disabled", false).text("Xác nhận");
            }
        });
    }
});