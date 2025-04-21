// Thêm vào file wwwroot/js/steamcmd-console.js

/**
 * SteamCMD Console Viewer
 * Hiển thị console SteamCMD với hiệu ứng gõ dữ liệu từ dưới lên và hỗ trợ nhập 2FA
 */
class SteamCmdConsole {
    constructor(containerId, options = {}) {
        this.container = document.getElementById(containerId);
        if (!this.container) {
            console.error(`Container với ID ${containerId} không tồn tại!`);
            return;
        }

        // Cấu hình mặc định
        this.options = Object.assign({
            maxLines: 1000,           // Số dòng tối đa hiển thị
            autoScroll: true,         // Tự động cuộn xuống
            showInput: true,          // Hiển thị ô nhập liệu 
            inputEnabled: false,      // Cho phép nhập dữ liệu
            promptText: ">",          // Ký tự hiển thị ở đầu dòng nhập
            steamGuardDetection: true // Tự động phát hiện yêu cầu Steam Guard
        }, options);

        // Khởi tạo các thuộc tính
        this.lines = [];
        this.lineElements = [];
        this.awaitingInput = false;
        this.awaitingAuthCode = false;
        this.profileId = null;
        this.outputLocked = false;
        
        // Tạo giao diện console
        this.createConsoleUI();
        
        // Khởi động xong, sẵn sàng nhận dữ liệu
        this.isReady = true;
    }

    /**
     * Tạo giao diện console UI
     */
    createConsoleUI() {
        this.container.innerHTML = '';
        this.container.classList.add('steamcmd-console');

        // Tạo khu vực hiển thị đầu ra
        this.outputContainer = document.createElement('div');
        this.outputContainer.classList.add('steamcmd-console-output');
        if (this.options.autoScroll) {
            this.outputContainer.classList.add('auto-scroll');
        }
        this.container.appendChild(this.outputContainer);

        // Tạo khu vực nhập liệu
        if (this.options.showInput) {
            this.inputContainer = document.createElement('div');
            this.inputContainer.classList.add('steamcmd-console-input');
            
            // Label hiển thị dấu nhắc
            this.promptLabel = document.createElement('span');
            this.promptLabel.textContent = this.options.promptText + ' ';
            this.promptLabel.classList.add('prompt-text');
            
            // Input để nhập dữ liệu
            this.inputElement = document.createElement('input');
            this.inputElement.type = 'text';
            this.inputElement.placeholder = this.options.inputEnabled ? 'Nhập lệnh hoặc dữ liệu...' : 'Đang chờ yêu cầu nhập liệu...';
            this.inputElement.disabled = !this.options.inputEnabled;
            
            // Nút gửi
            this.sendButton = document.createElement('button');
            this.sendButton.textContent = 'Gửi';
            this.sendButton.disabled = !this.options.inputEnabled;
            
            // Thêm sự kiện
            this.inputElement.addEventListener('keypress', (e) => {
                if (e.key === 'Enter' && !this.sendButton.disabled) {
                    this.submitInput();
                }
            });
            
            this.sendButton.addEventListener('click', () => {
                this.submitInput();
            });
            
            // Thêm các phần tử vào container
            this.inputContainer.appendChild(this.promptLabel);
            this.inputContainer.appendChild(this.inputElement);
            this.inputContainer.appendChild(this.sendButton);
            this.container.appendChild(this.inputContainer);
        }
    }

    /**
     * Thêm một dòng mới vào console
     * @param {string} text - Nội dung dòng
     * @param {string} type - Loại dòng (normal, error, warning, success, steam-guard)
     */
    addLine(text, type = 'normal') {
        if (!text) return;
        
        // Nếu dòng có nhiều dòng con (chứa \n) thì tách ra
        const lines = text.split('\n');
        for (const line of lines) {
            if (!line.trim()) continue;
            
            // Tạo một phần tử dòng mới
            const lineElement = document.createElement('div');
            lineElement.classList.add('console-line');
            lineElement.classList.add(type);
            lineElement.textContent = line;
            
            // Thêm vào danh sách dòng và container
            this.lines.push({ text: line, type });
            this.lineElements.push(lineElement);
            this.outputContainer.appendChild(lineElement);
            
            // Kiểm tra và loại bỏ các dòng cũ nếu vượt quá giới hạn
            if (this.lines.length > this.options.maxLines) {
                this.lines.shift();
                const oldElement = this.lineElements.shift();
                if (oldElement && oldElement.parentNode) {
                    oldElement.parentNode.removeChild(oldElement);
                }
            }
            
            // Tự động cuộn xuống nếu được bật
            if (this.options.autoScroll) {
                this.scrollToBottom();
            }
            
            // Phát hiện yêu cầu Steam Guard
            if (this.options.steamGuardDetection) {
                this.detectSteamGuardPrompt(line);
            }
        }
    }

