// Profile Selection Enhancement
document.addEventListener('DOMContentLoaded', function() {
    // Select all profile dropdown elements
    const profileSelects = document.querySelectorAll('.profile-select');
    
    // Initialize each profile select
    profileSelects.forEach(select => {
        enhanceProfileSelect(select);
    });
    
    // Function to enhance profile select
    function enhanceProfileSelect(select) {
        // Get parent container
        const container = select.closest('.profile-select-container') || select.parentElement;
        
        // Create search input if it doesn't exist
        if (!container.querySelector('.profile-search')) {
            const searchDiv = document.createElement('div');
            searchDiv.className = 'profile-search';
            
            const searchIcon = document.createElement('span');
            searchIcon.className = 'profile-search-icon';
            searchIcon.innerHTML = '<i class="bi bi-search"></i>';
            
            const searchInput = document.createElement('input');
            searchInput.type = 'text';
            searchInput.className = 'profile-search-input';
            searchInput.placeholder = 'Tìm kiếm profile...';
            
            searchDiv.appendChild(searchIcon);
            searchDiv.appendChild(searchInput);
            
            // Insert search before the select element
            select.parentNode.insertBefore(searchDiv, select);
            
            // Add search functionality
            searchInput.addEventListener('input', function() {
                const searchTerm = this.value.toLowerCase();
                const options = select.querySelectorAll('option');
                
                options.forEach(option => {
                    if (option.value === '') return; // Skip placeholder option
                    
                    const text = option.textContent.toLowerCase();
                    const isVisible = text.includes(searchTerm);
                    
                    // Use data attribute to track visibility
                    option.dataset.visible = isVisible;
                    
                    // This doesn't actually hide the option in native dropdown,
                    // but we can use for custom dropdown implementations
                    if (window.customDropdown) {
                        option.style.display = isVisible ? '' : 'none';
                    }
                });
            });
        }
        
        // Add game icons to options via CSS classes
        const options = select.querySelectorAll('option');
        options.forEach(option => {
            if (option.value === '') return; // Skip placeholder option
            
            // Get game name from option text
            const text = option.textContent.trim();
            
            // Determine icon based on game name (simplified example)
            let iconClass = 'bi-controller';
            
            if (text.includes('CS') || text.includes('Counter')) {
                iconClass = 'bi-bullseye';
            } else if (text.includes('PUBG') || text.includes('Battlegrounds')) {
                iconClass = 'bi-person-standing';
            } else if (text.includes('Apex')) {
                iconClass = 'bi-trophy';
            } else if (text.includes('Global')) {
                iconClass = 'bi-globe';
            }
            
            // Store icon class in data attribute (for custom rendering)
            option.dataset.icon = iconClass;
        });
        
        // Add placeholder if not exists
        if (!select.querySelector('option[value=""]')) {
            const placeholder = document.createElement('option');
            placeholder.value = '';
            placeholder.textContent = '-- Chọn profile --';
            select.insertBefore(placeholder, select.firstChild);
        }
        
        // Force placeholder selection if nothing selected
        if (!select.value) {
            select.value = '';
        }
        
        // Custom styles for dropdown
        select.classList.add('profile-select');
    }
    
    // Add event listener for any new profile selects added to the DOM
    const observer = new MutationObserver(mutations => {
        mutations.forEach(mutation => {
            if (mutation.type === 'childList') {
                mutation.addedNodes.forEach(node => {
                    if (node.nodeType === Node.ELEMENT_NODE) {
                        const newSelects = node.querySelectorAll('.profile-select');
                        newSelects.forEach(enhanceProfileSelect);
                    }
                });
            }
        });
    });
    
    // Start observing the document for changes
    observer.observe(document.body, { childList: true, subtree: true });
}); 