document.addEventListener('DOMContentLoaded', function () {
    console.log('Site JS loaded successfully');

    // Các chức năng toàn cục
    function handleAjaxErrors() {
        $(document).ajaxError(function (event, jqXHR, settings, thrownError) {
            console.error('AJAX Error:', thrownError);
            alert('Có lỗi xảy ra: ' + thrownError);
        });
    }

    handleAjaxErrors();

    // Khởi tạo biến toàn cục để theo dõi trạng thái 2FA
    window.steamAuth = {
        isWaitingFor2FA: false,
        currentProfileId: 0
    };

    // Hàm kiểm tra và xử lý yêu cầu 2FA từ nội dung text
    window.check2FARequest = function (text, profileId) {
        if (!text) return false;

        text = text.toLowerCase();
        if ((text.includes("steam guard") && text.includes("code")) ||
            text.includes("two-factor") ||
            text.includes("mobile authenticator") ||
            text.includes("enter the current code")) {

            if (!window.steamAuth.isWaitingFor2FA) {
                window.steamAuth.isWaitingFor2FA = true;
                window.steamAuth.currentProfileId = profileId || 1;

                // Nếu showSteamGuardPopup đã được định nghĩa (từ logHub.js)
                if (typeof showSteamGuardPopup === 'function') {
                    showSteamGuardPopup(window.steamAuth.currentProfileId, function (code) {
                        window.steamAuth.isWaitingFor2FA = false;
                        if (code && typeof connection !== 'undefined') {
                            connection.invoke("SubmitTwoFactorCode", parseInt(window.steamAuth.currentProfileId), code);
                        }
                    });
                    return true;
                }
            }
        }
        return false;
    };
});