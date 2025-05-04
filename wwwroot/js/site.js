// Cấu hình toastr
(function() {
    // Kiểm tra nếu toastr đã được tải
    if (typeof toastr !== 'undefined') {
        toastr.options = {
            "closeButton": true,
            "debug": false,
            "newestOnTop": true,
            "progressBar": true,
            "positionClass": "toast-bottom-right",
            "preventDuplicates": false,
            "showDuration": "300",
            "hideDuration": "1000",
            "timeOut": "5000",
            "extendedTimeOut": "1000",
            "showEasing": "swing",
            "hideEasing": "linear",
            "showMethod": "fadeIn",
            "hideMethod": "fadeOut"
        };
        console.log("Toastr configured successfully");
    } else {
        console.warn("Toastr not available. Notifications may not display correctly.");
    }
})();

// Sửa lỗi cookie và xác thực
(function () {
    // Kiểm tra và xóa cookie bị lỗi
    function cleanupBrokenCookies() {
        const cookies = document.cookie.split(';');
        for (let i = 0; i < cookies.length; i++) {
            const cookie = cookies[i].trim();
            // Tìm cookie .AspNetCore.Cookies bị lỗi
            if (cookie.startsWith('.AspNetCore.Cookies=') && cookie.includes('error')) {
                document.cookie = '.AspNetCore.Cookies=; expires=Thu, 01 Jan 1970 00:00:00 UTC; path=/;';
                console.log('Đã xóa cookie xác thực bị lỗi');
                // Kiểm tra nếu đang ở trang cần xác thực, chuyển hướng về trang đăng nhập
                if (!window.location.pathname.includes('/login') &&
                    !window.location.pathname.includes('/register') &&
                    !window.location.pathname.includes('/error')) {
                    window.location.href = '/login';
                }
                break;
            }
        }
    }

    // Kiểm tra trạng thái đăng nhập
    function checkAuthStatus() {
        fetch('/api/auth/check-session')
            .then(response => {
                if (response.status === 401 || response.status === 403) {
                    // Chưa đăng nhập hoặc không có quyền
                    if (!window.location.pathname.includes('/login') &&
                        !window.location.pathname.includes('/register')) {
                        window.location.href = '/login';
                    }
                    return;
                }
                return response.json();
            })
            .then(data => {
                if (data && !data.authenticated) {
                    // Không còn xác thực
                    if (!window.location.pathname.includes('/login') &&
                        !window.location.pathname.includes('/register')) {
                        window.location.href = '/login';
                    }
                }
            })
            .catch(error => {
                console.error('Lỗi kiểm tra xác thực:', error);
                // Mất kết nối đến server - không chuyển hướng
            });
    }

    // Làm sạch cookie khi trang tải
    cleanupBrokenCookies();

    // Kiểm tra trạng thái xác thực định kỳ mỗi 60 giây
    setInterval(checkAuthStatus, 60000);
})();

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

    // Xử lý lỗi Ajax
    function handleAjaxErrors() {
        if (typeof $ !== 'undefined') {
            $(document).ajaxError(function (event, jqXHR, settings, thrownError) {
                console.error('AJAX Error:', thrownError);
                // Không hiển thị alert mà chỉ ghi log
                console.error('Có lỗi xảy ra:', thrownError);
            });
        }
    }

    handleAjaxErrors();

    // Hàm cập nhật trạng thái profile
    window.updateProfileStatus = function (profileId, status) {
        const profileRow = document.querySelector(`tr[data-profile-id="${profileId}"]`);
        if (!profileRow) return;

        const statusBadge = profileRow.querySelector(".badge");
        if (!statusBadge) return;

        if (status === "Running") {
            profileRow.classList.add('table-success');
            statusBadge.classList.remove("bg-secondary");
            statusBadge.classList.add("bg-success");
            statusBadge.textContent = "Running";
        } else if (status === "Stopped") {
            profileRow.classList.remove('table-success');
            statusBadge.classList.remove("bg-success");
            statusBadge.classList.add("bg-secondary");
            statusBadge.textContent = "Stopped";
        }
    };

    // Xử lý các popup alert và error
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
                    // Consider re-rejecting or handling the error differently if needed
                    // return Promise.reject(error);
                });
        };
    })();

    // Hàm hiển thị thông báo
    window.showToast = function (message, type = 'success', duration = 3000) {
        const toastContainer = document.getElementById('toast-container') || createToastContainer();

        const toast = document.createElement('div');
        toast.className = `toast align-items-center text-white bg-${type} border-0`;
        toast.setAttribute('role', 'alert');
        toast.setAttribute('aria-live', 'assertive');
        toast.setAttribute('aria-atomic', 'true');

        toast.innerHTML = `
            <div class="d-flex">
                <div class="toast-body">
                    ${message}
                </div>
                <button type="button" class="btn-close btn-close-white me-2 m-auto" data-bs-dismiss="toast" aria-label="Close"></button>
            </div>
        `;

        toastContainer.appendChild(toast);

        if (typeof bootstrap !== 'undefined' && bootstrap.Toast) {
            const bsToast = new bootstrap.Toast(toast, {
                autohide: true,
                delay: duration
            });
            bsToast.show();
        } else {
            toast.style.opacity = '1';
            setTimeout(() => {
                toast.style.opacity = '0';
                setTimeout(() => {
                    if (toast.parentNode) {
                        toast.parentNode.removeChild(toast);
                    }
                }, 500);
            }, duration);
        }

        return toast;
    };

    function createToastContainer() {
        const container = document.createElement('div');
        container.id = 'toast-container';
        container.className = 'toast-container position-fixed bottom-0 end-0 p-3';
        document.body.appendChild(container);
        return container;
    }

    // Thêm mobile menu toggle nếu chưa có và đang ở mobile
    if (!document.querySelector('.menu-toggler') && window.innerWidth <= 1024) {
        const mobileMenuBtn = document.createElement('button');
        mobileMenuBtn.className = 'menu-toggler';
        mobileMenuBtn.innerHTML = '<span class="bi bi-list"></span>';
        document.body.appendChild(mobileMenuBtn);

        mobileMenuBtn.addEventListener('click', () => {
            const sidebar = document.querySelector('.sidebar');
            if (sidebar) {
                sidebar.classList.toggle('menu-active');
            }
        });
    }

    // Xử lý nút "Chạy tất cả"
    $("#runAllBtn").click(function (event) {
        event.preventDefault();
        var button = $(this);
        button.prop("disabled", true).html('<span class="spinner-border spinner-border-sm" role="status" aria-hidden="true"></span> Đang chạy...');

        var token = $('input[name="__RequestVerificationToken"]').val();
        $.ajax({
            url: "/ProfileManager?handler=RunAll",
            method: "POST",
            headers: {
                "RequestVerificationToken": token
            },
            success: function (response) {
                if (response && response.success) {
                    if (window.steamConsole) {
                        window.steamConsole.addLine("Đã thêm tất cả profile vào hàng đợi thực thi", "success");
                    }
                    // Cập nhật UI tất cả profile thành running
                    $("tr[data-profile-id]").each(function () {
                        var row = $(this);
                        row.addClass("table-success");
                        var statusBadge = row.find('.badge');
                        statusBadge.removeClass('bg-secondary').addClass('bg-success').text('Running');

                        // Chuyển tất cả nút run thành stop
                        var runBtn = row.find('.run-btn');
                        if (runBtn.length > 0) {
                            var profileId = runBtn.data('id');
                            var btnHtml = `<button class="btn btn-danger btn-sm stop-btn" type="button" data-id="${profileId}" title="Dừng"><i class="bi bi-stop-fill"></i> Dừng</button>`;
                            runBtn.replaceWith(btnHtml);
                        }
                    });
                } else {
                    const errorMsg = "Lỗi: " + (response && response.error ? response.error : "Không thể chạy tất cả profile");
                    if (window.steamConsole) {
                        window.steamConsole.addLine(errorMsg, "error");
                    }
                    showToast(errorMsg, 'danger');
                }
            },
            error: function (xhr, status, error) {
                const errorMsg = "Lỗi khi gửi yêu cầu chạy tất cả: " + (xhr.responseText || error);
                if (window.steamConsole) {
                    window.steamConsole.addLine(errorMsg, "error");
                }
                showToast(errorMsg, 'danger');
            },
            complete: function () {
                button.prop("disabled", false).html('<i class="bi bi-play-fill"></i> Chạy tất cả');
            }
        });
    });

    // Xử lý nút "Dừng tất cả"
    $("#stopAllBtn").click(function (event) {
        event.preventDefault();
        var button = $(this);
        button.prop("disabled", true).html('<span class="spinner-border spinner-border-sm" role="status" aria-hidden="true"></span> Đang dừng...');

        var token = $('input[name="__RequestVerificationToken"]').val();
        $.ajax({
            url: "/ProfileManager?handler=StopAll",
            method: "POST",
            headers: {
                "RequestVerificationToken": token
            },
            success: function (response) {
                if (response && response.success) {
                    if (window.steamConsole) {
                        window.steamConsole.addLine("Đã dừng tất cả profile thành công", "success");
                    }
                    // Cập nhật UI mà không reload trang
                    $("tr.table-success").removeClass("table-success");
                    $(".badge.bg-success").removeClass("bg-success").addClass("bg-secondary").text("Stopped");

                    // Chuyển nút stop thành run
                    $(".stop-btn").each(function () {
                        var id = $(this).data("id");
                        var btnHtml = `<button class="btn btn-success btn-sm run-btn" type="button" data-id="${id}" title="Chạy"><i class="bi bi-play-fill"></i> Chạy</button>`;
                        $(this).replaceWith(btnHtml);
                    });
                } else {
                    const errorMsg = "Lỗi: " + (response && response.error ? response.error : "Không thể dừng tất cả profile");
                    if (window.steamConsole) {
                        window.steamConsole.addLine(errorMsg, "error");
                    }
                    showToast(errorMsg, 'danger');
                }
            },
            error: function (xhr, status, error) {
                const errorMsg = "Lỗi khi gửi yêu cầu dừng tất cả: " + (xhr.responseText || error);
                if (window.steamConsole) {
                    window.steamConsole.addLine(errorMsg, "error");
                }
                showToast(errorMsg, 'danger');
            },
            complete: function () {
                button.prop("disabled", false).html('<i class="bi bi-stop-fill"></i> Dừng tất cả');
            }
        });
    });

    // Xử lý sự kiện "Chạy" cho từng profile
    $(document).on('click', '.run-btn', function (event) {
        event.preventDefault();
        var profileId = $(this).data('id');
        var button = $(this);
        var row = button.closest('tr');
        var profileName = row.find('td:nth-child(2)').text(); // Lấy tên từ cột thứ 2

        // Cập nhật UI ngay lập tức
        row.addClass('table-success');
        var statusBadge = row.find('.badge');
        statusBadge.removeClass('bg-secondary').addClass('bg-success').text('Running');

        // Thay đổi nút từ "run" thành "stop" ngay lập tức để tránh double click
        var btnHtml = `<button class="btn btn-danger btn-sm stop-btn" type="button" data-id="${profileId}" title="Dừng"><span class="spinner-border spinner-border-sm" role="status" aria-hidden="true"></span> Dừng</button>`;
        button.replaceWith(btnHtml);

        if (window.steamConsole) {
            window.steamConsole.clear();
            window.steamConsole.setProfileId(profileId);
            window.steamConsole.addLine(`Đang chuẩn bị chạy profile: ${profileName} (ID: ${profileId})`, 'info');
        }

        var token = $('input[name="__RequestVerificationToken"]').val();
        $.ajax({
            url: "/ProfileManager?handler=Run",
            method: "POST",
            data: { profileId: profileId },
            headers: {
                "RequestVerificationToken": token
            },
            success: function (response) {
                // Tìm lại nút stop vừa tạo
                var currentStopBtn = row.find('.stop-btn');
                // Cập nhật nút stop cuối cùng sau khi thành công
                var finalBtnHtml = `<button class="btn btn-danger btn-sm stop-btn" type="button" data-id="${profileId}" title="Dừng"><i class="bi bi-stop-fill"></i> Dừng</button>`;
                currentStopBtn.replaceWith(finalBtnHtml);

                if (response && response.success) {
                    // UI đã được cập nhật ở trên
                    if (window.steamConsole) {
                        window.steamConsole.addLine("Đã bắt đầu chạy profile: " + profileName, "success");
                    }
                    showToast(`Đã bắt đầu chạy profile: ${profileName}`, 'success');
                } else {
                    // Khôi phục UI nếu có lỗi
                    row.removeClass('table-success');
                    statusBadge.removeClass('bg-success').addClass('bg-secondary').text('Stopped');
                    // Thay nút stop lại thành run
                    var runBtnHtml = `<button class="btn btn-success btn-sm run-btn" type="button" data-id="${profileId}" title="Chạy"><i class="bi bi-play-fill"></i> Chạy</button>`;
                    row.find('.stop-btn').replaceWith(runBtnHtml);

                    const errorMsg = "Lỗi: " + (response && response.error ? response.error : "Không thể chạy profile");
                    if (window.steamConsole) {
                        window.steamConsole.addLine(errorMsg, "error");
                    }
                    showToast(errorMsg, 'danger');
                }
            },
            error: function (xhr, status, error) {
                // Khôi phục UI nếu có lỗi AJAX
                row.removeClass('table-success');
                statusBadge.removeClass('bg-success').addClass('bg-secondary').text('Stopped');
                // Thay nút stop lại thành run
                var runBtnHtml = `<button class="btn btn-success btn-sm run-btn" type="button" data-id="${profileId}" title="Chạy"><i class="bi bi-play-fill"></i> Chạy</button>`;
                row.find('.stop-btn').replaceWith(runBtnHtml);

                const errorMsg = "Lỗi khi gửi yêu cầu chạy: " + (xhr.responseText || error);
                if (window.steamConsole) {
                    window.steamConsole.addLine(errorMsg, "error");
                }
                showToast(errorMsg, 'danger');
            }
        });
    });

    // Xử lý sự kiện "Dừng" cho từng profile
    $(document).on('click', '.stop-btn', function (event) {
        event.preventDefault();
        var profileId = $(this).data('id');
        var button = $(this);
        var row = button.closest('tr');
        var profileName = row.find('td:nth-child(2)').text();

        // Cập nhật UI ngay lập tức
        row.removeClass('table-success');
        var statusBadge = row.find('.badge');
        statusBadge.removeClass('bg-success').addClass('bg-secondary').text('Stopped');

        // Thay đổi nút từ "stop" thành "run" ngay lập tức
        var btnHtml = `<button class="btn btn-success btn-sm run-btn" type="button" data-id="${profileId}" title="Chạy"><span class="spinner-border spinner-border-sm" role="status" aria-hidden="true"></span> Chạy</button>`;
        button.replaceWith(btnHtml);

        if (window.steamConsole) {
            window.steamConsole.addLine(`Đang dừng profile: ${profileName} (ID: ${profileId})`, 'warning');
        }

        var token = $('input[name="__RequestVerificationToken"]').val();
        $.ajax({
            url: "/ProfileManager?handler=Stop",
            method: "POST",
            data: { profileId: profileId },
            headers: {
                "RequestVerificationToken": token
            },
            success: function (response) {
                // Tìm lại nút run vừa tạo
                var currentRunBtn = row.find('.run-btn');
                // Cập nhật nút run cuối cùng sau khi thành công
                var finalBtnHtml = `<button class="btn btn-success btn-sm run-btn" type="button" data-id="${profileId}" title="Chạy"><i class="bi bi-play-fill"></i> Chạy</button>`;
                currentRunBtn.replaceWith(finalBtnHtml);

                if (response && response.success) {
                    // UI đã được cập nhật ở trên
                    if (window.steamConsole) {
                        window.steamConsole.addLine("Đã dừng profile: " + profileName, "success");
                    }
                    showToast(`Đã dừng profile: ${profileName}`, 'success');
                } else {
                    // Khôi phục UI nếu có lỗi
                    row.addClass('table-success');
                    statusBadge.removeClass('bg-secondary').addClass('bg-success').text('Running');
                    // Thay nút run lại thành stop
                    var stopBtnHtml = `<button class="btn btn-danger btn-sm stop-btn" type="button" data-id="${profileId}" title="Dừng"><i class="bi bi-stop-fill"></i> Dừng</button>`;
                    row.find('.run-btn').replaceWith(stopBtnHtml);

                    const errorMsg = "Lỗi: " + (response && response.error ? response.error : "Không thể dừng profile");
                    if (window.steamConsole) {
                        window.steamConsole.addLine(errorMsg, "error");
                    }
                    showToast(errorMsg, 'danger');
                }
            },
            error: function (xhr, status, error) {
                // Khôi phục UI nếu có lỗi AJAX
                row.addClass('table-success');
                statusBadge.removeClass('bg-secondary').addClass('bg-success').text('Running');
                // Thay nút run lại thành stop
                var stopBtnHtml = `<button class="btn btn-danger btn-sm stop-btn" type="button" data-id="${profileId}" title="Dừng"><i class="bi bi-stop-fill"></i> Dừng</button>`;
                row.find('.run-btn').replaceWith(stopBtnHtml);

                const errorMsg = "Lỗi khi gửi yêu cầu dừng: " + (xhr.responseText || error);
                if (window.steamConsole) {
                    window.steamConsole.addLine(errorMsg, "error");
                }
                showToast(errorMsg, 'danger');
            }
        });
    });

    // Xử lý nút xóa profile
    $(document).on('click', '.delete-btn', function (event) {
        event.preventDefault();
        var profileId = $(this).data('id');
        var button = $(this);
        var row = button.closest('tr');
        var profileName = row.find('td:nth-child(2)').text();

        // Sử dụng confirm dialog chuẩn của trình duyệt hoặc một thư viện modal đẹp hơn
        if (confirm(`Bạn có chắc chắn muốn xóa profile '${profileName}' không?`)) {
            if (window.steamConsole) {
                window.steamConsole.addLine(`Đang xóa profile: ${profileName} (ID: ${profileId})...`, 'warning');
            }

            var token = $('input[name="__RequestVerificationToken"]').val();
            $.ajax({
                url: "/ProfileManager?handler=Delete",
                method: "POST",
                data: { profileId: profileId },
                headers: {
                    "RequestVerificationToken": token
                },
                success: function (response) {
                    if (response && response.success) {
                        // Xóa row khỏi bảng với hiệu ứng fadeOut
                        row.fadeOut(300, function () {
                            $(this).remove();
                        });

                        if (window.steamConsole) {
                            window.steamConsole.addLine("Đã xóa profile: " + profileName, "success");
                        }
                        showToast(`Đã xóa profile: ${profileName}`, 'success');
                    } else {
                        const errorMsg = "Lỗi: " + (response && response.error ? response.error : "Không thể xóa profile");
                        if (window.steamConsole) {
                            window.steamConsole.addLine(errorMsg, "error");
                        }
                        showToast(errorMsg, 'danger');
                    }
                },
                error: function (xhr, status, error) {
                    const errorMsg = "Lỗi khi gửi yêu cầu xóa: " + (xhr.responseText || error);
                    if (window.steamConsole) {
                        window.steamConsole.addLine(errorMsg, "error");
                    }
                    showToast(errorMsg, 'danger');
                }
            });
        }
    });

    // Thêm sự kiện toggle cho auto-scroll
    $(document).on('click', '#toggleAutoScrollBtn', function () {
        const autoScrollEnabled = $(this).hasClass('btn-outline-secondary');
        $(this).toggleClass('btn-outline-secondary btn-secondary');
        $(this).find('i').toggleClass('bi-arrow-down-circle bi-arrow-down-circle-fill');
        $(this).attr('title', autoScrollEnabled ? 'Bật tự cuộn' : 'Tắt tự cuộn');

        if (window.steamConsole && typeof window.steamConsole.setAutoScroll === 'function') {
            window.steamConsole.setAutoScroll(autoScrollEnabled);
        } else {
            console.warn("steamConsole.setAutoScroll not found");
        }
    });

    // Thêm sự kiện xóa console
    $(document).on('click', '#clearConsoleBtn', function () {
        if (window.steamConsole && typeof window.steamConsole.clear === 'function') {
            window.steamConsole.clear();
        } else {
            console.warn("steamConsole.clear not found");
        }
    });

    // Khởi tạo các tooltip
    if (typeof bootstrap !== 'undefined' && bootstrap.Tooltip) {
        const tooltipTriggerList = [].slice.call(document.querySelectorAll('[data-bs-toggle="tooltip"]'));
        tooltipTriggerList.forEach(function (tooltipTriggerEl) {
            new bootstrap.Tooltip(tooltipTriggerEl);
        });
    }
});

// Function to format bytes
function formatBytes(bytes, decimals = 2) {
    if (!+bytes) return '0 Bytes'; // Sửa lỗi khi bytes là 0 hoặc không phải số

    const k = 1024;
    const dm = decimals < 0 ? 0 : decimals;
    const sizes = ['Bytes', 'KB', 'MB', 'GB', 'TB', 'PB', 'EB', 'ZB', 'YB'];

    const i = Math.floor(Math.log(bytes) / Math.log(k));

    // Đảm bảo i nằm trong phạm vi của sizes array
    const index = Math.min(i, sizes.length - 1);

    return `${parseFloat((bytes / Math.pow(k, index)).toFixed(dm))} ${sizes[index]}`;
}