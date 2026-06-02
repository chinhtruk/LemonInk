(function () {
    'use strict';

    const form = document.querySelector('[data-review-form]');
    const commentInput = document.querySelector('[data-review-comment-input]');
    const actionMenus = Array.from(document.querySelectorAll('[data-review-actions]'));
    const filter = document.querySelector('[data-review-filter]');
    const filterTrigger = document.querySelector('[data-review-filter-trigger]');
    const filterLabel = document.querySelector('[data-review-filter-label]');
    const filterOptions = Array.from(document.querySelectorAll('[data-review-filter-option]'));
    const reviewList = document.querySelector('.zen-book-review-list');
    const filterEmpty = document.querySelector('[data-review-filter-empty]');

    if (form) {
        form.reset();
    }

    function closeAll(except) {
        actionMenus.forEach(function (menu) {
            if (menu === except) return;
            menu.classList.remove('is-open');
            const trigger = menu.querySelector('[data-review-action-trigger]');
            if (trigger) trigger.setAttribute('aria-expanded', 'false');
        });
    }

    function closeFilter() {
        if (!filter || !filterTrigger) return;
        filter.classList.remove('is-open');
        filterTrigger.setAttribute('aria-expanded', 'false');
    }

    actionMenus.forEach(function (menu) {
        const trigger = menu.querySelector('[data-review-action-trigger]');
        if (!trigger) return;

        trigger.addEventListener('click', function (event) {
            event.stopPropagation();
            const shouldOpen = !menu.classList.contains('is-open');
            closeAll(menu);
            closeFilter();
            menu.classList.toggle('is-open', shouldOpen);
            trigger.setAttribute('aria-expanded', shouldOpen ? 'true' : 'false');
        });
    });

    if (filter && filterTrigger) {
        filterTrigger.addEventListener('click', function (event) {
            event.stopPropagation();
            const shouldOpen = !filter.classList.contains('is-open');
            closeAll();
            filter.classList.toggle('is-open', shouldOpen);
            filterTrigger.setAttribute('aria-expanded', shouldOpen ? 'true' : 'false');
        });
    }

    document.addEventListener('click', function (event) {
        actionMenus.forEach(function (menu) {
            if (!menu.contains(event.target)) {
                menu.classList.remove('is-open');
                const trigger = menu.querySelector('[data-review-action-trigger]');
                if (trigger) trigger.setAttribute('aria-expanded', 'false');
            }
        });

        if (filter && !filter.contains(event.target)) {
            closeFilter();
        }
    });

    document.addEventListener('keydown', function (event) {
        if (event.key === 'Escape') {
            closeAll();
            closeFilter();
        }
    });

    function applyReviewFilter(value, label) {
        if (!reviewList) return;

        const items = Array.from(reviewList.querySelectorAll('[data-review-item]'));
        const visibleItems = items.filter(function (item) {
            return value !== 'with-comment' || item.dataset.reviewHasComment === 'true';
        });

        visibleItems.sort(function (a, b) {
            const ratingA = Number(a.dataset.reviewRating || 0);
            const ratingB = Number(b.dataset.reviewRating || 0);
            const dateA = Date.parse(a.dataset.reviewDate || '') || 0;
            const dateB = Date.parse(b.dataset.reviewDate || '') || 0;

            if (value === 'oldest') return dateA - dateB;
            if (value === 'highest') return ratingB - ratingA || dateB - dateA;
            if (value === 'lowest') return ratingA - ratingB || dateB - dateA;
            return dateB - dateA;
        });

        items.forEach(function (item) {
            item.hidden = !visibleItems.includes(item);
        });

        visibleItems.forEach(function (item) {
            reviewList.append(item);
        });

        if (filterEmpty) {
            filterEmpty.hidden = visibleItems.length > 0 || items.length === 0;
            if (!filterEmpty.hidden) {
                reviewList.append(filterEmpty);
            }
        }

        if (filterLabel) {
            filterLabel.textContent = label;
        }

        filterOptions.forEach(function (option) {
            option.classList.toggle('is-active', option.dataset.reviewFilterValue === value);
        });

        closeFilter();
    }

    filterOptions.forEach(function (option) {
        option.addEventListener('click', function () {
            applyReviewFilter(option.dataset.reviewFilterValue || 'newest', option.textContent.trim());
        });
    });

    document.querySelectorAll('[data-review-edit]').forEach(function (button) {
        button.addEventListener('click', function () {
            if (!form || !commentInput) return;

            const rating = button.dataset.reviewRating;
            const comment = button.dataset.reviewComment || '';
            commentInput.value = comment;

            const ratingInput = form.querySelector(`[data-review-rating-input][value="${rating}"]`);
            if (ratingInput) ratingInput.checked = true;

            closeAll();
            form.scrollIntoView({ behavior: 'smooth', block: 'center' });
            window.setTimeout(function () {
                commentInput.focus();
            }, 260);

            if (window.LemonInkToast) {
                window.LemonInkToast.show('Bạn có thể chỉnh đánh giá rồi gửi lại.', 'info', { duration: 2800 });
            }
        });
    });

    document.querySelectorAll('[data-review-delete]').forEach(function (button) {
        button.addEventListener('click', function (event) {
            if (!window.confirm('Xoá đánh giá này?')) {
                event.preventDefault();
            }
        });
    });
})();
