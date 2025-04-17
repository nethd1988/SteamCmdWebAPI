document.addEventListener('DOMContentLoaded', function () {
    console.log('Site JS loaded successfully');

    // Chặn các thông báo lỗi mặc định từ trình duyệt
    window.addEventListener('error', function (e) {
        console.error('Lỗi bị chặn:', e.error || e.message);
        e.preventDefault();
        e.stopPropagation();
        return true;
    }, true);

    // Chặn các thông báo unhandled rejection
    window.addEventListener('unhandledrejection', function (e) {
        console.error('Promise rejection bị chặn:', e.reason);
        e.preventDefault();
        return true;
    });

    // Ghi đè XHR để xử lý lỗi
    (function () {
        const originalXHROpen = XMLHttpRequest.prototype.open;
        XMLHttpRequest.prototype.open = function () {
            this.addEventListener('error', function (e) {
                console.error('XHR error bị chặn:', e);
                e.stopPropagation();
            });
            originalXHROpen.apply(this, arguments);
        };
    })();

    // Các chức năng toàn cục
    function handleAjaxErrors() {
        $(document).ajaxError(function (event, jqXHR, settings, thrownError) {
            console.error('AJAX Error:', thrownError);
            // Không hiển thị alert mà chỉ ghi log
            console.error('Có lỗi xảy ra:', thrownError);
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

    // Hàm xử lý các popup alert và error
    (function () {
        // Chặn các dialog cảnh báo từ trình duyệt
        const originalOnError = window.onerror;
        window.onerror = function (message, source, lineno, colno, error) {
            console.error("Lỗi JS bị chặn:", { message, source, lineno, colno, error });
            return true; // Ngăn hiển thị lỗi mặc định
        };

        // Theo dõi và xóa các popup lỗi
        setInterval(function () {
            // Xóa các dialog thông báo từ trình duyệt
            const dialogs = document.querySelectorAll('div[role="alertdialog"], div[role="dialog"]');
            dialogs.forEach(dialog => {
                dialog.remove();
            });

            // Xóa overlay modal
            document.querySelectorAll('.modal-backdrop').forEach(backdrop => {
                backdrop.remove();
            });

            // Loại bỏ class modal-open từ body
            document.body.classList.remove('modal-open');
            document.body.style.overflow = '';
            document.body.style.paddingRight = '';
        }, 200);

        // Ghi đè phương thức fetch để bắt lỗi
        const originalFetch = window.fetch;
        window.fetch = function () {
            return originalFetch.apply(this, arguments)
                .catch(error => {
                    console.error('Fetch error bị chặn:', error);
                    return Promise.reject(error);
                });
        };
    })();
});