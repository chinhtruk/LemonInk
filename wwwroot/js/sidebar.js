/* ============================================
   ZenRead — Sidebar Collapse/Expand
   ============================================ */
(function () {
    'use strict';

    var sidebar = document.getElementById('zenSidebar');
    var toggleBtn = document.getElementById('zenSidebarToggle');

    if (!sidebar || !toggleBtn) return;

    // Restore state from localStorage
    var saved = localStorage.getItem('zen-sidebar-collapsed');
    if (saved === 'true') {
        sidebar.classList.add('is-collapsed');
    }

    toggleBtn.addEventListener('click', function () {
        sidebar.classList.toggle('is-collapsed');
        localStorage.setItem('zen-sidebar-collapsed', sidebar.classList.contains('is-collapsed'));
    });
})();
