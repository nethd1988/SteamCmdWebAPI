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
});