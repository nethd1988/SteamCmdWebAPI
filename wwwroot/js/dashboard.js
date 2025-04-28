// Tệp: wwwroot/js/dashboard.js

document.addEventListener("DOMContentLoaded", function () {
    // Lấy các phần tử cần thao tác
    const sidebar = document.querySelector(".sidebar");
    const sidebarToggler = document.querySelector(".sidebar-toggler");
    const menuToggler = document.querySelector(".menu-toggler");

    // Chiều cao sidebar khi đóng/mở trên thiết bị di động
    let collapsedSidebarHeight = "56px"; // Chiều cao ở chế độ mobile khi đóng
    let fullSidebarHeight = "calc(100vh - 32px)"; // Chiều cao khi mở đầy đủ

    // Toggle sidebar's collapsed state khi click vào nút toggle
    if (sidebarToggler) {
        sidebarToggler.addEventListener("click", () => {
            sidebar.classList.toggle("collapsed");
        });
    }

    // Hàm cập nhật chiều cao sidebar và trạng thái toggle menu
    const toggleMenu = (isMenuActive) => {
        if (window.innerWidth <= 1024) {
            sidebar.style.height = isMenuActive ? `${sidebar.scrollHeight}px` : collapsedSidebarHeight;
            if (menuToggler) {
                const menuIcon = menuToggler.querySelector("span");
                if (menuIcon) {
                    menuIcon.textContent = isMenuActive ? "close" : "menu";
                }
            }
        }
    }

    // Toggle menu-active class và điều chỉnh chiều cao
    if (menuToggler) {
        menuToggler.addEventListener("click", () => {
            const isActive = sidebar.classList.toggle("menu-active");
            toggleMenu(isActive);
        });
    }

    // Điều chỉnh sidebar khi resize màn hình
    window.addEventListener("resize", () => {
        if (window.innerWidth >= 1024) {
            sidebar.style.height = fullSidebarHeight;
            sidebar.classList.remove("menu-active");
        } else {
            sidebar.classList.remove("collapsed");
            sidebar.style.height = sidebar.classList.contains("menu-active") ?
                `${sidebar.scrollHeight}px` : collapsedSidebarHeight;
        }
    });

    // Đánh dấu mục đang active trong sidebar
    const currentPath = window.location.pathname;
    const navLinks = document.querySelectorAll('.nav-link');

    navLinks.forEach(link => {
        const href = link.getAttribute('href');
        if (href === '/' && currentPath === '/') {
            link.classList.add('active');
        } else if (href !== '/' && currentPath.startsWith(href)) {
            link.classList.add('active');
        }
    });

    // Khởi tạo Console object nếu chưa tồn tại
    if (document.getElementById('steamcmd-console') && !window.steamConsole) {
        window.steamConsole = {
            addLine: function (message, type = 'info') {
                const consoleElement = document.getElementById('steamcmd-console');
                if (!consoleElement) return;

                const lineElement = document.createElement('div');
                lineElement.classList.add('console-line');

                if (type === 'error') {
                    lineElement.classList.add('error');
                } else if (type === 'warning') {
                    lineElement.classList.add('warning');
                } else if (type === 'success') {
                    lineElement.classList.add('success');
                }

                lineElement.innerText = message;
                consoleElement.appendChild(lineElement);

                this.scrollToBottom();
            },

            clear: function () {
                const consoleElement = document.getElementById('steamcmd-console');
                if (consoleElement) {
                    consoleElement.innerHTML = '';
                }
            },

            scrollToBottom: function () {
                const consoleElement = document.getElementById('steamcmd-console');
                if (consoleElement) {
                    consoleElement.scrollTop = consoleElement.scrollHeight;
                }
            },

            setProfileId: function (id) {
                this.profileId = id;
            },

            setAutoScroll: function (enabled) {
                this.autoScroll = enabled;
                if (enabled) {
                    this.scrollToBottom();
                }
            },

            autoScroll: true,
            profileId: null
        };
    }

    // Xử lý hiển thị Modal
    function showModal(modalId) {
        const modal = document.getElementById(modalId);
        if (!modal) return;

        modal.style.display = 'block';
        modal.classList.add('show');
        document.body.classList.add('modal-open');
    }

    function hideModal(modalId) {
        const modal = document.getElementById(modalId);
        if (!modal) return;

        modal.style.display = 'none';
        modal.classList.remove('show');
        document.body.classList.remove('modal-open');
    }

    // Xử lý đóng Modal với nút close
    document.querySelectorAll('.modal .close-modal, .modal .btn-close').forEach(button => {
        button.addEventListener('click', function () {
            const modal = this.closest('.modal');
            if (modal) {
                hideModal(modal.id);
            }
        });
    });

    // Xử lý khi click ra ngoài Modal
    document.querySelectorAll('.modal').forEach(modal => {
        modal.addEventListener('click', function (e) {
            if (e.target === this) {
                hideModal(this.id);
            }
        });
    });

    // Thêm xử lý cho nút tìm kiếm trong bảng
    const profileSearch = document.getElementById('profileSearch');
    if (profileSearch) {
        profileSearch.addEventListener('keyup', function () {
            const value = this.value.toLowerCase();
            const rows = document.querySelectorAll('#profilesTable tbody tr');

            rows.forEach(row => {
                const text = row.textContent.toLowerCase();
                row.style.display = text.includes(value) ? '' : 'none';
            });
        });
    }

    // Xử lý auto scroll cho console
    const toggleAutoScrollBtn = document.getElementById('toggleAutoScrollBtn');
    if (toggleAutoScrollBtn) {
        toggleAutoScrollBtn.addEventListener('click', function () {
            this.classList.toggle('active');
            if (window.steamConsole) {
                window.steamConsole.setAutoScroll(this.classList.contains('active'));
            }
        });
    }

    // Xử lý clear console
    const clearConsoleBtn = document.getElementById('clearConsoleBtn');
    if (clearConsoleBtn) {
        clearConsoleBtn.addEventListener('click', function () {
            if (window.steamConsole) {
                window.steamConsole.clear();
            }
        });
    }

    // Khởi tạo các tooltip nếu có Bootstrap
    if (typeof bootstrap !== 'undefined' && bootstrap.Tooltip) {
        const tooltipTriggerList = [].slice.call(document.querySelectorAll('[data-bs-toggle="tooltip"]'));
        tooltipTriggerList.forEach(function (tooltipTriggerEl) {
            new bootstrap.Tooltip(tooltipTriggerEl);
        });
    }

    // Hiện các alert thông báo tự động biến mất sau thời gian nhất định
    document.querySelectorAll('.alert:not(.alert-persistent)').forEach(alert => {
        setTimeout(() => {
            if (alert.parentElement) {
                alert.classList.add('fade');
                setTimeout(() => {
                    if (alert.parentElement) {
                        alert.parentElement.removeChild(alert);
                    }
                }, 500);
            }
        }, 5000);
    });

    // Hàm hiển thị thông báo toast
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

        // Sử dụng Bootstrap toast nếu có sẵn, ngược lại tự xử lý
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

    // Kiểm tra hiệu suất trang
    const perfNow = window.performance.now();
    console.log("Thời gian load trang: " + perfNow + "ms");
});