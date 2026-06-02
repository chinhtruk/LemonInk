/* ============================================
   LemonInk — Reading audio realtime status
   ============================================ */
(function () {
    'use strict';

    const root = document.querySelector('[data-reading-audio-status]');
    if (!root) return;

    const bookId = root.dataset.bookId;
    if (!bookId) return;

    const pollInterval = 4500;
    const slowPollInterval = 12000;
    let polling = root.dataset.audioReady !== 'true';
    let warned = false;

    function escapeHtml(value) {
        return String(value ?? '')
            .replaceAll('&', '&amp;')
            .replaceAll('<', '&lt;')
            .replaceAll('>', '&gt;')
            .replaceAll('"', '&quot;')
            .replaceAll("'", '&#039;');
    }

    function formatTime(value) {
        if (window.LemonInkAudioPlayer && typeof window.LemonInkAudioPlayer.formatTime === 'function') {
            return window.LemonInkAudioPlayer.formatTime(value);
        }

        const seconds = Math.max(0, Math.floor(value || 0));
        const minutes = Math.floor(seconds / 60);
        const secs = seconds % 60;
        return String(minutes).padStart(2, '0') + ':' + String(secs).padStart(2, '0');
    }

    function showToast(message, type) {
        if (window.LemonInkToast && typeof window.LemonInkToast.show === 'function') {
            window.LemonInkToast.show(message, type || 'info');
        }
    }

    function renderAudioButton() {
        const button = document.querySelector('[data-reading-audio-button]');
        if (!button) return;

        button.disabled = false;
        button.title = '';
        button.classList.remove('zen-btn-ghost', 'is-audio-pending');
        button.classList.add('zen-btn-primary');
        button.setAttribute('data-audio-toggle', '');

        const text = button.querySelector('[data-reading-audio-button-text]');
        if (text) {
            text.textContent = 'Nghe Audio';
        }

        button.innerHTML = `
            <svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"><polygon points="5 3 19 12 5 21 5 3"/></svg>
            <span data-reading-audio-button-text>Nghe Audio</span>
        `;
    }

    function updatePendingAudioStatus(status) {
        const panel = document.querySelector('[data-reading-audio-status-panel]');
        const statusText = document.querySelector('[data-reading-audio-status-text]');
        const statusProgress = document.querySelector('[data-reading-audio-status-progress]');
        const button = document.querySelector('[data-reading-audio-button]');
        const buttonText = document.querySelector('[data-reading-audio-button-text]');
        const metaText = document.querySelector('[data-reading-audio-meta-text]');

        if (panel) {
            panel.classList.remove('is-hidden', 'is-error');
        }

        const progress = Number(status && status.progress);
        const hasProgress = Number.isFinite(progress) && progress > 0;
        const state = String(status && (status.processingStatus || status.latestJobStatus) || '').toLowerCase();
        const step = String(status && status.step || '').trim();
        const estimated = String(status && status.estimatedTime || '').trim();
        const failedReason = String(status && status.failedReason || '').trim();

        let message = 'Audio đang được tạo và sẽ tự xuất hiện khi sẵn sàng.';

        if (state.includes('failed')) {
            message = failedReason || 'Audio chưa tạo được. LemonInk sẽ chờ bạn retry hoặc tạo lại sau.';
            if (panel) panel.classList.add('is-error');
            if (buttonText) buttonText.textContent = 'Audio chưa sẵn sàng';
        } else if (step) {
            message = step;
            if (buttonText) buttonText.textContent = state.includes('generatingaudio') ? 'Đang tạo audio' : 'Audio đang xử lý';
        } else if (state.includes('summaryready')) {
            message = 'Bản tóm tắt đã sẵn sàng, audio đang chờ đến lượt tạo.';
            if (buttonText) buttonText.textContent = 'Audio đang chờ';
        } else if (state.includes('generatingaudio')) {
            message = 'LemonInk đang tạo audio từ bản tóm tắt.';
            if (buttonText) buttonText.textContent = 'Đang tạo audio';
        }

        if (estimated && !state.includes('failed')) {
            message += ` ${estimated}.`;
        }

        if (statusText) statusText.textContent = message;
        if (statusProgress) statusProgress.textContent = hasProgress ? `${Math.round(progress)}%` : '';
        if (metaText && root.dataset.audioReady !== 'true') {
            metaText.textContent = hasProgress ? `Audio đang xử lý ${Math.round(progress)}%` : 'Audio đang xử lý';
        }

        if (button) {
            button.disabled = true;
            button.removeAttribute('data-audio-toggle');
            button.classList.add('is-audio-pending');
            button.title = message;
        }
    }

    function hidePendingAudioStatus() {
        const panel = document.querySelector('[data-reading-audio-status-panel]');
        if (panel) {
            panel.classList.add('is-hidden');
            panel.classList.remove('is-error');
        }
    }

    function renderAudioPlayer(payload) {
        const mount = document.querySelector('[data-reading-audio-player-mount]');
        if (!mount || document.getElementById('zenAudioPlayer')) return;

        const durationSeconds = Math.max(1, Number(payload.audioDurationSeconds || 0));
        const durationText = payload.durationText || formatTime(durationSeconds);

        mount.innerHTML = `
            <div id="zenAudioPlayer" class="zen-audio-player" aria-live="polite" data-duration-seconds="${durationSeconds}">
                <audio id="zenAudioElement" src="${escapeHtml(payload.audioUrl)}" preload="metadata"></audio>
                <div class="zen-audio-transport" aria-label="Điều khiển audio">
                    <button id="zenAudioRewind" class="zen-audio-btn" aria-label="Tua lại 15 giây" title="Tua lại 15s" type="button">
                        <svg viewBox="0 0 24 24"><polygon points="11 19 2 12 11 5 11 19"/><polygon points="22 19 13 12 22 5 22 19"/></svg>
                    </button>
                    <button id="zenAudioPlayBtn" class="zen-audio-btn zen-audio-btn-play play-btn" aria-label="Phát / Tạm dừng" type="button">
                        <svg viewBox="0 0 24 24"><path d="M6 3l14 9-14 9V3z" fill="currentColor" stroke="none"/></svg>
                    </button>
                    <button id="zenAudioForward" class="zen-audio-btn" aria-label="Tua tới 15 giây" title="Tua tới 15s" type="button">
                        <svg viewBox="0 0 24 24"><polygon points="13 19 22 12 13 5 13 19"/><polygon points="2 19 11 12 2 5 2 19"/></svg>
                    </button>
                </div>
                <div class="zen-audio-info">
                    <img src="${escapeHtml(payload.coverUrl)}" alt="Bìa sách ${escapeHtml(payload.title)}" class="zen-audio-artwork" />
                    <div class="zen-audio-nowplaying">
                        <div class="zen-audio-row">
                            <div class="zen-audio-title">${escapeHtml(payload.title)}</div>
                            <div class="zen-audio-artist">${escapeHtml(payload.author)}</div>
                        </div>
                        <div class="zen-audio-progress-wrap">
                            <input id="zenAudioSeek" class="zen-audio-progress" type="range" min="0" max="${durationSeconds}" value="0" step="1" aria-label="Tiến trình audio" />
                            <div id="zenAudioProgressFill" class="zen-audio-progress-fill"></div>
                        </div>
                        <div class="zen-audio-time">
                            <span id="zenAudioCurrentTime">00:00</span>
                            <span id="zenAudioDuration">${escapeHtml(durationText)}</span>
                            <span id="zenAudioMinuteLabel" class="zen-audio-minute-label">Đã nghe 0 phút</span>
                        </div>
                    </div>
                </div>
                <div class="zen-audio-tools">
                    <label class="zen-audio-speed" title="Tốc độ phát">
                        <select id="zenAudioSpeed" aria-label="Tốc độ phát audio">
                            <option value="0.75">0.75x</option>
                            <option value="1" selected>1x</option>
                            <option value="1.25">1.25x</option>
                            <option value="1.5">1.5x</option>
                            <option value="2">2x</option>
                        </select>
                    </label>
                </div>
            </div>
        `;

        if (window.LemonInkAudioPlayer && typeof window.LemonInkAudioPlayer.init === 'function') {
            window.LemonInkAudioPlayer.init();
        }
    }

    function markAudioReady(payload) {
        if (!payload || !payload.isAudioReady || !payload.audioUrl) {
            return false;
        }

        root.dataset.audioReady = 'true';
        polling = false;

        const metaText = document.querySelector('[data-reading-audio-meta-text]');
        if (metaText && payload.readingTimeText) {
            metaText.textContent = payload.readingTimeText;
        }

        hidePendingAudioStatus();
        renderAudioButton();
        renderAudioPlayer(payload);
        showToast('Audio của sách đã sẵn sàng.', 'success');
        return true;
    }

    async function refreshProcessingStatus() {
        try {
            const response = await fetch(`/Books/ProcessingStatus/${bookId}?v=${Date.now()}`, {
                headers: { Accept: 'application/json' },
                credentials: 'same-origin'
            });

            if (!response.ok) return;

            const status = await response.json();
            updatePendingAudioStatus(status);
        } catch {
            // Reading pages can be public; lack of auth for processing status should not break audio polling.
        }
    }

    async function tick() {
        if (!polling) return;

        try {
            const response = await fetch(`/Books/ReadAudioStatus/${bookId}?v=${Date.now()}`, {
                headers: { Accept: 'application/json' },
                credentials: 'same-origin'
            });

            if (!response.ok) {
                throw new Error('Audio status request failed');
            }

            const payload = await response.json();
            warned = false;
            if (!markAudioReady(payload)) {
                await refreshProcessingStatus();
            }
        } catch {
            if (!warned) {
                warned = true;
                showToast('Chưa cập nhật được trạng thái audio. LemonInk sẽ tự thử lại.', 'warning');
            }
        } finally {
            if (polling) {
                window.setTimeout(tick, document.hidden ? slowPollInterval : pollInterval);
            }
        }
    }

    window.setTimeout(tick, 1200);
})();
