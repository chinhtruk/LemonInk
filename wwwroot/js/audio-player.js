/* ============================================
   LemonInk Audio Player
   ============================================ */
(function () {
    'use strict';

    const playPath = 'M6 3l14 9-14 9V3z';
    const pausePath = 'M6 4h4v16H6V4zm8 0h4v16h-4V4z';

    function initAudioPlayer() {
        const player = document.getElementById('zenAudioPlayer');
        const audio = document.getElementById('zenAudioElement');

        if (!player || !audio || player.dataset.audioInitialized === 'true') return;
        player.dataset.audioInitialized = 'true';

        const toggleBtn = document.getElementById('zenAudioFab');
        const inlineToggles = document.querySelectorAll('[data-audio-toggle]');
        const playBtn = document.getElementById('zenAudioPlayBtn');
        const rewindBtn = document.getElementById('zenAudioRewind');
        const forwardBtn = document.getElementById('zenAudioForward');
        const progressFill = document.getElementById('zenAudioProgressFill');
        const seekInput = document.getElementById('zenAudioSeek');
        const speedSelect = document.getElementById('zenAudioSpeed');
        const currentTimeEl = document.getElementById('zenAudioCurrentTime');
        const durationEl = document.getElementById('zenAudioDuration');
        const minuteLabel = document.getElementById('zenAudioMinuteLabel');

        let isPlaying = false;
        let isSeeking = false;

        function togglePlayer() {
            player.classList.toggle('is-visible');
            if (toggleBtn) toggleBtn.classList.toggle('is-active');
        }

        if (toggleBtn) {
            toggleBtn.addEventListener('click', togglePlayer);
        }

        inlineToggles.forEach(function (button) {
            if (button.dataset.audioToggleBound === 'true') return;
            button.dataset.audioToggleBound = 'true';
            button.addEventListener('click', function () {
                if (!player.classList.contains('is-visible')) togglePlayer();
                if (playBtn && !isPlaying) playBtn.click();
            });
        });

        if (playBtn) {
            playBtn.addEventListener('click', async function () {
                const svg = playBtn.querySelector('path');

                if (isPlaying) {
                    audio.pause();
                } else {
                    try {
                        await audio.play();
                    } catch (error) {
                        isPlaying = false;
                        if (svg) svg.setAttribute('d', playPath);
                        playBtn.classList.add('play-btn');
                    }
                }
            });
        }

        if (rewindBtn) {
            rewindBtn.addEventListener('click', function () {
                audio.currentTime = Math.max(0, audio.currentTime - 15);
            });
        }

        if (forwardBtn) {
            forwardBtn.addEventListener('click', function () {
                audio.currentTime = Math.min(resolveDuration(), audio.currentTime + 15);
            });
        }

        if (seekInput) {
            seekInput.addEventListener('input', function () {
                isSeeking = true;
                updateProgressVisual(Number(seekInput.value || 0), resolveDuration());
                if (currentTimeEl) currentTimeEl.textContent = formatTime(Number(seekInput.value || 0));
                updateMinuteLabel(Number(seekInput.value || 0));
            });

            seekInput.addEventListener('change', function () {
                audio.currentTime = Math.min(resolveDuration(), Math.max(0, Number(seekInput.value || 0)));
                isSeeking = false;
                updatePlayerUI();
            });
        }

        if (speedSelect) {
            speedSelect.addEventListener('change', function () {
                audio.playbackRate = Number(speedSelect.value || 1);
            });
        }

        function updatePlayerUI() {
            const totalDuration = resolveDuration();
            const currentTime = audio.currentTime || 0;

            updateProgressVisual(currentTime, totalDuration);

            if (seekInput && !isSeeking) {
                seekInput.max = String(Math.floor(totalDuration));
                seekInput.value = String(Math.floor(currentTime));
            }

            if (currentTimeEl) {
                currentTimeEl.textContent = formatTime(currentTime);
            }

            if (durationEl && Number.isFinite(audio.duration)) {
                durationEl.textContent = formatTime(audio.duration);
            }

            updateMinuteLabel(currentTime);
        }

        function updateProgressVisual(currentTime, totalDuration) {
            const percent = totalDuration > 0 ? Math.min(100, Math.max(0, currentTime / totalDuration * 100)) : 0;
            if (progressFill) {
                progressFill.style.width = percent + '%';
            }
            if (seekInput) {
                seekInput.style.setProperty('--audio-progress', percent + '%');
            }
        }

        function updateMinuteLabel(currentTime) {
            if (!minuteLabel) return;
            const minutes = Math.floor((currentTime || 0) / 60);
            minuteLabel.textContent = 'Đã nghe ' + minutes + ' phút';
        }

        function syncPlayState(nextIsPlaying) {
            isPlaying = nextIsPlaying;
            const svg = playBtn?.querySelector('path');
            if (svg) svg.setAttribute('d', isPlaying ? pausePath : playPath);
            if (playBtn) playBtn.classList.toggle('play-btn', !isPlaying);
        }

        function resolveDuration() {
            if (Number.isFinite(audio.duration) && audio.duration > 0) {
                return audio.duration;
            }

            return Number(player.dataset.durationSeconds) || 1;
        }

        audio.addEventListener('play', function () { syncPlayState(true); });
        audio.addEventListener('pause', function () { syncPlayState(false); });
        audio.addEventListener('ended', function () { syncPlayState(false); });
        audio.addEventListener('timeupdate', updatePlayerUI);
        audio.addEventListener('loadedmetadata', updatePlayerUI);
        updatePlayerUI();
    }

    function formatTime(value) {
        var seconds = Math.max(0, Math.floor(value || 0));
        var hours = Math.floor(seconds / 3600);
        var minutes = Math.floor((seconds % 3600) / 60);
        var secs = seconds % 60;

        if (hours > 0) {
            return hours + ':' + pad(minutes) + ':' + pad(secs);
        }

        return pad(minutes) + ':' + pad(secs);
    }

    function pad(value) {
        return value < 10 ? '0' + value : String(value);
    }

    document.addEventListener('DOMContentLoaded', initAudioPlayer);

    window.LemonInkAudioPlayer = {
        init: initAudioPlayer,
        formatTime: formatTime
    };
})();
