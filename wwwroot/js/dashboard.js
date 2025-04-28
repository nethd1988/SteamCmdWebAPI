// Tệp: wwwroot/js/dashboard.js

document.addEventListener("DOMContentLoaded", function () {
    // Lấy các phần tử cần thao tác
    const sidebar = document.querySelector(".sidebar");
    const sidebarToggler = document.querySelector(".sidebar-toggler");
    const menuToggler = document.querySelector(".menu-toggler");

    // Chiều cao sidebar khi đóng/mở trên thiết bị di động
    let collapsedSidebarHeight = "56px"; // Chiều cao ở chế độ mobile khi đóng [cite: 1]
    let fullSidebarHeight = "100vh"; // Chiều cao khi mở đầy đủ - sửa thành 100vh [cite: 1]

    // Toggle sidebar's collapsed state khi click vào nút toggle
    if (sidebarToggler) {
        sidebarToggler.addEventListener("click", () => {
            sidebar.classList.toggle("collapsed"); // [cite: 2]
        });
    }

    // Hàm cập nhật chiều cao sidebar và trạng thái toggle menu
    const toggleMenu = (isMenuActive) => {
        if (window.innerWidth <= 1024) {
            if (isMenuActive) {
                sidebar.style.height = "100vh"; // Luôn sử dụng 100vh khi mở [cite: 3]
                sidebar.style.overflow = "auto"; // Thêm scroll cho menu dài [cite: 4]
            } else {
                sidebar.style.height = collapsedSidebarHeight; // [cite: 5]
                sidebar.style.overflow = ""; // Xóa scroll khi đóng
            }

            if (menuToggler) {
                const menuIcon = menuToggler.querySelector("span"); // [cite: 5]
                if (menuIcon) { // [cite: 6]
                    menuIcon.textContent = isMenuActive ? "close" : "menu"; // [cite: 6, 7]
                }
            }
        }
    }

    // Hàm đóng menu khi click ra ngoài
    function closeMenuOnClickOutside(event) {
        if (sidebar && sidebar.classList.contains('menu-active')) {
            // Kiểm tra xem click có nằm ngoài sidebar và ngoài nút toggle không
            if (!sidebar.contains(event.target) && !menuToggler.contains(event.target)) {
                sidebar.classList.remove('menu-active'); // [cite: 9]
                toggleMenu(false); // [cite: 10]
                document.removeEventListener('click', closeMenuOnClickOutside); // [cite: 10]
            }
        }
    }

    // Toggle menu-active class và điều chỉnh chiều cao
    if (menuToggler) {
        menuToggler.addEventListener("click", () => {
            const isActive = sidebar.classList.toggle("menu-active"); // [cite: 8]
            toggleMenu(isActive);

            // Thêm/xóa trình lắng nghe sự kiện click bên ngoài [cite: 8]
            if (isActive) {
                document.addEventListener('click', closeMenuOnClickOutside); // [cite: 8]
            } else {
                document.removeEventListener('click', closeMenuOnClickOutside); // [cite: 8]
            }
        });
    }


    // Điều chỉnh sidebar khi resize màn hình
    window.addEventListener("resize", () => {
        if (window.innerWidth >= 1024) {
            sidebar.style.height = fullSidebarHeight; // [cite: 11]
            sidebar.classList.remove("menu-active"); // [cite: 11]
            sidebar.style.overflow = ""; // Đảm bảo không có scroll ở desktop
            document.removeEventListener('click', closeMenuOnClickOutside); // Xóa listener nếu chuyển sang desktop [cite: 11]
        } else {
            sidebar.classList.remove("collapsed"); // Không dùng collapsed ở mobile [cite: 11]
            // Cập nhật chiều cao dựa trên trạng thái menu-active
            sidebar.style.height = sidebar.classList.contains("menu-active") ?
                "100vh" : collapsedSidebarHeight; // [cite: 11]
            sidebar.style.overflow = sidebar.classList.contains("menu-active") ? "auto" : ""; // Thêm/xóa scroll
        }
    });

    // Đánh dấu mục đang active trong sidebar
    const currentPath = window.location.pathname; // [cite: 12]
    const navLinks = document.querySelectorAll('.nav-link'); // [cite: 12]

    navLinks.forEach(link => { // [cite: 13]
        const href = link.getAttribute('href'); // [cite: 13]
        // Xử lý trường hợp trang chủ
        if (href === '/' && (currentPath === '/' || currentPath === '/index.html')) { // Thêm kiểm tra /index.html nếu cần
            link.classList.add('active'); // [cite: 13]
        }
        // Xử lý các trang con, đảm bảo khớp chính xác hơn
        else if (href !== '/' && currentPath.startsWith(href) && (currentPath.length === href.length || currentPath[href.length] === '/')) {
            link.classList.add('active'); // [cite: 13]
        }
    });

    // ------------- Các phần code khác từ dashboard.js gốc -------------

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

                if (this.autoScroll) { // Chỉ cuộn nếu autoScroll đang bật
                    this.scrollToBottom();
                }
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
                // Cập nhật trạng thái nút nếu cần
                const toggleBtn = document.getElementById('toggleAutoScrollBtn');
                if (toggleBtn) {
                    if (enabled) toggleBtn.classList.add('active');
                    else toggleBtn.classList.remove('active');
                }
                if (enabled) { // Cuộn xuống đáy khi bật auto-scroll
                    this.scrollToBottom();
                }
            },

            autoScroll: true, // Mặc định bật auto scroll
            profileId: null
        };
        // Đặt trạng thái ban đầu cho nút toggle auto scroll
        const toggleBtn = document.getElementById('toggleAutoScrollBtn');
        if (toggleBtn && window.steamConsole.autoScroll) {
            toggleBtn.classList.add('active');
        }
    }

    // Xử lý hiển thị Modal
    function showModal(modalId) {
        const modal = document.getElementById(modalId);
        if (!modal) return;

        // Tạo backdrop nếu chưa có
        let backdrop = document.querySelector('.modal-backdrop');
        if (!backdrop) {
            backdrop = document.createElement('div');
            backdrop.className = 'modal-backdrop fade';
            document.body.appendChild(backdrop);
        }

        // Hiển thị backdrop và modal
        document.body.classList.add('modal-open');
        backdrop.classList.add('show');
        modal.style.display = 'block';
        setTimeout(() => modal.classList.add('show'), 10); // Thêm độ trễ nhỏ cho transition
    }

    function hideModal(modalId) {
        const modal = document.getElementById(modalId);
        const backdrop = document.querySelector('.modal-backdrop');
        if (!modal) return;

        modal.classList.remove('show');

        // Chỉ xóa backdrop nếu không còn modal nào đang mở
        setTimeout(() => {
            modal.style.display = 'none';
            const anyModalOpen = document.querySelector('.modal.show');
            if (!anyModalOpen) {
                if (backdrop) {
                    backdrop.classList.remove('show');
                    setTimeout(() => {
                        if (backdrop && !document.querySelector('.modal.show')) { // Kiểm tra lại trước khi xóa
                            backdrop.remove();
                        }
                    }, 150); // Thời gian transition của backdrop
                }
                document.body.classList.remove('modal-open');
            }
        }, 150); // Thời gian transition của modal
    }

    // Xử lý đóng Modal với nút close hoặc data-bs-dismiss="modal"
    document.addEventListener('click', function (event) {
        if (event.target.matches('.modal .close-modal, .modal .btn-close, [data-bs-dismiss="modal"]')) {
            const modal = event.target.closest('.modal');
            if (modal) {
                hideModal(modal.id);
            }
        }
    });


    // Xử lý khi click ra ngoài Modal
    document.querySelectorAll('.modal').forEach(modal => {
        modal.addEventListener('click', function (e) {
            // Chỉ đóng khi click trực tiếp vào modal background (không phải content bên trong)
            if (e.target === this) {
                hideModal(this.id);
            }
        });
    });

    // Thêm xử lý cho nút tìm kiếm trong bảng
    const profileSearch = document.getElementById('profileSearch');
    if (profileSearch) {
        profileSearch.addEventListener('keyup', function () {
            const value = this.value.toLowerCase().trim(); // Thêm trim()
            const rows = document.querySelectorAll('#profilesTable tbody tr');

            rows.forEach(row => {
                const text = row.textContent.toLowerCase();
                row.style.display = text.includes(value) ? '' : 'none';
            });
        });
    }

    // Xử lý auto scroll cho console
    const toggleAutoScrollBtn = document.getElementById('toggleAutoScrollBtn');
    if (toggleAutoScrollBtn && window.steamConsole) { // Đảm bảo steamConsole tồn tại
        toggleAutoScrollBtn.addEventListener('click', function () {
            // Không cần toggle class ở đây vì setAutoScroll sẽ làm việc đó
            const newState = !this.classList.contains('active');
            window.steamConsole.setAutoScroll(newState);
        });
    }

    // Xử lý clear console
    const clearConsoleBtn = document.getElementById('clearConsoleBtn');
    if (clearConsoleBtn && window.steamConsole) { // Đảm bảo steamConsole tồn tại
        clearConsoleBtn.addEventListener('click', function () {
            window.steamConsole.clear();
        });
    }

    // Khởi tạo các tooltip nếu có Bootstrap
    if (typeof bootstrap !== 'undefined' && bootstrap.Tooltip) {
        const tooltipTriggerList = [].slice.call(document.querySelectorAll('[data-bs-toggle="tooltip"]'));
        tooltipTriggerList.map(function (tooltipTriggerEl) {
            return new bootstrap.Tooltip(tooltipTriggerEl);
        });
    }

    // Hiện các alert thông báo tự động biến mất sau thời gian nhất định
    document.querySelectorAll('.alert:not(.alert-persistent)').forEach(alert => {
        setTimeout(() => {
            // Kiểm tra xem alert còn tồn tại không trước khi thao tác
            if (alert.parentElement) {
                // Sử dụng bootstrap's dismiss nếu có, ngược lại tự xử lý fade out
                if (typeof bootstrap !== 'undefined' && bootstrap.Alert) {
                    const bsAlert = bootstrap.Alert.getInstance(alert);
                    if (bsAlert) {
                        bsAlert.close();
                    } else {
                        // Fallback nếu không khởi tạo được bs Alert
                        alert.classList.add('fade-out'); // Thêm class để bắt đầu transition
                        setTimeout(() => {
                            if (alert.parentElement) alert.remove();
                        }, 500); // Chờ transition hoàn thành
                    }
                } else {
                    alert.classList.add('fade-out');
                    setTimeout(() => {
                        if (alert.parentElement) alert.remove();
                    }, 500);
                }
            }
        }, 5000); // Thời gian hiển thị alert
    });

    // Thêm CSS cho fade-out nếu không dùng Bootstrap Alert
    if (typeof bootstrap === 'undefined' || !bootstrap.Alert) {
        const style = document.createElement('style');
        style.textContent = `
             .fade-out {
                 opacity: 0;
                 transition: opacity 0.5s ease-out;
             }
         `;
        document.head.appendChild(style);
    }


    // Hàm hiển thị thông báo toast
    window.showToast = function (message, type = 'success', duration = 3000) {
        const toastContainer = document.getElementById('toast-container') || createToastContainer();

        const toast = document.createElement('div');
        // Thêm class 'showing' để trigger animation vào
        toast.className = `toast align-items-center text-white bg-${type} border-0 showing`;
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

        // Sử dụng Bootstrap toast nếu có sẵn
        if (typeof bootstrap !== 'undefined' && bootstrap.Toast) {
            const bsToast = new bootstrap.Toast(toast, {
                autohide: true,
                delay: duration
            });
            // Loại bỏ class 'showing' sau khi animation vào hoàn tất (Bootstrap tự xử lý)
            toast.addEventListener('shown.bs.toast', () => toast.classList.remove('showing'));
            bsToast.show();
            // Xóa element khỏi DOM sau khi ẩn
            toast.addEventListener('hidden.bs.toast', () => {
                if (toast.parentNode) toast.remove();
            });
        } else {
            // Tự xử lý animation và xóa nếu không có Bootstrap
            setTimeout(() => toast.classList.add('show'), 10); // Thêm class 'show' để hiển thị
            setTimeout(() => toast.classList.remove('showing'), 500); // Xóa showing sau animation vào

            const closeButton = toast.querySelector('[data-bs-dismiss="toast"]');
            const hideToast = () => {
                toast.classList.remove('show');
                toast.classList.add('hiding'); // Thêm class để trigger animation ra
                setTimeout(() => {
                    if (toast.parentNode) {
                        toast.remove();
                    }
                }, 500); // Thời gian animation ra
            };

            if (closeButton) closeButton.addEventListener('click', hideToast);

            setTimeout(hideToast, duration);
        }

        return toast; // Trả về toast element
    };

    function createToastContainer() {
        let container = document.getElementById('toast-container');
        if (!container) {
            container = document.createElement('div');
            container.id = 'toast-container';
            // Cập nhật class để sử dụng Bootstrap 5 positioning
            container.className = 'toast-container position-fixed bottom-0 end-0 p-3';
            // Thêm style cho z-index để đảm bảo hiển thị trên các element khác
            container.style.zIndex = "1080"; // z-index cao hơn modal backdrop (1050)
            document.body.appendChild(container);

            // Thêm CSS transitions nếu không dùng Bootstrap Toast
            if (typeof bootstrap === 'undefined' || !bootstrap.Toast) {
                const style = document.createElement('style');
                style.textContent = `
                 .toast-container .toast {
                     opacity: 0;
                     transition: opacity 0.4s ease-in-out, transform 0.4s ease-in-out;
                     transform: translateY(20px);
                 }
                 .toast-container .toast.showing,
                 .toast-container .toast.show {
                     opacity: 1;
                     transform: translateY(0);
                 }
                  .toast-container .toast.hiding {
                      opacity: 0;
                      transform: translateY(20px);
                  }
                 `;
                document.head.appendChild(style);
            }
        }
        return container;
    }

    // Kiểm tra hiệu suất trang (optional)
    // const perfNow = window.performance.now();
    // console.log("Thời gian load trang: " + perfNow + "ms");

    // Kiểm tra trạng thái menu khi tải trang và thực thi toggleMenu nếu cần [cite: 14]
    if (window.innerWidth <= 1024 && sidebar) {
        // Mặc định đóng menu khi tải trang trên thiết bị di động
        sidebar.classList.remove("menu-active"); // [cite: 14]
        toggleMenu(false); // Gọi toggleMenu để đặt chiều cao và icon chính xác [cite: 15]
    }

}); // Kết thúc DOMContentLoaded