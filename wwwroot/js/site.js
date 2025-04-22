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

    // Hàm cập nhật trạng thái profile
    window.updateProfileStatus = function (profileId, status) {
        const profileRow = $(`tr[data-profile-id="${profileId}"]`);
        const statusBadge = profileRow.find(".badge");

        if (status === "Running") {
            profileRow.addClass('table-success');
            statusBadge.removeClass("bg-secondary").addClass("bg-success").text("Running");
        } else if (status === "Stopped") {
            profileRow.removeClass('table-success');
            statusBadge.removeClass("bg-success").addClass("bg-secondary").text("Stopped");
        }
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

        const bsToast = new bootstrap.Toast(toast, {
            autohide: true,
            delay: duration
        });

        bsToast.show();

        toast.addEventListener('hidden.bs.toast', function () {
            toast.remove();
        });

        return toast;
    };

    function createToastContainer() {
        const container = document.createElement('div');
        container.id = 'toast-container';
        container.className = 'toast-container position-fixed bottom-0 end-0 p-3';
        document.body.appendChild(container);
        return container;
    }

    // Hàm xử lý click nút "Dừng"
    $(document).on('click', '.stop-btn', function () {
        const profileId = $(this).data("id");
        const profileRow = $(`tr[data-profile-id="${profileId}"]`);
        const token = $('input[name="__RequestVerificationToken"]').val();

        // Cập nhật UI ngay lập tức
        profileRow.removeClass('table-success');
        profileRow.find(".badge").removeClass("bg-success").addClass("bg-secondary").text("Stopped");

        if (window.steamConsole) {
            window.steamConsole.addLine(`Đang dừng profile ID: ${profileId}...`, 'warning');
        }

        // Cập nhật badge trạng thái ở console
        if (typeof updateActiveProfileBadge === 'function') {
            updateActiveProfileBadge(null);
        }

        $.ajax({
            url: "/Index?handler=Stop",
            method: "POST",
            data: { profileId: profileId },
            headers: {
                "RequestVerificationToken": token
            },
            success: function (response) {
                if (response.success) {
                    if (window.steamConsole) {
                        window.steamConsole.addLine(`Đã dừng profile ID: ${profileId} thành công`, 'success');
                    }
                } else if (response.error) {
                    if (window.steamConsole) {
                        window.steamConsole.addLine("Lỗi: " + response.error, 'error');
                    }
                }
            },
            error: function (xhr, status, error) {
                if (window.steamConsole) {
                    window.steamConsole.addLine("Lỗi khi gửi yêu cầu dừng: " + (xhr.responseText || error), 'error');
                }
            }
        });
    });

    // Hàm xử lý click nút "Dừng tất cả"
    $(document).on('click', '#stopAllBtn', function () {
        const token = $('input[name="__RequestVerificationToken"]').val();

        // Cập nhật UI ngay lập tức
        $("tr.table-success").removeClass('table-success');
        $(".badge.bg-success").removeClass("bg-success").addClass("bg-secondary").text("Stopped");

        if (typeof updateActiveProfileBadge === 'function') {
            updateActiveProfileBadge(null);
        }

        if (window.steamConsole) {
            window.steamConsole.addLine("Đang dừng tất cả các profile...", 'warning');
        }

        $.ajax({
            url: "/Index?handler=StopAll",
            method: "POST",
            headers: {
                "RequestVerificationToken": token
            },
            success: function (response) {
                if (response.success) {
                    if (window.steamConsole) {
                        window.steamConsole.addLine("Đã dừng tất cả các profile thành công", 'success');
                    }
                } else if (response.error) {
                    if (window.steamConsole) {
                        window.steamConsole.addLine("Lỗi: " + response.error, 'error');
                    }
                }
            },
            error: function (xhr, status, error) {
                if (window.steamConsole) {
                    window.steamConsole.addLine("Lỗi khi gửi yêu cầu dừng tất cả: " + (xhr.responseText || error), 'error');
                }
            }
        });
    });

    // Hàm xử lý click nút "Chạy"
    $(document).on('click', '.run-btn', function () {
        const profileId = $(this).data("id");
        const profileName = $(this).closest('tr').find('td:nth-child(2)').text();
        const token = $('input[name="__RequestVerificationToken"]').val();

        // Cập nhật UI ngay lập tức
        $(`tr[data-profile-id="${profileId}"]`).addClass('table-success');
        $(`tr[data-profile-id="${profileId}"]`).find(".badge").removeClass("bg-secondary").addClass("bg-success").text("Running");

        $("#steamcmd-console").attr("data-profile-id", profileId);

        if (typeof updateActiveProfileBadge === 'function') {
            updateActiveProfileBadge(profileId, profileName);
        }

        if (window.steamConsole) {
            window.steamConsole.clear();
            window.steamConsole.setProfileId(profileId);
            window.steamConsole.addLine(`Đang chuẩn bị chạy profile: ${profileName} (ID: ${profileId})`, 'success');
        }

        $.ajax({
            url: "/Index?handler=Run",
            method: "POST",
            data: { profileId: profileId },
            headers: {
                "RequestVerificationToken": token
            },
            success: function (response) {
                if (!response.success && response.error) {
                    if (window.steamConsole) {
                        window.steamConsole.addLine("Lỗi: " + response.error, 'error');
                    }
                    // Khôi phục UI khi có lỗi
                    $(`tr[data-profile-id="${profileId}"]`).removeClass('table-success');
                    $(`tr[data-profile-id="${profileId}"]`).find(".badge").removeClass("bg-success").addClass("bg-secondary").text("Stopped");
                }
            },
            error: function (xhr, status, error) {
                if (window.steamConsole) {
                    window.steamConsole.addLine("Lỗi khi gửi yêu cầu: " + (xhr.responseText || error), 'error');
                }
                // Khôi phục UI khi có lỗi
                $(`tr[data-profile-id="${profileId}"]`).removeClass('table-success');
                $(`tr[data-profile-id="${profileId}"]`).find(".badge").removeClass("bg-success").addClass("bg-secondary").text("Stopped");
            }
        });
    });

    // Hàm xử lý click nút "Chạy tất cả"
    // Xử lý nút "Chạy tất cả"
    $(document).on('click', '#runAllBtn', function () {
        const token = $('input[name="__RequestVerificationToken"]').val();

        if (window.steamConsole) {
            window.steamConsole.clear();
            window.steamConsole.addLine("Đang chuẩn bị chạy tất cả các profile...", 'success');
        }

        $.ajax({
            url: "/Index?handler=RunAll",
            method: "POST",
            headers: {
                "RequestVerificationToken": token
            },
            success: function (response) {
                if (!response.success && response.error) {
                    if (window.steamConsole) {
                        window.steamConsole.addLine("Lỗi: " + response.error, 'error');
                    }
                }
            },
            error: function (xhr, status, error) {
                if (window.steamConsole) {
                    window.steamConsole.addLine("Lỗi khi gửi yêu cầu chạy tất cả: " + (xhr.responseText || error), 'error');
                }
            }
        });
    });

    // Xử lý nút xóa profile
    $(document).on('click', '.delete-btn', function () {
        const profileId = $(this).data("id");
        const token = $('input[name="__RequestVerificationToken"]').val();

        if (confirm("Bạn có chắc chắn muốn xóa cấu hình này?")) {
            if (window.steamConsole) {
                window.steamConsole.addLine(`Đang xóa profile ID: ${profileId}...`, 'warning');
            }

            $.ajax({
                url: "/Index?handler=Delete",
                method: "POST",
                data: { profileId: profileId },
                headers: {
                    "RequestVerificationToken": token
                },
                success: function (response) {
                    if (response.success) {
                        if (window.steamConsole) {
                            window.steamConsole.addLine(`Đã xóa profile ID: ${profileId} thành công`, 'success');
                        }
                        location.reload();
                    } else if (response.error) {
                        if (window.steamConsole) {
                            window.steamConsole.addLine("Lỗi khi xóa: " + response.error, 'error');
                        }
                    }
                },
                error: function (xhr, status, error) {
                    if (window.steamConsole) {
                        window.steamConsole.addLine("Lỗi khi gửi yêu cầu xóa: " + (xhr.responseText || error), 'error');
                    }
                }
            });
        }
    });

    // Thêm sự kiện toggle cho auto-scroll
    $(document).on('click', '#toggleAutoScrollBtn', function () {
        const autoScrollEnabled = $(this).hasClass('btn-outline-secondary');
        $(this).toggleClass('btn-outline-secondary btn-secondary');

        if (window.steamConsole) {
            window.steamConsole.setAutoScroll(autoScrollEnabled);
        }
    });

    // Thêm sự kiện xóa console
    $(document).on('click', '#clearConsoleBtn', function () {
        if (window.steamConsole) {
            window.steamConsole.clear();
        }
    });

    // Khởi tạo các tooltip
    if (typeof bootstrap !== 'undefined' && bootstrap.Tooltip) {
        const tooltipTriggerList = [].slice.call(document.querySelectorAll('[data-bs-toggle="tooltip"]'));
        tooltipTriggerList.forEach(function (tooltipTriggerEl) {
            new bootstrap.Tooltip(tooltipTriggerEl);
        });
    }

    // Kiểm tra hiệu suất trang
    const perfNow = window.performance.now();
    console.log("Thời gian load trang: " + perfNow + "ms");
});

// Function to format bytes to human-readable format
function formatBytes(bytes, decimals = 2) {
    if (bytes === 0) return '0 Bytes';

    const k = 1024;
    const dm = decimals < 0 ? 0 : decimals;
    const sizes = ['Bytes', 'KB', 'MB', 'GB', 'TB', 'PB', 'EB', 'ZB', 'YB'];

    const i = Math.floor(Math.log(bytes) / Math.log(k));

    return parseFloat((bytes / Math.pow(k, i)).toFixed(dm)) + ' ' + sizes[i];
}