    /**
     * Phát hiện yêu cầu Steam Guard từ nội dung văn bản
     * @param {string} text - Nội dung dòng văn bản
     */
    detectSteamGuardPrompt(text) {
        // Kiểm tra các mẫu văn bản có thể là yêu cầu Steam Guard
        const steamGuardPatterns = [
            /steam guard code/i,
            /enter the current code/i,
            /two-factor code/i,
            /mobile authenticator/i,
            /đã gửi mã xác thực/i,
            /steam guard/i,
            /nhập mã xác thực/i
        ];
        
        if (steamGuardPatterns.some(pattern => pattern.test(text))) {
            this.enableSteamGuardInput();
            
            // Thêm một dòng nhấn mạnh yêu cầu Steam Guard
            this.addLine('>>> YÊU CẦU NHẬP MÃ XÁC THỰC STEAM GUARD <<<', 'steam-guard');
        }
    }

    /**
     * Bật chế độ nhập mã Steam Guard
     */
    enableSteamGuardInput() {
        if (!this.options.showInput) return;
        
        this.awaitingAuthCode = true;
        this.inputElement.disabled = false;
        this.sendButton.disabled = false;
        this.inputElement.placeholder = 'Nhập mã xác thực Steam Guard...';
        this.inputElement.focus();
        
        // Thay đổi giao diện để làm nổi bật
        this.inputContainer.style.borderColor = '#ff9800';
        this.inputContainer.style.boxShadow = '0 0 10px rgba(255, 152, 0, 0.5)';
    }

    /**
     * Gửi dữ liệu đã nhập
     */
    submitInput() {
        const input = this.inputElement.value.trim();
        if (!input) return;
        
        // Hiển thị dữ liệu nhập vào console
        this.addLine(`${this.options.promptText} ${input}`, 'user-input');
        
        // Xử lý theo loại đầu vào
        if (this.awaitingAuthCode) {
            this.handleSteamGuardInput(input);
        } else if (this.awaitingInput) {
            this.handleUserInput(input);
        }
        
        // Xóa dữ liệu đã nhập
        this.inputElement.value = '';
    }

    /**
     * Xử lý khi người dùng nhập mã Steam Guard
     * @param {string} code - Mã xác thực người dùng nhập vào
     */
    handleSteamGuardInput(code) {
        // Reset trạng thái
        this.awaitingAuthCode = false;
        this.inputContainer.style.borderColor = '';
        this.inputContainer.style.boxShadow = '';
        
        // Vô hiệu hóa input nếu không cần nữa
        if (!this.awaitingInput && !this.options.inputEnabled) {
            this.inputElement.disabled = true;
            this.sendButton.disabled = true;
            this.inputElement.placeholder = 'Đang chờ yêu cầu nhập liệu...';
        }
        
        // Gửi mã xác thực đến callback nếu có
        if (typeof this.options.onSteamGuardSubmit === 'function') {
            this.options.onSteamGuardSubmit(code, this.profileId);
        }
        
        // Hoặc gửi mã xác thực qua SignalR
        if (window.connection) {
            try {
                window.connection.invoke("SubmitTwoFactorCode", this.profileId || 1, code);
                this.addLine('Đã gửi mã Steam Guard đến SteamCMD...', 'success');
            } catch (err) {
                console.error('Lỗi khi gửi mã 2FA qua SignalR:', err);
                this.addLine('Lỗi khi gửi mã xác thực: ' + err, 'error');
            }
        }
    }

    /**
     * Xử lý khi người dùng nhập lệnh thông thường
     * @param {string} input - Lệnh người dùng nhập vào
     */
    handleUserInput(input) {
        this.awaitingInput = false;
        
        // Vô hiệu hóa input nếu không cần nữa
        if (!this.options.inputEnabled) {
            this.inputElement.disabled = true;
            this.sendButton.disabled = true;
        }
        
        // Gửi lệnh đến callback nếu có
        if (typeof this.options.onCommandSubmit === 'function') {
            this.options.onCommandSubmit(input);
        }
    }

