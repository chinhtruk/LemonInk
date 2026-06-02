/* ============================================
   LemonInk Bookmark Interactions
   ============================================ */
(function () {
    'use strict';

    const gridCards = Array.from(document.querySelectorAll('[data-bookmark-card]'));
    if (!gridCards.length) return;

    const searchInput = document.querySelector('[data-bookmark-search]');
    const filterSelect = document.querySelector('[data-bookmark-filter-select]');
    const sortSelect = document.querySelector('[data-bookmark-sort]');
    const resetButtons = Array.from(document.querySelectorAll('[data-bookmark-reset]'));
    const countEl = document.querySelector('[data-bookmark-count]');
    const emptyEl = document.querySelector('[data-bookmark-empty]');
    const sections = Array.from(document.querySelectorAll('[data-bookmark-section]'));
    const searchTools = window.LemonInkSearch || {};
    const debounce = searchTools.debounce || function (fn, wait) {
        let timer = 0;
        return function () {
            window.clearTimeout(timer);
            timer = window.setTimeout(fn, wait || 240);
        };
    };

    let activeFilter = 'Tất cả';

    function normalize(value) {
        if (searchTools.normalize) {
            return searchTools.normalize(value);
        }

        return (value || '').toString().trim().toLocaleLowerCase('vi-VN');
    }

    function getSearchTerm() {
        return normalize(searchInput ? searchInput.value : '');
    }

    function getRawSearchTerm() {
        return searchInput ? searchInput.value.trim() : '';
    }

    function matchesCard(card, searchTerm) {
        const haystack = normalize([
            card.dataset.bookmarkTitle,
            card.dataset.bookmarkAuthor,
            card.dataset.bookmarkCategory,
            card.dataset.bookmarkStatus
        ].join(' '));

        const matchesSearch = !searchTerm || haystack.includes(searchTerm);
        const status = card.dataset.bookmarkStatus || '';
        const hasAudio = card.dataset.bookmarkAudio === 'true';
        const hasNotes = card.dataset.bookmarkNotes === 'true';

        let matchesFilter = true;
        if (activeFilter === 'Đang đọc') {
            matchesFilter = status === 'Đang đọc';
        } else if (activeFilter === 'Có audio') {
            matchesFilter = hasAudio;
        } else if (activeFilter === 'Có ghi chú') {
            matchesFilter = hasNotes;
        } else if (activeFilter === 'Đã đọc xong') {
            matchesFilter = status === 'Đã đọc xong';
        }

        return matchesSearch && matchesFilter;
    }

    function sortCards(visibleCards) {
        const sortValue = sortSelect ? sortSelect.value : 'default';

        return visibleCards.sort(function (a, b) {
            if (sortValue === 'progress-desc') {
                return Number(b.dataset.bookmarkProgress) - Number(a.dataset.bookmarkProgress);
            }
            if (sortValue === 'title-asc') {
                return (a.dataset.bookmarkTitle || '').localeCompare(b.dataset.bookmarkTitle || '', 'vi');
            }
            return Number(a.dataset.bookmarkOrder) - Number(b.dataset.bookmarkOrder);
        });
    }

    function renderBookmarks() {
        const searchTerm = getSearchTerm();
        const visibleCards = sortCards(gridCards.filter(function (card) {
            return matchesCard(card, searchTerm);
        }));

        gridCards.forEach(function (card) {
            card.hidden = true;
        });

        visibleCards.forEach(function (card) {
            card.hidden = false;
            card.parentElement && card.parentElement.appendChild(card);
        });

        sections.forEach(function (section) {
            const visibleSectionCards = Array.from(section.querySelectorAll('[data-bookmark-card]')).filter(function (card) {
                return !card.hidden;
            });
            section.hidden = visibleSectionCards.length === 0;
        });

        if (countEl) {
            countEl.textContent = visibleCards.length + ' mục';
        }

        if (emptyEl) {
            emptyEl.hidden = visibleCards.length > 0;
        }

        if (searchTools.highlight) {
            searchTools.highlight(document, getRawSearchTerm(), '.zen-book-card-title, .zen-book-card-author, .zen-book-card-category, .zen-bookmark-feature-body h3, .zen-bookmark-feature-body p');
        }
    }

    function resetBookmarks() {
        activeFilter = 'Tất cả';
        if (searchInput) {
            searchInput.value = '';
        }
        if (sortSelect) {
            sortSelect.value = 'default';
        }
        if (filterSelect) {
            filterSelect.value = 'Tất cả';
        }
        renderBookmarks();
    }

    if (searchInput) {
        searchInput.addEventListener('input', debounce(renderBookmarks, 220));
    }

    if (filterSelect) {
        filterSelect.addEventListener('change', function () {
            activeFilter = filterSelect.value || 'Tất cả';
            renderBookmarks();
        });
    }

    if (sortSelect) {
        sortSelect.addEventListener('change', renderBookmarks);
    }

    resetButtons.forEach(function (button) {
        button.addEventListener('click', resetBookmarks);
    });

    renderBookmarks();
})();
