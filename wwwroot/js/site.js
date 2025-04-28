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

    // Các chức năng toàn cục
    function handleAjaxErrors() {
        $(document).ajaxError(function (event, jqXHR, settings, thrownError) {
            console.error('AJAX Error:', thrownError);
            // Không hiển thị alert mà chỉ ghi log
            console.error('Có lỗi xảy ra:', thrownError);
        });
    }

    handleAjaxErrors();

    // Hàm cập nhật trạng thái profile (Giữ lại từ site.js gốc nếu cần thiết cho các phần khác)
    window.updateProfileStatus = function (profileId, status) {
        const profileRow = $(`tr[data-profile-id="${profileId}"]`);
        const statusBadge = profileRow.find(".badge"); // Dùng class .badge từ site.js

        if (status === "Running") {
            profileRow.addClass('table-success');
            statusBadge.removeClass("bg-secondary").addClass("bg-success").text("Running"); // Dùng class bg-* từ site.js
        } else if (status === "Stopped") {
            profileRow.removeClass('table-success');
            statusBadge.removeClass("bg-success").addClass("bg-secondary").text("Stopped"); // Dùng class bg-* từ site.js
        }
        // Thêm các trạng thái khác nếu cần (ví dụ: Error)
    };

    // Hàm xử lý các popup alert và error (Giữ lại từ site.js gốc)
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

    // Hàm hiển thị thông báo (Giữ lại từ site.js gốc)
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

    // --- Bắt đầu phần tích hợp từ 1.txt ---

    // Lấy CSRF token [cite: 1]
    // Cần đảm bảo @Html.AntiForgeryToken() được render đúng trong form hoặc có input chứa token
    var token = $('input[name="__RequestVerificationToken"]').val();
    if (!token) {
        console.error("CSRF token not found!");
        // Thêm một form tạm thời nếu cần thiết và server render nó đúng cách
        // $('body').append('<form id="antiForgeryForm" style="display:none;">@Html.AntiForgeryToken()</form>');
        // token = $('#antiForgeryForm input[name="__RequestVerificationToken"]').val();
    }

    // Xử lý nút "Chạy tất cả" [cite: 1]
    $("#runAllBtn").click(function (event) {
        event.preventDefault();
        var button = $(this);
        button.prop("disabled", true).html('<span class="spinner-border spinner-border-sm" role="status" aria-hidden="true"></span> Đang chạy...');

        $.ajax({
            url: "/ProfileManager?handler=RunAll", // Endpoint từ 1.txt [cite: 1]
            method: "POST",
            headers: {
                "RequestVerificationToken": token // Sử dụng token đã lấy [cite: 2]
            },
            success: function (response) {
                if (response && response.success) {
                    if (window.steamConsole) {
                        window.steamConsole.addLine("Đã thêm tất cả profile vào hàng đợi thực thi", "success"); // [cite: 3]
                    }
                    // Cập nhật UI tất cả profile thành running
                    $("tr[data-profile-id]").each(function () { // Target các row có data-profile-id
                        var row = $(this);
                        row.addClass("table-success"); // Class từ site.js
                        var statusBadge = row.find('.badge'); // Class từ site.js
                        statusBadge.removeClass('bg-secondary').addClass('bg-success').text('Running'); // Classes + text từ site.js [cite: 5]

                        // Chuyển tất cả nút run thành stop
                        var runBtn = row.find('.run-btn');
                        if (runBtn.length > 0) { // [cite: 7]
                            var profileId = runBtn.data('id');
                            // Sử dụng cấu trúc button từ site.js gốc (nếu có) hoặc cấu trúc từ 1.txt
                            var btnHtml = `<button class="btn btn-danger btn-sm stop-btn" type="button" data-id="${profileId}" title="Dừng"><i class="bi bi-stop-fill"></i> Dừng</button>`; // Giả định cấu trúc nút Stop
                            runBtn.replaceWith(btnHtml); // [cite: 8]
                        }
                    }); // [cite: 9]
                } else { // [cite: 10]
                    const errorMsg = "Lỗi: " + (response && response.error ? response.error : "Không thể chạy tất cả profile");
                    if (window.steamConsole) {
                        window.steamConsole.addLine(errorMsg, "error");
                    }
                    showToast(errorMsg, 'danger'); // Sử dụng toast từ site.js
                    // [cite: 11]
                }
            },
            error: function (xhr, status, error) {
                const errorMsg = "Lỗi khi gửi yêu cầu chạy tất cả: " + (xhr.responseText || error);
                if (window.steamConsole) {
                    window.steamConsole.addLine(errorMsg, "error");
                }
                showToast(errorMsg, 'danger');
                // [cite: 12]
            },
            complete: function () {
                button.prop("disabled", false).html('<i class="bi bi-play-fill"></i> Chạy tất cả'); // Khôi phục nút [cite: 12]
                // [cite: 13]
            }
        });
    });

    // Xử lý nút "Dừng tất cả" [cite: 14]
    $("#stopAllBtn").click(function (event) {
        event.preventDefault();
        var button = $(this);
        button.prop("disabled", true).html('<span class="spinner-border spinner-border-sm" role="status" aria-hidden="true"></span> Đang dừng...');

        $.ajax({
            url: "/ProfileManager?handler=StopAll", // Endpoint từ 1.txt [cite: 14]
            method: "POST",
            headers: {
                "RequestVerificationToken": token // Sử dụng token đã lấy [cite: 14]
            }, // [cite: 15]
            success: function (response) {
                if (response && response.success) {
                    if (window.steamConsole) {
                        window.steamConsole.addLine("Đã dừng tất cả profile thành công", "success"); // [cite: 15]
                    } // [cite: 16]
                    // Cập nhật UI mà không reload trang
                    $("tr.table-success").removeClass("table-success"); // Class từ site.js
                    $(".badge.bg-success").removeClass("bg-success").addClass("bg-secondary").text("Stopped"); // Classes + text từ site.js [cite: 17]

                    // Chuyển nút stop thành run
                    $(".stop-btn").each(function () { // Target các nút stop hiện có
                        var id = $(this).data("id");
                        // Sử dụng cấu trúc button từ site.js gốc hoặc cấu trúc từ 1.txt
                        var btnHtml = `<button class="btn btn-success btn-sm run-btn" type="button" data-id="${id}" title="Chạy"><i class="bi bi-play-fill"></i> Chạy</button>`; // Giả định cấu trúc nút Run
                        $(this).replaceWith(btnHtml); // [cite: 18]
                    });
                } else { // [cite: 19]
                    const errorMsg = "Lỗi: " + (response && response.error ? response.error : "Không thể dừng tất cả profile");
                    if (window.steamConsole) {
                        window.steamConsole.addLine(errorMsg, "error");
                    }
                    showToast(errorMsg, 'danger');
                    // [cite: 20]
                }
            },
            error: function (xhr, status, error) {
                const errorMsg = "Lỗi khi gửi yêu cầu dừng tất cả: " + (xhr.responseText || error);
                if (window.steamConsole) {
                    window.steamConsole.addLine(errorMsg, "error");
                }
                showToast(errorMsg, 'danger');
                // [cite: 21]
            },
            complete: function () {
                button.prop("disabled", false).html('<i class="bi bi-stop-fill"></i> Dừng tất cả'); // Khôi phục nút [cite: 21]
                // [cite: 22]
            }
        });
    });

    // Xử lý sự kiện "Chạy" cho từng profile [cite: 23]
    $(document).on('click', '.run-btn', function (event) {
        event.preventDefault();
        var profileId = $(this).data('id');
        var button = $(this);
        var row = button.closest('tr');
        var profileName = row.find('td:nth-child(2)').text(); // Lấy tên từ cột thứ 2

        // Cập nhật UI ngay lập tức
        row.addClass('table-success'); // Class từ site.js
        var statusBadge = row.find('.badge'); // Class từ site.js
        statusBadge.removeClass('bg-secondary').addClass('bg-success').text('Running'); // Classes + text từ site.js [cite: 27]

        // Thay đổi nút từ "run" thành "stop" ngay lập tức để tránh double click
        var btnHtml = `<button class="btn btn-danger btn-sm stop-btn" type="button" data-id="${profileId}" title="Dừng"><span class="spinner-border spinner-border-sm" role="status" aria-hidden="true"></span> Dừng</button>`; // Hiển thị tạm loading
        button.replaceWith(btnHtml);

        if (window.steamConsole) {
            window.steamConsole.clear();
            window.steamConsole.setProfileId(profileId); // Cập nhật profile ID cho console nếu cần
            window.steamConsole.addLine(`Đang chuẩn bị chạy profile: ${profileName} (ID: ${profileId})`, 'info'); // [cite: 24]
        }

        // Cập nhật badge console (nếu hàm tồn tại từ site.js gốc)
        if (typeof updateActiveProfileBadge === 'function') {
            updateActiveProfileBadge(profileId, profileName);
        }

        $("#steamcmd-console").attr("data-profile-id", profileId); // Cập nhật data attribute console nếu cần


        $.ajax({
            url: "/ProfileManager?handler=Run", // Endpoint từ 1.txt [cite: 24]
            method: "POST", // [cite: 25]
            data: { profileId: profileId },
            headers: {
                "RequestVerificationToken": token // Sử dụng token đã lấy [cite: 25]
            },
            success: function (response) {
                // Tìm lại nút stop vừa tạo
                var currentStopBtn = row.find('.stop-btn');
                // Cập nhật nút stop cuối cùng sau khi thành công
                var finalBtnHtml = `<button class="btn btn-danger btn-sm stop-btn" type="button" data-id="${profileId}" title="Dừng"><i class="bi bi-stop-fill"></i> Dừng</button>`;
                currentStopBtn.replaceWith(finalBtnHtml);

                if (response && response.success) { // [cite: 26]
                    // UI đã được cập nhật ở trên
                    if (window.steamConsole) {
                        window.steamConsole.addLine("Đã bắt đầu chạy profile: " + profileName, "success"); // [cite: 29]
                    } // [cite: 30]
                    showToast(`Đã bắt đầu chạy profile: ${profileName}`, 'success');
                } else { // [cite: 30]
                    // Khôi phục UI nếu có lỗi
                    row.removeClass('table-success');
                    statusBadge.removeClass('bg-success').addClass('bg-secondary').text('Stopped');
                    // Thay nút stop lại thành run
                    var runBtnHtml = `<button class="btn btn-success btn-sm run-btn" type="button" data-id="${profileId}" title="Chạy"><i class="bi bi-play-fill"></i> Chạy</button>`;
                    row.find('.stop-btn').replaceWith(runBtnHtml); // Thay thế nút stop lỗi bằng nút run

                    const errorMsg = "Lỗi: " + (response && response.error ? response.error : "Không thể chạy profile");
                    if (window.steamConsole) {
                        window.steamConsole.addLine(errorMsg, "error");
                    }
                    showToast(errorMsg, 'danger');
                    // [cite: 31, 32]
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
                // [cite: 33, 34]
            }
        }); // [cite: 35]
    });

    // Xử lý sự kiện "Dừng" cho từng profile [cite: 35]
    $(document).on('click', '.stop-btn', function (event) {
        event.preventDefault();
        var profileId = $(this).data('id');
        var button = $(this);
        var row = button.closest('tr');
        var profileName = row.find('td:nth-child(2)').text();

        // Cập nhật UI ngay lập tức
        row.removeClass('table-success'); // Class từ site.js
        var statusBadge = row.find('.badge'); // Class từ site.js
        statusBadge.removeClass('bg-success').addClass('bg-secondary').text('Stopped'); // Classes + text từ site.js [cite: 39]

        // Thay đổi nút từ "stop" thành "run" ngay lập tức
        var btnHtml = `<button class="btn btn-success btn-sm run-btn" type="button" data-id="${profileId}" title="Chạy"><span class="spinner-border spinner-border-sm" role="status" aria-hidden="true"></span> Chạy</button>`; // Hiển thị tạm loading
        button.replaceWith(btnHtml);

        if (window.steamConsole) {
            window.steamConsole.addLine(`Đang dừng profile: ${profileName} (ID: ${profileId})`, 'warning'); // [cite: 36]
        }

        // Cập nhật badge console (nếu hàm tồn tại từ site.js gốc)
        if (typeof updateActiveProfileBadge === 'function') {
            updateActiveProfileBadge(null); // Không có profile nào active
        }

        $.ajax({
            url: "/ProfileManager?handler=Stop", // Endpoint từ 1.txt [cite: 36]
            method: "POST",
            data: { profileId: profileId },
            headers: {
                "RequestVerificationToken": token // Sử dụng token đã lấy [cite: 37]
            },
            success: function (response) {
                // Tìm lại nút run vừa tạo
                var currentRunBtn = row.find('.run-btn');
                // Cập nhật nút run cuối cùng sau khi thành công
                var finalBtnHtml = `<button class="btn btn-success btn-sm run-btn" type="button" data-id="${profileId}" title="Chạy"><i class="bi bi-play-fill"></i> Chạy</button>`; // [cite: 40]
                currentRunBtn.replaceWith(finalBtnHtml); // [cite: 41]

                if (response && response.success) { // [cite: 38]
                    // UI đã được cập nhật ở trên
                    if (window.steamConsole) {
                        window.steamConsole.addLine("Đã dừng profile: " + profileName, "success"); // [cite: 41]
                    } // [cite: 42]
                    showToast(`Đã dừng profile: ${profileName}`, 'success');
                } else { // [cite: 42]
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
                    // [cite: 43, 44]
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
                // [cite: 45, 46]
            }
        }); // [cite: 47]
    });

    // Xử lý nút xóa profile [cite: 47]
    $(document).on('click', '.delete-btn', function (event) {
        event.preventDefault();
        var profileId = $(this).data('id');
        var button = $(this);
        var row = button.closest('tr');
        var profileName = row.find('td:nth-child(2)').text();

        // Sử dụng confirm dialog chuẩn của trình duyệt hoặc một thư viện modal đẹp hơn
        if (confirm(`Bạn có chắc chắn muốn xóa profile '${profileName}' không?`)) {
            if (window.steamConsole) {
                window.steamConsole.addLine(`Đang xóa profile: ${profileName} (ID: ${profileId})...`, 'warning'); // [cite: 48]
            }

            $.ajax({
                url: "/ProfileManager?handler=Delete", // Endpoint từ 1.txt [cite: 48]
                method: "POST",
                data: { profileId: profileId },
                headers: {
                    "RequestVerificationToken": token // Sử dụng token đã lấy [cite: 49]
                },
                success: function (response) {
                    if (response && response.success) { // [cite: 50]
                        // Xóa row khỏi bảng với hiệu ứng fadeOut
                        row.fadeOut(300, function () {
                            $(this).remove(); // [cite: 51]
                        });

                        if (window.steamConsole) {
                            window.steamConsole.addLine("Đã xóa profile: " + profileName, "success"); // [cite: 51]
                        }
                        showToast(`Đã xóa profile: ${profileName}`, 'success');
                        // [cite: 52]
                    } else { // [cite: 52]
                        const errorMsg = "Lỗi: " + (response && response.error ? response.error : "Không thể xóa profile");
                        if (window.steamConsole) {
                            window.steamConsole.addLine(errorMsg, "error");
                        }
                        showToast(errorMsg, 'danger');
                        // [cite: 53]
                    }
                },
                error: function (xhr, status, error) {
                    const errorMsg = "Lỗi khi gửi yêu cầu xóa: " + (xhr.responseText || error);
                    if (window.steamConsole) {
                        window.steamConsole.addLine(errorMsg, "error"); // [cite: 54]
                    }
                    showToast(errorMsg, 'danger');
                    // [cite: 55]
                }
            }); // [cite: 56]
        } // [cite: 56]
    });

    // --- Kết thúc phần tích hợp từ 1.txt ---


    // Thêm sự kiện toggle cho auto-scroll (Giữ lại từ site.js gốc)
    $(document).on('click', '#toggleAutoScrollBtn', function () {
        const autoScrollEnabled = $(this).hasClass('btn-outline-secondary');
        $(this).toggleClass('btn-outline-secondary btn-secondary');
        $(this).find('i').toggleClass('bi-arrow-down-circle bi-arrow-down-circle-fill'); // Toggle icon
        $(this).attr('title', autoScrollEnabled ? 'Bật tự cuộn' : 'Tắt tự cuộn'); // Toggle title

        if (window.steamConsole && typeof window.steamConsole.setAutoScroll === 'function') {
            window.steamConsole.setAutoScroll(autoScrollEnabled);
        } else {
            console.warn("steamConsole.setAutoScroll not found");
        }
    });

    // Thêm sự kiện xóa console (Giữ lại từ site.js gốc)
    $(document).on('click', '#clearConsoleBtn', function () {
        if (window.steamConsole && typeof window.steamConsole.clear === 'function') {
            window.steamConsole.clear();
        } else {
            console.warn("steamConsole.clear not found");
        }
    });

    // Khởi tạo các tooltip (Giữ lại từ site.js gốc)
    if (typeof bootstrap !== 'undefined' && bootstrap.Tooltip) {
        const tooltipTriggerList = [].slice.call(document.querySelectorAll('[data-bs-toggle="tooltip"]'));
        tooltipTriggerList.forEach(function (tooltipTriggerEl) {
            new bootstrap.Tooltip(tooltipTriggerEl);
        });
    }

    // Kiểm tra hiệu suất trang (Giữ lại từ site.js gốc)
    // const perfNow = window.performance.now();
    // console.log("Thời gian xử lý JS sau khi DOM load: " + perfNow + "ms");

}); // Kết thúc document.addEventListener('DOMContentLoaded', ...)

// Function to format bytes (Giữ lại từ site.js gốc)
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