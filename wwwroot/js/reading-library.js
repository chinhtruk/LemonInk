/* ============================================
   LemonInk — Reading bookmark and progress
   ============================================ */
(function () {
    'use strict';

    const bookmarkButton = document.querySelector('[data-bookmark-toggle]');
    const readingMain = document.querySelector('.zen-reading-main');
    const chapterSections = Array.from(document.querySelectorAll('[data-summary-section-id]'));
    const bookId = bookmarkButton ? Number.parseInt(bookmarkButton.dataset.bookId || '', 10) : NaN;

    if (!Number.isFinite(bookId)) return;

    let lastSavedPercent = -1;
    let lastSavedAt = 0;
    let isBookmarkBusy = false;

    function updateBookmarkButton(isBookmarked) {
        if (!bookmarkButton) return;

        const label = bookmarkButton.querySelector('[data-bookmark-label]');
        bookmarkButton.classList.toggle('is-saved', isBookmarked);
        bookmarkButton.setAttribute('aria-pressed', isBookmarked ? 'true' : 'false');
        bookmarkButton.title = isBookmarked ? 'Bỏ đánh dấu sách này' : 'Lưu sách vào danh sách đánh dấu';

        if (label) {
            label.textContent = isBookmarked ? 'Đã đánh dấu' : 'Đánh dấu';
        }
    }

    async function loadBookmarkStatus() {
        if (!bookmarkButton) return;

        try {
            const response = await fetch('/Bookmarks/Status/' + encodeURIComponent(bookId), {
                headers: { 'Accept': 'application/json' }
            });

            if (!response.ok || response.redirected) return;

            const payload = await response.json();
            updateBookmarkButton(Boolean(payload.isBookmarked));
        } catch {
            // Bookmark state is optional; keep the default button state if unavailable.
        }
    }

    async function toggleBookmark() {
        if (!bookmarkButton || isBookmarkBusy) return;

        isBookmarkBusy = true;
        bookmarkButton.disabled = true;

        try {
            const response = await fetch('/Bookmarks/Toggle/' + encodeURIComponent(bookId), {
                method: 'POST',
                headers: { 'Accept': 'application/json' }
            });

            if (response.status === 401 || response.redirected) {
                window.location.href = '/Account/Login';
                return;
            }

            const payload = await response.json().catch(function () { return {}; });
            if (response.ok) {
                updateBookmarkButton(Boolean(payload.isBookmarked));
            }
        } finally {
            isBookmarkBusy = false;
            bookmarkButton.disabled = false;
        }
    }

    function resolveProgressPayload() {
        const target = readingMain || document.documentElement;
        const start = target.offsetTop || 0;
        const height = Math.max(1, target.scrollHeight - window.innerHeight);
        const current = Math.max(0, window.scrollY - start);
        const percent = Math.max(0, Math.min(100, Math.round((current / height) * 100)));
        const activeSection = resolveActiveSection();

        return {
            bookId: bookId,
            summarySectionId: activeSection ? Number.parseInt(activeSection.dataset.summarySectionId || '', 10) || null : null,
            progressPercent: percent,
            lastPosition: activeSection ? '#' + activeSection.id : window.location.hash || null
        };
    }

    function resolveActiveSection() {
        if (!chapterSections.length) return null;

        const probeY = window.scrollY + 160;
        let active = chapterSections[0];

        chapterSections.forEach(function (section) {
            if (section.offsetTop <= probeY) {
                active = section;
            }
        });

        return active;
    }

    async function saveProgress(force) {
        const now = Date.now();
        const payload = resolveProgressPayload();

        if (!force &&
            Math.abs(payload.progressPercent - lastSavedPercent) < 5 &&
            now - lastSavedAt < 12000) {
            return;
        }

        lastSavedPercent = payload.progressPercent;
        lastSavedAt = now;

        try {
            await fetch('/ReadingProgress/Update', {
                method: 'POST',
                headers: {
                    'Accept': 'application/json',
                    'Content-Type': 'application/json'
                },
                body: JSON.stringify(payload),
                keepalive: true
            });
        } catch {
            // Progress saving should never interrupt reading.
        }
    }

    let progressTimer = null;
    function scheduleProgressSave() {
        if (progressTimer) return;

        progressTimer = window.setTimeout(function () {
            progressTimer = null;
            saveProgress(false);
        }, 900);
    }

    if (bookmarkButton) {
        bookmarkButton.addEventListener('click', toggleBookmark);
        loadBookmarkStatus();
    }

    window.addEventListener('scroll', scheduleProgressSave, { passive: true });
    window.addEventListener('pagehide', function () {
        saveProgress(true);
    });

    saveProgress(false);
})();
