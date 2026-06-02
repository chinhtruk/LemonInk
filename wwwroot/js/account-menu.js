/* ============================================
   LemonInk Account Menu
   ============================================ */
(function () {
    'use strict';

    const menus = Array.from(document.querySelectorAll('[data-account-menu]'));
    if (!menus.length) return;

    function closeMenu(menu) {
        const trigger = menu.querySelector('[data-account-trigger]');
        menu.classList.remove('is-open');
        if (trigger) trigger.setAttribute('aria-expanded', 'false');
    }

    function closeAll(except) {
        menus.forEach(function (menu) {
            if (menu !== except) closeMenu(menu);
        });
    }

    menus.forEach(function (menu) {
        const trigger = menu.querySelector('[data-account-trigger]');
        if (!trigger) return;

        trigger.addEventListener('click', function () {
            const shouldOpen = !menu.classList.contains('is-open');
            closeAll(menu);
            menu.classList.toggle('is-open', shouldOpen);
            trigger.setAttribute('aria-expanded', shouldOpen ? 'true' : 'false');
        });
    });

    document.addEventListener('click', function (event) {
        menus.forEach(function (menu) {
            if (!menu.contains(event.target)) closeMenu(menu);
        });
    });

    document.addEventListener('keydown', function (event) {
        if (event.key !== 'Escape') return;
        closeAll();
    });
})();
