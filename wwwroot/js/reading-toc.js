(function () {
    'use strict';

    const links = Array.from(document.querySelectorAll('[data-reading-toc-link]'));
    if (!links.length) return;

    const sections = links
        .map((link) => {
            const href = link.getAttribute('href') || '';
            return href.startsWith('#') ? document.querySelector(href) : null;
        })
        .filter(Boolean);

    if (!sections.length) return;

    const list = document.querySelector('.zen-reading-toc-list');
    let activeSectionId = '';
    let scrollFrame = 0;

    const revealActiveLink = (link) => {
        if (!list || !link || list.scrollHeight <= list.clientHeight) return;

        const prefersReducedMotion = window.matchMedia('(prefers-reduced-motion: reduce)').matches;
        const targetTop = link.offsetTop - (list.clientHeight / 2) + (link.offsetHeight / 2);
        const maxTop = Math.max(0, list.scrollHeight - list.clientHeight);

        list.scrollTo({
            top: Math.min(maxTop, Math.max(0, targetTop)),
            behavior: prefersReducedMotion ? 'auto' : 'smooth'
        });
    };

    const setActive = (id) => {
        if (!id || activeSectionId === id) return;

        activeSectionId = id;
        let activeLink = null;

        links.forEach((link) => {
            const isActive = link.getAttribute('href') === `#${id}`;
            link.classList.toggle('is-active', isActive);
            if (isActive) {
                link.setAttribute('aria-current', 'true');
            } else {
                link.removeAttribute('aria-current');
            }
            if (isActive) activeLink = link;
        });

        revealActiveLink(activeLink);
    };

    const rail = document.querySelector('.zen-reading-toc-rail');

    const updateFromScroll = () => {
        const probeY = window.scrollY + Math.min(window.innerHeight * 0.38, 360);
        let current = sections[0];

        for (const section of sections) {
            if (section.offsetTop <= probeY) current = section;
        }

        if (current) setActive(current.id);
    };

    if ('IntersectionObserver' in window) {
        // Highlighting observer
        const sectionObserver = new IntersectionObserver((entries) => {
            const visible = entries
                .filter((entry) => entry.isIntersecting)
                .sort((a, b) => b.intersectionRatio - a.intersectionRatio)[0];

            if (visible) setActive(visible.target.id);
        }, {
            rootMargin: '-20% 0px -60% 0px',
            threshold: [0.1]
        });

        sections.forEach((section) => sectionObserver.observe(section));

        // Hide-at-end observer (Footer watching)
        const footer = document.querySelector('.zen-footer');
        if (footer && rail) {
            const footerObserver = new IntersectionObserver((entries) => {
                const isFooterVisible = entries[0].isIntersecting;
                rail.classList.toggle('is-hidden', isFooterVisible);
            }, {
                rootMargin: '100px 0px 0px 0px', // Trigger slightly before footer
                threshold: 0
            });
            footerObserver.observe(footer);
        }
    }

    window.addEventListener('scroll', () => {
        if (scrollFrame) return;

        scrollFrame = window.requestAnimationFrame(() => {
            scrollFrame = 0;
            updateFromScroll();
        });
    }, { passive: true });
    updateFromScroll();
})();