    /**
     * Đặt profile ID hiện tại
     * @param {number} id - ID của profile đang chạy
     */
    setProfileId(id) {
        this.profileId = id;
    }

    /**
     * Xóa toàn bộ nội dung console
     */
    clear() {
        this.lines = [];
        this.lineElements = [];
        this.outputContainer.innerHTML = '';
    }

    /**
     * Cuộn xuống cuối console
     */
    scrollToBottom() {
        this.outputContainer.scrollTop = this.outputContainer.scrollHeight;
    }
    
    /**
     * Bật/tắt chế độ cuộn tự động
     * @param {boolean} enabled - Trạng thái bật/tắt
     */
    setAutoScroll(enabled) {
        this.options.autoScroll = enabled;
        if (enabled) {
            this.outputContainer.classList.add('auto-scroll');
            this.scrollToBottom();
        } else {
            this.outputContainer.classList.remove('auto-scroll');
        }
    }
}

// Khởi tạo console khi trang được tải
document.addEventListener('DOMContentLoaded', function() {
    // Kiểm tra nếu container tồn tại
    if (document.getElementById('steamcmd-console')) {
        // Khởi tạo global instance để các phần khác có thể truy cập
        window.steamConsole = new SteamCmdConsole('steamcmd-console', {
            showInput: true,
            steamGuardDetection: true,
            onSteamGuardSubmit: function(code, profileId) {
                console.log(`Đã nhận mã 2FA: ${code} cho profile: ${profileId}`);
            }
        });
    }
    
    // Thiết lập SignalR kết nối console
    setupConsoleSignalRConnection();
});

/**
 * Thiết lập kết nối SignalR cho console
 */
function setupConsoleSignalRConnection() {
    if (!window.steamConsole) return;
    
    // Kiểm tra xem SignalR đã được khởi tạo chưa
    if (typeof signalR === 'undefined') {
        console.error('SignalR chưa được tải');
        return;
    }
    
    try {
        // Nếu kết nối đã tồn tại, đảm bảo đã đóng trước khi tạo mới
        if (window.connection) {
            window.connection.stop();
        }
        
        // Tạo kết nối mới
        const connection = new signalR.HubConnectionBuilder()
            .withUrl("/logHub")
            .withAutomaticReconnect()
            .build();
        
        // Đăng ký sự kiện nhận log
        connection.on("ReceiveLog", function(message) {
            if (!window.steamConsole) return;
            
            // Xác định loại thông báo
            let type = 'normal';
            if (message.includes("Error") || message.includes("Lỗi") || message.includes("failed")) {
                type = 'error';
            } else if (message.includes("Warning") || message.includes("Cảnh báo")) {
                type = 'warning';
            } else if (message.includes("Success") || message.includes("thành công") || message.includes("successfully")) {
                type = 'success';
            } else if (message.includes("Steam Guard") || message.includes("2FA") || message.includes("mã xác thực")) {
                type = 'steam-guard';
            }
            
            // Thêm vào console
            window.steamConsole.addLine(message, type);
        });
        
        // Đăng ký sự kiện yêu cầu nhập mã xác thực
        connection.on("RequestTwoFactorCode", function(profileId) {
            if (!window.steamConsole) return;
            
            window.steamConsole.setProfileId(profileId);
            window.steamConsole.addLine(`Đang yêu cầu mã xác thực cho profile ID: ${profileId}`, 'steam-guard');
            window.steamConsole.enableSteamGuardInput();
        });
        
        // Khởi động kết nối
        connection.start()
            .then(function() {
                window.steamConsole.addLine('Đã kết nối với máy chủ thành công', 'success');
                
                // Lưu connection vào biến global để sử dụng sau này
                window.connection = connection;
            })
            .catch(function(err) {
                window.steamConsole.addLine(`Lỗi kết nối: ${err.toString()}`, 'error');
                console.error(err.toString());
            });
    } catch (ex) {
        window.steamConsole.addLine(`Lỗi khởi tạo kết nối: ${ex.toString()}`, 'error');
        console.error('Lỗi khởi tạo SignalR:', ex);
    }
}