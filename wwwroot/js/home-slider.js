/* ============================================
   LemonInk Home Slider
   ============================================ */
(function () {
    'use strict';

    const sliders = Array.from(document.querySelectorAll('[data-home-slider]'));
    if (!sliders.length) return;

    sliders.forEach(function (slider) {
        const track = slider.querySelector('[data-home-slider-track]');
        if (!track) return;

        slider.querySelectorAll('[data-home-slider-control]').forEach(function (button) {
            button.addEventListener('click', function () {
                const direction = button.dataset.homeSliderControl === 'next' ? 1 : -1;
                const firstCard = track.querySelector('.zen-book-card');
                const cardWidth = firstCard ? firstCard.getBoundingClientRect().width : 280;
                track.scrollBy({
                    left: direction * (cardWidth + 24),
                    behavior: 'smooth'
                });
            });
        });
    });
})();
