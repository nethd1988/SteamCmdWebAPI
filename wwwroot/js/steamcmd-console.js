// Tệp: wwwroot/js/steamcmd-console.js
// Cập nhật lớp SteamCmdConsole để xử lý Steam Guard tốt hơn

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
            steamGuardDetection: true, // Tự động phát hiện yêu cầu Steam Guard
            bufferSize: 20,           // Số lượng dòng đệm trước khi render
            renderDelay: 16           // Thời gian chờ giữa các lần render (ms)
        }, options);

        // Khởi tạo các thuộc tính
        this.lines = [];
        this.lineElements = [];
        this.pendingLines = [];
        this.awaitingInput = false;
        this.awaitingAuthCode = false;
        this.profileId = null;
        this.outputLocked = false;
        this.pendingRender = false;
        this.lastRenderTime = 0;
        this.lastLogMessage = '';
        this.lastSteamGuardTime = 0;

        // Tạo giao diện console
        this.createConsoleUI();

        // Khởi động xong, sẵn sàng nhận dữ liệu
        this.isReady = true;

        // Khởi động RAF để tối ưu performance
        this.setupRenderLoop();
    }

    /**
     * Thiết lập vòng lặp render sử dụng requestAnimationFrame
     */
    setupRenderLoop() {
        const renderLoop = () => {
            const now = performance.now();
            const deltaTime = now - this.lastRenderTime;

            // Chỉ render khi có dòng mới và đủ thời gian delay
            if (this.pendingLines.length > 0 && deltaTime >= this.options.renderDelay) {
                this.processPendingLines();
                this.lastRenderTime = now;
            }

            requestAnimationFrame(renderLoop);
        };

        requestAnimationFrame(renderLoop);
    }

    /**
     * Xử lý các dòng đang đợi render
     */
    processPendingLines() {
        if (this.pendingLines.length === 0) return;

        const outputFragment = document.createDocumentFragment();
        const linesToProcess = Math.min(this.pendingLines.length, this.options.bufferSize);

        for (let i = 0; i < linesToProcess; i++) {
            const { text, type } = this.pendingLines.shift();

            // Tránh thêm các dòng Steam Guard trùng lặp
            if (this.isSteamGuardLine(text) && this.isRecentSteamGuardMessage()) {
                continue;
            }

            const lineElement = document.createElement('div');
            lineElement.classList.add('console-line');
            lineElement.classList.add(type);
            lineElement.textContent = text;

            this.lines.push({ text, type });
            this.lineElements.push(lineElement);
            outputFragment.appendChild(lineElement);

            // Phát hiện yêu cầu Steam Guard
            if (this.options.steamGuardDetection) {
                this.detectSteamGuardPrompt(text);
            }

            // Đánh dấu nếu là dòng Steam Guard
            if (this.isSteamGuardLine(text)) {
                this.lastSteamGuardTime = performance.now();
            }
        }

        // Thêm tất cả dòng vào DOM trong một thao tác duy nhất
        this.outputContainer.appendChild(outputFragment);

        // Kiểm tra và loại bỏ các dòng cũ nếu vượt quá giới hạn
        this.pruneOldLines();

        // Tự động cuộn xuống nếu được bật
        if (this.options.autoScroll) {
            this.scrollToBottom();
        }
    }

    /**
     * Kiểm tra xem dòng có phải là thông báo Steam Guard hay không
     */
    isSteamGuardLine(text) {
        return text.includes("Steam Guard") ||
            text.includes("mã xác thực") ||
            text.includes("Two-factor") ||
            text.includes("Mobile Authenticator");
    }

    /**
     * Kiểm tra xem đã có thông báo Steam Guard gần đây chưa
     */
    isRecentSteamGuardMessage() {
        return (performance.now() - this.lastSteamGuardTime) < 5000; // 5 giây
    }

    /**
     * Loại bỏ các dòng cũ khi vượt quá giới hạn
     */
    pruneOldLines() {
        const excessLines = this.lines.length - this.options.maxLines;
        if (excessLines <= 0) return;

        // Xóa các dòng cũ
        this.lines.splice(0, excessLines);

        // Xóa DOM elements tương ứng
        const elementsToRemove = this.lineElements.splice(0, excessLines);
        for (const element of elementsToRemove) {
            if (element.parentNode) {
                element.parentNode.removeChild(element);
            }
        }
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

        // Tối ưu rendering với virtualization
        this.outputContainer.style.willChange = 'transform';
        this.outputContainer.style.transform = 'translateZ(0)';

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
            this.sendButton.classList.add('btn', 'btn-primary', 'btn-sm');
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

        // Tránh xử lý những dòng Mobile Authenticator hoặc Steam Guard liên tục trùng lặp
        if (this.lastLogMessage === text && this.isSteamGuardLine(text)) {
            return;
        }

        this.lastLogMessage = text;

        // Nếu dòng có nhiều dòng con (chứa \n) thì tách ra
        const lines = text.split('\n');
        for (const line of lines) {
            if (!line.trim()) continue;

            // Đẩy vào hàng đợi thay vì render ngay lập tức
            this.pendingLines.push({ text: line, type });
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

        // Chỉ phát hiện nếu hiện không có yêu cầu 2FA đang chờ
        // và thời gian từ lần cuối phát hiện phải lớn hơn 5 giây
        const now = performance.now();
        if (!this.awaitingAuthCode &&
            (now - this.lastSteamGuardTime > 5000) &&
            steamGuardPatterns.some(pattern => pattern.test(text))) {
            this.lastSteamGuardTime = now;
            this.enableConsoleInput();
            this.awaitingAuthCode = true;
        }
    }

    /**
     * Bật chế độ nhập cho console
     */
    enableConsoleInput() {
        if (!this.options.showInput) return;

        this.awaitingInput = true;
        this.inputElement.disabled = false;
        this.sendButton.disabled = false;
        this.inputElement.placeholder = 'Nhập mã xác thực Steam Guard...';
        this.inputElement.focus();

        // Thay đổi giao diện để làm nổi bật
        this.inputContainer.style.borderColor = '#ff9800';
        this.inputContainer.style.boxShadow = '0 0 10px rgba(255, 152, 0, 0.5)';

        // Hiện và cuộn đến khu vực nhập
        this.inputContainer.style.display = 'flex';
        this.scrollToBottom();

        // Thêm lớp CSS để tăng độ ưu tiên
        this.container.classList.add('awaiting-input');

        // Thông báo đặc biệt
        this.addLine('STEAM GUARD: Vui lòng nhập mã xác thực vào ô bên dưới và nhấn Enter', 'steam-guard');
    }

    /**
     * Tắt chế độ nhập cho console
     */
    disableConsoleInput() {
        if (!this.options.showInput) return;

        this.awaitingInput = false;
        this.awaitingAuthCode = false;

        if (!this.options.inputEnabled) {
            this.inputElement.disabled = true;
            this.sendButton.disabled = true;
        }

        this.inputElement.placeholder = 'Đang chờ yêu cầu nhập liệu...';

        // Khôi phục kiểu giao diện
        this.inputContainer.style.borderColor = '';
        this.inputContainer.style.boxShadow = '';

        // Xóa lớp CSS
        this.container.classList.remove('awaiting-input');
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
            this.handleConsoleInput(input);
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

        // Gửi mã xác thực đến callback nếu có
        if (typeof this.options.onSteamGuardSubmit === 'function') {
            this.options.onSteamGuardSubmit(code, this.profileId);
            this.addLine('Đã gửi mã Steam Guard đến SteamCMD...', 'success');
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

        // Vô hiệu hóa chế độ nhập console
        this.disableConsoleInput();
    }

    /**
     * Xử lý khi người dùng nhập dữ liệu từ console
     * @param {string} input - Dữ liệu người dùng nhập vào
     */
    handleConsoleInput(input) {
        // Gửi dữ liệu qua SignalR
        if (window.connection) {
            try {
                window.connection.invoke("SubmitConsoleInput", this.profileId || 1, input);
                this.addLine('Đã gửi dữ liệu đến SteamCMD...', 'success');
            } catch (err) {
                console.error('Lỗi khi gửi dữ liệu qua SignalR:', err);
                this.addLine('Lỗi khi gửi dữ liệu: ' + err, 'error');
            }
        }

        // Callback nếu có
        if (typeof this.options.onInputSubmit === 'function') {
            this.options.onInputSubmit(input, this.profileId);
        }

        // Không vô hiệu hóa input ở đây để cho phép nhập nhiều lần nếu cần
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
        this.pendingLines = [];
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

// Thiết lập kết nối SignalR cho console với tối ưu hiệu suất
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

        // Tạo kết nối mới với tối ưu hiệu suất
        const connection = new signalR.HubConnectionBuilder()
            .withUrl("/logHub")
            .withAutomaticReconnect([0, 1000, 5000, 10000]) // Thử kết nối lại nhanh hơn
            .configureLogging(signalR.LogLevel.Error) // Giảm log để tăng hiệu suất
            .build();

        // Xử lý lỗi kết nối
        connection.onclose((error) => {
            if (error) {
                console.error("Kết nối SignalR đã đóng với lỗi:", error);
                if (window.steamConsole) {
                    window.steamConsole.addLine("Mất kết nối với máy chủ. Đang thử kết nối lại...", "error");
                }
            }

            // Thử kết nối lại sau 2 giây
            setTimeout(() => {
                connection.start().catch(err => {
                    console.error("Không thể kết nối lại:", err);
                });
            }, 2000);
        });

        // Biến để theo dõi thông báo 2FA gần nhất
        let lastSteamGuardMessage = '';
        let lastSteamGuardTime = 0;

        // Đăng ký sự kiện nhận log - tối ưu xử lý các gói tin lớn
        connection.on("ReceiveLog", function (message) {
            if (!window.steamConsole) return;
            if (!message) return;

            // Lọc và chỉ hiển thị thông báo Steam Guard duy nhất
            if (isSteamGuardMessage(message)) {
                const now = Date.now();
                // Bỏ qua thông báo trùng lặp trong khoảng 5 giây
                if (message === lastSteamGuardMessage && now - lastSteamGuardTime < 5000) {
                    return;
                }
                lastSteamGuardMessage = message;
                lastSteamGuardTime = now;
            }

            // Xử lý từng dòng riêng biệt nếu có nhiều dòng
            const lines = message.split('\n');

            for (const line of lines) {
                if (!line.trim()) continue;

                // Xác định loại thông báo
                let type = 'normal';
                if (line.includes("Error") || line.includes("Lỗi") || line.includes("failed")) {
                    type = 'error';
                } else if (line.includes("Warning") || line.includes("Cảnh báo")) {
                    type = 'warning';
                } else if (line.includes("Success") || line.includes("thành công") || line.includes("successfully")) {
                    type = 'success';
                } else if (line.includes("Steam Guard") || line.includes("2FA") || line.includes("mã xác thực")) {
                    type = 'steam-guard';
                }

                // Thêm vào console
                window.steamConsole.addLine(line, type);
            }
        });

        // Đăng ký sự kiện yêu cầu nhập mã xác thực - chỉ xử lý một lần trong mỗi chu kỳ
        connection.on("RequestTwoFactorCode", function (profileId) {
            if (!window.steamConsole) return;

            const now = Date.now();
            // Bỏ qua yêu cầu 2FA liên tiếp trong khoảng 5 giây
            if (now - lastSteamGuardTime < 5000) {
                return;
            }

            lastSteamGuardTime = now;
            window.steamConsole.setProfileId(profileId);
            window.steamConsole.addLine(`Đang yêu cầu mã xác thực cho profile ID: ${profileId}`, 'steam-guard');
            window.steamConsole.awaitingAuthCode = true;
            window.steamConsole.enableConsoleInput();
        });

        // Thêm sự kiện bật chế độ nhập console
        connection.on("EnableConsoleInput", function (profileId) {
            if (!window.steamConsole) return;

            window.steamConsole.setProfileId(profileId);
            window.steamConsole.awaitingInput = true;
            window.steamConsole.enableConsoleInput();
        });

        // Thêm sự kiện tắt chế độ nhập console
        connection.on("DisableConsoleInput", function () {
            if (!window.steamConsole) return;

            window.steamConsole.disableConsoleInput();
        });

        // Hàm kiểm tra thông báo Steam Guard
        function isSteamGuardMessage(message) {
            return message.includes("Steam Guard") ||
                message.includes("mã xác thực") ||
                message.includes("Two-factor") ||
                message.includes("Mobile Authenticator");
        }

        // Khởi động kết nối với xử lý lỗi tốt hơn
        connection.start()
            .then(function () {
                window.steamConsole.addLine('Đã kết nối với máy chủ thành công', 'success');

                // Lưu connection vào biến global để sử dụng sau này
                window.connection = connection;
            })
            .catch(function (err) {
                window.steamConsole.addLine(`Lỗi kết nối: ${err.toString()}`, 'error');
                console.error("Lỗi kết nối SignalR:", err);

                // Thử kết nối lại sau 2 giây
                setTimeout(() => {
                    window.steamConsole.addLine("Đang thử kết nối lại...", "warning");
                    connection.start().catch(err => {
                        window.steamConsole.addLine(`Không thể kết nối lại: ${err.toString()}`, "error");
                    });
                }, 2000);
            });
    } catch (ex) {
        if (window.steamConsole) {
            window.steamConsole.addLine(`Lỗi khởi tạo kết nối: ${ex.toString()}`, 'error');
        }
        console.error('Lỗi khởi tạo SignalR:', ex);
    }
}

// Khởi tạo console khi trang được tải
document.addEventListener('DOMContentLoaded', function () {
    // Khởi tạo CSS cho console nếu chưa có
    if (!document.getElementById('steamcmd-console-styles')) {
        const style = document.createElement('style');
        style.id = 'steamcmd-console-styles';
        style.textContent = `
            .steamcmd-console {
                display: flex;
                flex-direction: column;
                height: 400px;
                background-color: #0d0d0d;
                color: #f0f0f0;
                font-family: Consolas, monospace;
                border-radius: 4px;
                overflow: hidden;
                will-change: transform;
                transform: translateZ(0);
            }
            
            .steamcmd-console-output {
                flex: 1;
                overflow-y: auto;
                padding: 10px;
                font-size: 14px;
                line-height: 1.4;
                will-change: transform;
                transform: translateZ(0);
            }
            
            .steamcmd-console-input {
                display: flex;
                background-color: #1a1a1a;
                padding: 8px;
                border-top: 1px solid #333;
                transition: border-color 0.2s ease-in-out, box-shadow 0.2s ease-in-out;
            }
            
            .steamcmd-console.awaiting-input .steamcmd-console-input {
                background-color: #2a2a2a;
                animation: pulse-border 1.5s infinite alternate;
            }
            
            @keyframes pulse-border {
                0% { border-color: #ff9800; box-shadow: 0 0 5px rgba(255, 152, 0, 0.5); }
                100% { border-color: #ffcc00; box-shadow: 0 0 10px rgba(255, 204, 0, 0.8); }
            }
            
            .steamcmd-console-input input {
                flex: 1;
                background-color: #2a2a2a;
                color: #fff;
                border: none;
                padding: 6px 10px;
                font-family: Consolas, monospace;
                font-size: 14px;
            }
            
            .steamcmd-console-input button {
                background-color: #4a4a4a;
                color: #fff;
                border: none;
                padding: 0 15px;
                margin-left: 5px;
                cursor: pointer;
                transition: background-color 0.15s ease;
            }
            
            .steamcmd-console-input button:hover {
                background-color: #5a5a5a;
            }
            
            .steamcmd-console-input button:disabled {
                background-color: #3a3a3a;
                color: #aaa;
                cursor: not-allowed;
            }
            
            .console-line {
                white-space: pre-wrap;
                word-break: break-word;
                margin: 1px 0;
                line-height: 1.3;
                transition: opacity 0.1s ease;
                opacity: 0.95;
                will-change: opacity, transform;
            }
            
            .console-line:hover {
                opacity: 1;
            }
            
            .console-line.error {
                color: #ff5555;
            }
            
            .console-line.warning {
                color: #ffaa00;
            }
            
            .console-line.success {
                color: #55ff55;
            }
            
            .console-line.steam-guard {
                color: #ffff00;
                font-weight: bold;
                animation: blink 1s infinite alternate;
            }
            
            @keyframes blink {
                0% { opacity: 0.8; }
                100% { opacity: 1; }
            }
            
            .console-line.user-input {
                color: #55aaff;
                font-weight: bold;
            }
            
            .prompt-text {
                color: #55aaff;
                margin-right: 5px;
            }
        `;
        document.head.appendChild(style);
    }

    // Kiểm tra nếu container tồn tại
    if (document.getElementById('steamcmd-console')) {
        // Khởi tạo global instance để các phần khác có thể truy cập
        window.steamConsole = new SteamCmdConsole('steamcmd-console', {
            showInput: true,
            steamGuardDetection: true,
            bufferSize: 30,  // Số lượng dòng xử lý mỗi lần
            renderDelay: 16, // Giữ FPS ổn định ở 60fps
            onSteamGuardSubmit: function (code, profileId) {
                console.log(`Đã nhận mã 2FA: ${code} cho profile: ${profileId}`);

                // Không cần popup để xác nhận
                if (window.connection) {
                    window.connection.invoke("SubmitTwoFactorCode", profileId || 1, code)
                        .catch(function (err) {
                            console.error("Lỗi khi gửi mã 2FA:", err);
                        });
                }
            },
            onInputSubmit: function (input, profileId) {
                console.log(`Đã nhận dữ liệu nhập: ${input} cho profile: ${profileId}`);

                if (window.connection) {
                    window.connection.invoke("SubmitConsoleInput", profileId || 1, input)
                        .catch(function (err) {
                            console.error("Lỗi khi gửi dữ liệu nhập:", err);
                        });
                }
            }
        });
    }

    // Thiết lập SignalR kết nối console
    setupConsoleSignalRConnection();

    // Tối ưu hiệu suất khi chuyển tab
    document.addEventListener('visibilitychange', function () {
        if (document.hidden) {
            // Khi tab không hiển thị, giảm tần suất render
            if (window.steamConsole) {
                window.steamConsole.options.renderDelay = 100; // 10fps
                window.steamConsole.options.bufferSize = 60;   // Tăng buffer size
            }
        } else {
            // Khi tab hiển thị lại, khôi phục hiệu suất
            if (window.steamConsole) {
                window.steamConsole.options.renderDelay = 16;  // 60fps
                window.steamConsole.options.bufferSize = 30;   // Giảm buffer size
                window.steamConsole.processPendingLines();     // Xử lý các dòng đang đợi
            }
        }
    });
});