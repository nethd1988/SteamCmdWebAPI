/**
 * Module kiểm tra xác thực client-side
 * 
 * File này chịu trách nhiệm kiểm tra trạng thái đăng nhập của người dùng
 * và thực hiện chuyển hướng khi cần thiết.
 */

// Module tự thực thi
(function() {
    // Biến cờ để tránh chuyển hướng nhiều lần
    let redirectInProgress = false;
    
    // Danh sách các đường dẫn không cần kiểm tra xác thực
    const excludedPaths = [
        '/login',
        '/register',
        '/logout',
        '/error',
        '/licenseerror'
    ];
    
    // Kiểm tra nếu đường dẫn hiện tại thuộc danh sách loại trừ
    function isExcludedPath() {
        const currentPath = window.location.pathname.toLowerCase();
        return excludedPaths.some(path => currentPath.includes(path));
    }
    
    // Kiểm tra và xóa cookie bị lỗi
    function cleanupBrokenCookies() {
        const cookies = document.cookie.split(';');
        for (let i = 0; i < cookies.length; i++) {
            const cookie = cookies[i].trim();
            // Tìm cookie .AspNetCore.Cookies bị lỗi
            if (cookie.startsWith('.AspNetCore.Cookies=') && cookie.includes('error')) {
                document.cookie = '.AspNetCore.Cookies=; expires=Thu, 01 Jan 1970 00:00:00 UTC; path=/;';
                console.log('Đã xóa cookie xác thực bị lỗi');
                
                if (!isExcludedPath() && !redirectInProgress) {
                    redirectInProgress = true;
                    window.location.href = '/login';
                }
                break;
            }
        }
    }

    // Kiểm tra trạng thái đăng nhập từ API
    function checkAuthStatus() {
        // Nếu đang ở trang loại trừ, không cần kiểm tra
        if (isExcludedPath() || redirectInProgress) {
            return;
        }
        
        fetch('/api/auth/check-session')
            .then(response => {
                if (!response.ok) {
                    throw new Error('Phản hồi không hợp lệ');
                }
                return response.json();
            })
            .then(data => {
                if (!data.authenticated) {
                    console.log('Phiên làm việc không hợp lệ, chuyển hướng đến trang đăng nhập');
                    redirectInProgress = true;
                    
                    // Lưu URL hiện tại để chuyển hướng trở lại sau khi đăng nhập
                    const currentUrl = window.location.pathname + window.location.search;
                    window.location.href = `/login?ReturnUrl=${encodeURIComponent(currentUrl)}`;
                }
            })
            .catch(error => {
                console.error('Lỗi kiểm tra xác thực:', error);
                // Không chuyển hướng khi có lỗi kết nối để tránh trường hợp mất kết nối tạm thời
            });
    }
    
    // Khởi tạo việc kiểm tra
    function initialize() {
        // Không thực hiện kiểm tra nếu đang ở trang loại trừ
        if (isExcludedPath()) {
            return;
        }
        
        // Kiểm tra ngay lập tức sau khi tải trang
        cleanupBrokenCookies();
        
        // Trì hoãn việc kiểm tra xác thực để tránh chuyển hướng ngay lập tức
        setTimeout(checkAuthStatus, 500);
        
        // Thiết lập kiểm tra định kỳ
        setInterval(checkAuthStatus, 30000); // Kiểm tra mỗi 30 giây
    }
    
    // Khởi động khi trang đã tải xong
    if (document.readyState === 'loading') {
        document.addEventListener('DOMContentLoaded', initialize);
    } else {
        initialize();
    }
})();