/* ============================================
   ZenRead — Reading Progress Bar
   ============================================ */
(function () {
    'use strict';

    const progressBar = document.getElementById('zenProgressBar');
    if (!progressBar) return;

    let ticking = false;

    function updateProgress() {
        const scrollTop = window.scrollY;
        const docHeight = document.documentElement.scrollHeight - window.innerHeight;
        const progress = docHeight > 0 ? (scrollTop / docHeight) * 100 : 0;
        progressBar.style.width = Math.min(progress, 100) + '%';
        ticking = false;
    }

    window.addEventListener('scroll', function () {
        if (!ticking) {
            requestAnimationFrame(updateProgress);
            ticking = true;
        }
    }, { passive: true });

    // Initial call
    updateProgress();
})();
