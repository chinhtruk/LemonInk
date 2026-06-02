/* ============================================
   LemonInk — Chat Panel
   ============================================ */
(function () {
    'use strict';

    const panel = document.getElementById('zenChatPanel');
    const chatFab = document.getElementById('zenChatFab');
    const closeBtn = document.getElementById('zenChatClose');
    const chatForm = document.getElementById('zenChatForm');
    const chatInput = document.getElementById('zenChatInput');
    const chatSend = document.getElementById('zenChatSend');
    const messagesContainer = document.getElementById('zenChatMessages');
    const suggestedButtons = document.querySelectorAll('[data-chat-suggest]');

    if (!panel || !messagesContainer) return;

    const bookId = Number.parseInt(panel.getAttribute('data-book-id') || '', 10);
    let typingNode = null;
    let isSending = false;
    let hasLoadedHistory = false;

    function openPanel() {
        panel.classList.add('is-open');
        if (chatFab) chatFab.classList.add('is-active');
        loadHistory();
        scrollMessagesToBottom();
        window.setTimeout(function () {
            if (chatInput) chatInput.focus();
        }, 180);
    }

    function closePanel() {
        panel.classList.remove('is-open');
        if (chatFab) chatFab.classList.remove('is-active');
    }

    function togglePanel() {
        if (panel.classList.contains('is-open')) {
            closePanel();
            return;
        }

        openPanel();
    }

    function scrollMessagesToBottom() {
        window.requestAnimationFrame(function () {
            messagesContainer.scrollTop = messagesContainer.scrollHeight;
        });
    }

    function scrollMessageIntoView(messageNode) {
        window.requestAnimationFrame(function () {
            if (messageNode && typeof messageNode.scrollIntoView === 'function') {
                messageNode.scrollIntoView({ block: 'start', behavior: 'smooth' });
                return;
            }

            scrollMessagesToBottom();
        });
    }

    function resizeChatInput() {
        if (!chatInput) return;
        const maxHeight = 82;
        chatInput.style.height = 'auto';
        const nextHeight = Math.min(chatInput.scrollHeight, maxHeight);
        chatInput.style.height = nextHeight + 'px';
        chatInput.style.overflowY = chatInput.scrollHeight > maxHeight ? 'auto' : 'hidden';

        const shell = chatInput.closest('.zen-chat-input-shell');
        if (shell) {
            shell.classList.toggle('is-multiline', nextHeight > 44);
        }
    }

    function appendMessage(message, type) {
        const text = typeof message === 'string' ? message : (message.content || '');
        const wrapper = document.createElement('div');
        wrapper.className = 'zen-chat-message ' + type;

        const bubble = document.createElement('div');
        bubble.className = 'zen-chat-bubble';

        if (type === 'ai') {
            const avatar = document.createElement('div');
            avatar.className = 'zen-chat-avatar';
            avatar.textContent = 'L';

            const label = document.createElement('div');
            label.className = 'zen-chat-bubble-label';
            label.textContent = 'LemonAI';

            const body = document.createElement('div');
            body.className = 'zen-chat-bubble-text';
            renderAssistantText(body, text);

            bubble.appendChild(label);
            bubble.appendChild(body);
            wrapper.appendChild(avatar);
            wrapper.appendChild(bubble);
        } else {
            bubble.textContent = text;
            wrapper.appendChild(bubble);
        }

        messagesContainer.appendChild(wrapper);
        if (type === 'ai') {
            scrollMessageIntoView(wrapper);
        } else {
            scrollMessagesToBottom();
        }

        return wrapper;
    }

    function renderAssistantText(container, text) {
        const cleanText = cleanAssistantText(text);
        const segments = cleanText.split(/(\*\*[^*]+\*\*)/g);

        segments.forEach(function (segment) {
            if (!segment) return;

            if (segment.startsWith('**') && segment.endsWith('**') && segment.length > 4) {
                const strong = document.createElement('strong');
                strong.textContent = segment.slice(2, -2);
                container.appendChild(strong);
                return;
            }

            container.appendChild(document.createTextNode(segment));
        });
    }

    function cleanAssistantText(text) {
        return (text || '')
            .replace(/__(.*?)__/g, '**$1**')
            .replace(/^\s{0,3}#{1,6}\s+/gm, '')
            .replace(/^\s*[-*]\s+/gm, '')
            .replace(/`{1,3}/g, '')
            .trim();
    }

    function showTypingIndicator() {
        const message = document.createElement('div');
        message.className = 'zen-chat-message ai is-typing';
        message.innerHTML = '<div class="zen-chat-avatar">L</div><div class="zen-chat-bubble"><div class="zen-chat-bubble-label">LemonAI</div><div class="zen-chat-typing"><span></span><span></span><span></span></div></div>';
        messagesContainer.appendChild(message);
        scrollMessagesToBottom();
        return message;
    }

    function clearTypingIndicator() {
        if (typingNode && typingNode.parentNode) {
            typingNode.parentNode.removeChild(typingNode);
        }

        typingNode = null;
    }

    function setSending(nextValue) {
        isSending = nextValue;
        if (chatSend) chatSend.disabled = nextValue;
        if (chatInput) chatInput.disabled = nextValue;
    }

    async function loadHistory() {
        if (hasLoadedHistory || !Number.isFinite(bookId)) return;
        hasLoadedHistory = true;

        try {
            const response = await fetch('/Chat/History/' + encodeURIComponent(bookId), {
                headers: { 'Accept': 'application/json' }
            });

            if (response.status === 401 || response.redirected) {
                appendMessage('Bạn đăng nhập để LemonAI lưu lịch sử chat theo sách nhé.', 'ai');
                return;
            }

            if (!response.ok) return;

            const payload = await response.json();
            const messages = payload.messages || [];
            if (!messages.length) return;

            messagesContainer.innerHTML = '';
            messages.forEach(function (message) {
                appendMessage(message, message.role === 'user' ? 'user' : 'ai');
            });
        } catch {
            hasLoadedHistory = false;
        }
    }

    async function sendMessage(textOverride) {
        const text = typeof textOverride === 'string'
            ? textOverride.trim()
            : (chatInput ? chatInput.value.trim() : '');

        if (!text || isSending) return;

        if (!Number.isFinite(bookId)) {
            appendMessage('Bạn mở một trang đọc sách rồi hỏi LemonAI theo nội dung cuốn đó nhé.', 'ai');
            return;
        }

        appendMessage(text, 'user');
        if (chatInput) {
            chatInput.value = '';
            resizeChatInput();
        }

        clearTypingIndicator();
        typingNode = showTypingIndicator();
        setSending(true);

        try {
            const response = await fetch('/Chat/Ask', {
                method: 'POST',
                headers: {
                    'Accept': 'application/json',
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify({
                    bookId: bookId,
                    message: text
                })
            });

            clearTypingIndicator();

            if (response.status === 401 || response.redirected) {
                appendMessage('Bạn đăng nhập để dùng LemonAI chat theo sách nhé.', 'ai');
                return;
            }

            const payload = await response.json().catch(function () { return {}; });
            if (!response.ok) {
                appendMessage(payload.message || 'LemonAI chưa trả lời được lúc này. Bạn thử lại sau nhé.', 'ai');
                return;
            }

            appendMessage({
                content: payload.message || 'Mình chưa nhận được câu trả lời rõ ràng từ LemonAI.'
            }, 'ai');
        } catch {
            clearTypingIndicator();
            appendMessage('Không kết nối được LemonAI. Bạn kiểm tra lại server rồi thử lại nhé.', 'ai');
        } finally {
            setSending(false);
        }
    }

    if (chatFab) chatFab.addEventListener('click', togglePanel);
    if (closeBtn) closeBtn.addEventListener('click', closePanel);

    document.querySelectorAll('[data-chat-open]').forEach(function (trigger) {
        trigger.addEventListener('click', openPanel);
    });

    if (chatForm) {
        chatForm.addEventListener('submit', function (event) {
            event.preventDefault();
            sendMessage();
        });
    }

    if (chatInput) {
        chatInput.addEventListener('input', resizeChatInput);
        chatInput.addEventListener('keydown', function (event) {
            if (event.key === 'Enter' && !event.shiftKey) {
                event.preventDefault();
                sendMessage();
            }
        });
        resizeChatInput();
    }

    suggestedButtons.forEach(function (button) {
        button.addEventListener('click', function () {
            openPanel();
            sendMessage(button.getAttribute('data-chat-suggest') || '');
        });
    });

    document.addEventListener('keydown', function (event) {
        if (event.key === 'Escape' && panel.classList.contains('is-open')) {
            closePanel();
        }
    });
})();
