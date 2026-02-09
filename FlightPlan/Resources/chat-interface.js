// simple markdown renderer for chat messages
function renderMarkdown(text) {
    return text
        .replace(/^### (.*$)/gim, '<h3>$1</h3>')
        .replace(/^## (.*$)/gim, '<h2>$1</h2>')
        .replace(/^# (.*$)/gim, '<h2>$1</h2>')
        .replace(/\[([^\]]+)\]\(([^\)]+)\)/g, '<a href="$2">$1</a>')
        .replace(/\*\*(.*?)\*\*/g, '<strong>$1</strong>')
        .replace(/\*(.*?)\*/g, '<em>$1</em>')
        .replace(/`([^`]+)`/g, '<code>$1</code>')
        .replace(/^- (.*$)/gim, '<li>$1</li>')
        .replace(/(<li>.*<\/li>)/s, '<ul>$1</ul>')
        .replace(/\n/g, '<br>');
}

const chatButton = document.getElementById('chat-button');
const chatModal = document.getElementById('chat-modal');
const chatClose = document.getElementById('chat-close');
const chatMessages = document.getElementById('chat-messages');
const chatInput = document.getElementById('chat-input');
const chatSend = document.getElementById('chat-send');

chatButton.addEventListener('click', () => {
    chatModal.classList.toggle('show');
    if (chatModal.classList.contains('show')) {
        chatInput.focus();
    }
});

chatClose.addEventListener('click', () => {
    chatModal.classList.remove('show');
});

async function sendMessage() {
    const query = chatInput.value.trim();
    if (!query) return;

    // Disable input
    chatInput.disabled = true;
    chatSend.disabled = true;
    chatInput.value = '';

    // Add user message
    const userMessage = document.createElement('div');
    userMessage.className = 'chat-message user';
    userMessage.textContent = query;
    chatMessages.appendChild(userMessage);

    // Add typing indicator
    const typingDiv = document.createElement('div');
    typingDiv.className = 'chat-message assistant typing-indicator';
    typingDiv.innerHTML = '<span></span><span></span><span></span>';
    chatMessages.appendChild(typingDiv);
    chatMessages.scrollTop = chatMessages.scrollHeight;

    try {
        const response = await fetch('/api/query', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ query })
        });

        const result = await response.json();
        
        // Remove typing indicator
        chatMessages.removeChild(typingDiv);

        // Add assistant response
        const assistantMessage = document.createElement('div');
        
        if (result.success) {
            assistantMessage.className = 'chat-message assistant markdown-body';
            assistantMessage.innerHTML = renderMarkdown(result.answer);
        } else {
            assistantMessage.className = 'chat-message error';
            assistantMessage.textContent = result.error || 'An error occurred';
        }
        
        chatMessages.appendChild(assistantMessage);
    } catch (error) {
        // Remove typing indicator
        chatMessages.removeChild(typingDiv);
        
        // Show error
        const errorMessage = document.createElement('div');
        errorMessage.className = 'chat-message error';
        errorMessage.textContent = 'Failed to connect to server: ' + error.message;
        chatMessages.appendChild(errorMessage);
    } finally {
        // Re-enable input
        chatInput.disabled = false;
        chatSend.disabled = false;
        chatInput.focus();
        chatMessages.scrollTop = chatMessages.scrollHeight;
    }
}

chatSend.addEventListener('click', sendMessage);
chatInput.addEventListener('keypress', (e) => {
    if (e.key === 'Enter' && !e.shiftKey) {
        e.preventDefault();
        sendMessage();
    }
});
