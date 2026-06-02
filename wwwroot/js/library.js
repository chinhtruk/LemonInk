/* ============================================
   LemonInk Library Interactions
   ============================================ */
(function () {
    'use strict';

    const grid = document.querySelector('[data-library-grid]');
    if (!grid) return;

    const endpoint = grid.dataset.libraryEndpoint || '/Books/Search';
    const searchInputs = Array.from(document.querySelectorAll('[data-library-search]'));
    const searchForms = Array.from(document.querySelectorAll('[data-library-search-form]'));
    const categorySelect = document.querySelector('[data-library-category-select]');
    const sortSelect = document.querySelector('[data-library-sort]');
    const sourceSelect = document.querySelector('[data-library-source]');
    const statusSelect = document.querySelector('[data-library-status]');
    const resetButton = document.querySelector('[data-library-reset]');
    const countEl = document.querySelector('[data-library-count]');
    const emptyEl = document.querySelector('[data-library-empty]');
    const paginationEl = document.querySelector('[data-library-pagination]');
    const pageSize = 12;
    const searchTools = window.LemonInkSearch || {};
    const debounce = searchTools.debounce || function (fn, wait) {
        let timer = 0;
        return function () {
            window.clearTimeout(timer);
            timer = window.setTimeout(fn, wait || 240);
        };
    };

    let activeCategory = categorySelect ? (categorySelect.value || 'Tất cả') : 'Tất cả';
    let currentRequest = null;
    let currentPage = 1;
    let currentCards = Array.from(grid.querySelectorAll('[data-library-card]'));

    function getSearchValue() {
        const input = searchInputs.find(function (item) {
            return item.value.trim().length > 0;
        }) || searchInputs[0];

        return input ? input.value.trim() : '';
    }

    function syncSearchInputs(source) {
        searchInputs.forEach(function (input) {
            if (input !== source) input.value = source.value;
        });
    }

    function buildUrl() {
        const params = new URLSearchParams();
        const searchValue = getSearchValue();

        if (searchValue) params.set('query', searchValue);
        if (activeCategory !== 'Tất cả') params.set('category', activeCategory);
        if (sortSelect && sortSelect.value !== 'default') params.set('sort', sortSelect.value);
        if (sourceSelect && sourceSelect.value !== 'all') params.set('source', sourceSelect.value);
        if (statusSelect && statusSelect.value !== 'all') params.set('status', statusSelect.value);

        const suffix = params.toString();
        return suffix ? endpoint + '?' + suffix : endpoint;
    }

    function formatRating(book) {
        if (!book.canRead) return 'Đang xử lý';
        if (!book.hasRating) return 'Chưa có đánh giá';

        const rating = Number(book.rating || 0);
        return rating.toFixed(1) + '/5';
    }

    function createTextEl(tagName, className, text) {
        const el = document.createElement(tagName);
        el.className = className;
        el.textContent = text || '';
        return el;
    }

    function createBookCard(book, index) {
        const card = document.createElement('a');
        card.href = book.canRead ? (book.readUrl || '/Books/Detail/' + book.id) : '/Books/Processing';
        card.className = 'zen-book-card';
        card.dataset.libraryCard = '';
        card.dataset.title = book.title || '';
        card.dataset.author = book.author || '';
        card.dataset.category = book.category || '';
        card.dataset.source = book.source || '';
        card.dataset.status = book.status || '';
        card.dataset.rating = String(book.rating || 0);
        card.dataset.hasRating = String(Boolean(book.hasRating));
        card.dataset.readingTime = String(book.readingTimeMinutes || 0);
        card.dataset.index = String(index);

        const cover = document.createElement('div');
        cover.className = 'zen-book-card-cover';
        cover.style.background = book.coverGradient || 'linear-gradient(135deg, #fff7d1 0%, #f8b84e 100%)';

        const image = document.createElement('img');
        image.src = book.coverUrl || '/images/book-cover-thinking-fast-and-slow.svg';
        image.alt = 'Bìa sách ' + (book.title || '');
        image.className = 'zen-book-card-cover-image';
        image.loading = 'lazy';
        cover.appendChild(image);

        const meta = document.createElement('div');
        meta.className = 'zen-book-card-meta';
        meta.appendChild(createTextEl('span', '', (book.readingTimeMinutes || 0) + ' phút đọc'));
        meta.appendChild(createTextEl('span', '', formatRating(book)));

        card.appendChild(cover);
        card.appendChild(createTextEl('span', 'zen-book-card-category', book.category || 'Chưa phân loại'));
        card.appendChild(createTextEl('h3', 'zen-book-card-title', book.title || 'Chưa có tiêu đề'));
        card.appendChild(createTextEl('p', 'zen-book-card-author', book.author || 'Chưa rõ tác giả'));
        card.appendChild(meta);

        return card;
    }

    function createPaginationButton(label, page, options) {
        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'zen-library-page-btn';
        if (options && options.isIcon) {
            button.innerHTML = label;
            button.classList.add('is-icon');
        } else {
            button.textContent = label;
        }
        button.dataset.libraryPage = String(page);

        if (options && options.active) {
            button.classList.add('is-active');
            button.setAttribute('aria-current', 'page');
        }

        if (options && options.disabled) {
            button.disabled = true;
        }

        return button;
    }

    function renderPagination(totalItems) {
        if (!paginationEl) return;

        const totalPages = Math.ceil(totalItems / pageSize);
        paginationEl.innerHTML = '';
        paginationEl.hidden = totalPages <= 1;

        if (totalPages <= 1) return;

        const start = (currentPage - 1) * pageSize + 1;
        const end = Math.min(currentPage * pageSize, totalItems);

        const summary = document.createElement('span');
        summary.className = 'zen-library-page-summary';
        summary.textContent = `Hiển thị ${start}-${end} trong ${totalItems}`;

        const controls = document.createElement('div');
        controls.className = 'zen-library-page-controls';

        const prevIcon = `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="15 18 9 12 15 6"></polyline></svg>`;
        controls.appendChild(createPaginationButton(prevIcon, Math.max(1, currentPage - 1), {
            disabled: currentPage === 1,
            isIcon: true
        }));

        for (let page = 1; page <= totalPages; page += 1) {
            controls.appendChild(createPaginationButton(String(page), page, {
                active: page === currentPage
            }));
        }

        const nextIcon = `<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><polyline points="9 18 15 12 9 6"></polyline></svg>`;
        controls.appendChild(createPaginationButton(nextIcon, Math.min(totalPages, currentPage + 1), {
            disabled: currentPage === totalPages,
            isIcon: true
        }));

        paginationEl.appendChild(summary);
        paginationEl.appendChild(controls);
    }

    function renderCurrentPage() {
        const totalItems = currentCards.length;
        const totalPages = Math.max(1, Math.ceil(totalItems / pageSize));
        currentPage = Math.min(Math.max(1, currentPage), totalPages);

        const start = (currentPage - 1) * pageSize;
        const pageCards = currentCards.slice(start, start + pageSize);

        grid.innerHTML = '';
        pageCards.forEach(function (card) {
            grid.appendChild(card);
        });

        if (emptyEl) {
            emptyEl.hidden = totalItems > 0;
        }

        renderPagination(totalItems);
        if (searchTools.highlight) {
            searchTools.highlight(grid, getSearchValue(), '.zen-book-card-title, .zen-book-card-author, .zen-book-card-category');
        }
    }

    function renderBooks(books) {
        currentCards = books.map(createBookCard);
        currentPage = 1;
        renderCurrentPage();
    }

    function syncCategoryState() {
        if (categorySelect) categorySelect.value = activeCategory;
    }

    function renderCategories(categories) {
        if (!categorySelect || !Array.isArray(categories) || categories.length === 0) return;

        if (!categories.includes(activeCategory)) {
            activeCategory = 'Tất cả';
        }

        categorySelect.innerHTML = '';
        categories.forEach(function (category) {
            const option = document.createElement('option');
            option.value = category;
            option.textContent = category;
            categorySelect.appendChild(option);
        });

        syncCategoryState();
    }

    function setLoading(isLoading) {
        grid.classList.toggle('is-loading', isLoading);
    }

    async function fetchBooks() {
        if (currentRequest) {
            currentRequest.abort();
        }

        currentRequest = new AbortController();
        setLoading(true);

        try {
            const response = await fetch(buildUrl(), {
                headers: { 'Accept': 'application/json' },
                signal: currentRequest.signal
            });

            if (!response.ok) throw new Error('Search request failed');

            const result = await response.json();
            const books = Array.isArray(result.books) ? result.books : [];
            renderCategories(result.categories || []);
            renderBooks(books);

            if (countEl) {
                countEl.textContent = (result.filteredCount || books.length) + ' cuốn sách';
            }
        } catch (error) {
            if (error.name !== 'AbortError') {
                renderBooks([]);
                if (countEl) countEl.textContent = '0 cuốn sách';
            }
        } finally {
            setLoading(false);
        }
    }

    const scheduleFetch = debounce(fetchBooks, 240);

    function resetLibrary() {
        activeCategory = 'Tất cả';

        searchInputs.forEach(function (input) {
            input.value = '';
        });

        if (categorySelect) categorySelect.value = 'Tất cả';
        if (sortSelect) sortSelect.value = 'default';
        if (sourceSelect) sourceSelect.value = 'all';
        if (statusSelect) statusSelect.value = 'all';

        syncCategoryState();
        fetchBooks();
    }

    searchInputs.forEach(function (input) {
        input.addEventListener('input', function () {
            syncSearchInputs(input);
            scheduleFetch();
        });
    });

    searchForms.forEach(function (form) {
        form.addEventListener('submit', function (event) {
            event.preventDefault();
            fetchBooks();
        });
    });

    [categorySelect, sortSelect, sourceSelect, statusSelect].forEach(function (select) {
        if (select) select.addEventListener('change', function(event) {
            if (select === categorySelect) activeCategory = categorySelect.value || 'Tất cả';
            fetchBooks();
        });
    });

    if (resetButton) {
        resetButton.addEventListener('click', resetLibrary);
    }

    if (paginationEl) {
        paginationEl.addEventListener('click', function (event) {
            const button = event.target.closest('[data-library-page]');
            if (!button || button.disabled) return;

            currentPage = Number(button.dataset.libraryPage || 1);
            renderCurrentPage();
            grid.scrollIntoView({ behavior: 'smooth', block: 'start' });
        });
    }

    syncCategoryState();
    renderCurrentPage();
})();
