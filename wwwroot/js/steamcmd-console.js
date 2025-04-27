// Tệp: wwwroot/js/steamcmd-console.js
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
            bufferSize: 20,           // Số lượng dòng đệm trước khi render
            renderDelay: 16           // Thời gian chờ giữa các lần render (ms)
        }, options);

        // Khởi tạo các thuộc tính
        this.lines = [];
        this.lineElements = [];
        this.pendingLines = [];
        this.awaitingInput = false;
        this.profileId = null;
        this.outputLocked = false;
        this.pendingRender = false;
        this.lastRenderTime = 0;

        // Sử dụng Set để lưu trữ các dòng gần đây, tránh trùng lặp
        this._recentLines = new Set();
        this._maxRecentLines = 100;

        // Tạo giao diện console
        this.createConsoleUI();

        // Khởi động xong, sẵn sàng nhận dữ liệu
        this.isReady = true;

        // Khởi động requestAnimationFrame để tối ưu performance
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

            // Kiểm tra nếu dòng này đã hiển thị gần đây
            if (this._recentLines.has(text)) {
                continue; // Bỏ qua dòng lặp lại
            }

            // Thêm vào danh sách dòng gần đây
            this._recentLines.add(text);
            if (this._recentLines.size > this._maxRecentLines) {
                this._recentLines.delete([...this._recentLines][0]);
            }

            const lineElement = document.createElement('div');
            lineElement.classList.add('console-line');
            lineElement.classList.add(type);
            lineElement.textContent = text;

            this.lines.push({ text, type });
            this.lineElements.push(lineElement);
            outputFragment.appendChild(lineElement);
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

        // Tối ưu rendering
        this.outputContainer.style.willChange = 'transform';
        this.outputContainer.style.transform = 'translateZ(0)';

        this.container.appendChild(this.outputContainer);

        // Tạo khu vực nhập liệu nếu cần
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
     * @param {string} type - Loại dòng (normal, error, warning, success)
     */
    addLine(text, type = 'normal') {
        if (!text) return;

        // Nếu dòng có nhiều dòng con (chứa \n) thì tách ra
        const lines = text.split('\n');
        for (const line of lines) {
            if (!line.trim()) continue;

            // Xác định loại thông báo nếu không chỉ định
            if (type === 'normal') {
                if (line.includes("Error") || line.includes("Lỗi") || line.includes("failed")) {
                    type = 'error';
                } else if (line.includes("Warning") || line.includes("Cảnh báo")) {
                    type = 'warning';
                } else if (line.includes("Success") || line.includes("thành công") || line.includes("successfully")) {
                    type = 'success';
                }
            }

            // Đẩy vào hàng đợi thay vì render ngay lập tức
            this.pendingLines.push({ text: line, type });
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
        this.inputElement.placeholder = 'Nhập dữ liệu...';
        this.inputElement.focus();

        // Thay đổi giao diện để làm nổi bật
        this.inputContainer.style.borderColor = '#ff9800';
        this.inputContainer.style.boxShadow = '0 0 10px rgba(255, 152, 0, 0.5)';

        // Hiện và cuộn đến khu vực nhập
        this.inputContainer.style.display = 'flex';
        this.scrollToBottom();

        // Thêm lớp CSS để tăng độ ưu tiên
        this.container.classList.add('awaiting-input');
    }

    /**
     * Tắt chế độ nhập cho console
     */
    disableConsoleInput() {
        if (!this.options.showInput) return;

        this.awaitingInput = false;

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
        if (this.awaitingInput) {
            this.handleConsoleInput(input);
        }

        // Xóa dữ liệu đã nhập
        this.inputElement.value = '';
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
        this._recentLines.clear();
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

        // Tạo kết nối mới
        const connection = new signalR.HubConnectionBuilder()
            .withUrl("/logHub")
            .withAutomaticReconnect([0, 1000, 5000, 10000])
            .configureLogging(signalR.LogLevel.Error)
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

        // Đăng ký sự kiện nhận log
        connection.on("ReceiveLog", function (message) {
            if (!window.steamConsole || !message) return;

            // Sử dụng addLine đã được cải thiện để tránh trùng lặp
            window.steamConsole.addLine(message);
        });

        // Thêm sự kiện bật chế độ nhập console
        connection.on("EnableConsoleInput", function (profileId) {
            if (!window.steamConsole) return;

            window.steamConsole.setProfileId(profileId);
            window.steamConsole.enableConsoleInput();
        });

        // Thêm sự kiện tắt chế độ nhập console
        connection.on("DisableConsoleInput", function () {
            if (!window.steamConsole) return;

            window.steamConsole.disableConsoleInput();
        });

        // Khởi động kết nối với xử lý lỗi tốt hơn
        connection.start()
            .then(function () {
                window.steamConsole.addLine('Đã kết nối với máy chủ thành công', 'success');
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
            bufferSize: 30,
            renderDelay: 16,
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