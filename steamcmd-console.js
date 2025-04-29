addLine(text, type = 'normal') {
    if (!text) return;

    // Kiểm tra lỗi đăng nhập và định dạng đặc biệt
    if (text.includes("LỖI ĐĂNG NHẬP") || text.includes("Sai tên đăng nhập hoặc mật khẩu")) {
        // Thêm trực tiếp vào console với định dạng đặc biệt
        const errorLine = document.createElement('div');
        errorLine.classList.add('console-line', 'error', 'login-error');
        errorLine.style.fontWeight = 'bold';
        errorLine.style.color = '#ff5555';
        errorLine.style.padding = '8px';
        errorLine.style.border = '1px solid #ff5555';
        errorLine.style.marginTop = '10px';
        errorLine.style.marginBottom = '10px';
        errorLine.style.borderRadius = '4px';
        errorLine.style.backgroundColor = '#ffebeb';
        errorLine.style.display = 'flex';
        errorLine.style.alignItems = 'center';
        
        // Thêm icon cảnh báo
        const icon = document.createElement('span');
        icon.innerHTML = '⚠️';
        icon.style.marginRight = '8px';
        icon.style.fontSize = '20px';
        errorLine.appendChild(icon);
        
        // Thêm nội dung lỗi
        const content = document.createElement('div');
        content.style.flex = '1';
        content.textContent = text;
        errorLine.appendChild(content);
        
        this.outputContainer.appendChild(errorLine);
        
        // Cuộn xuống để hiển thị thông báo lỗi
        if (this.autoScroll) {
            this.scrollToBottom();
        }
        return;
    }

    // Nếu dòng có nhiều dòng con (chứa \n) thì tách ra
    const lines = text.split('\n');
    for (const line of lines) {
        if (!line.trim()) continue;

        // Xác định loại thông báo
        let currentType = type;
        if (type === 'normal') {
            if (line.includes("Error") || line.includes("Lỗi") || line.includes("KHÔNG thành công") || 
                line.includes("thất bại") || line.includes("failed") || line.includes("ERROR")) {
                currentType = 'error';
            } else if (line.includes("Warning") || line.includes("Cảnh báo")) {
                currentType = 'warning';
            } else if (line.includes("Success") || line.includes("thành công") || line.includes("successfully") || 
                      line.includes("hoàn tất") || line.includes("đã xong")) {
                currentType = 'success';
            }
        }

        // Đẩy vào hàng đợi
        this.pendingLines.push({ text: line, type: currentType });
    }
} 