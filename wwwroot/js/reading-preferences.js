/* ============================================
   LemonInk — Reading preferences
   ============================================ */
(function () {
    'use strict';

    const reader = document.querySelector('.zen-reading-main');
    const panel = document.querySelector('[data-reading-preferences-panel]');
    const overlay = document.querySelector('[data-reading-preferences-overlay]');
    const openButtons = document.querySelectorAll('[data-reading-preferences-open]');
    const closeButtons = document.querySelectorAll('[data-reading-preferences-close]');
    const resetButton = document.querySelector('[data-reading-preferences-reset]');
    const settingInputs = Array.from(document.querySelectorAll('[data-reader-setting]'));
    const audioSpeedSelect = document.getElementById('zenAudioSpeed');

    if (!reader) return;

    const defaults = {
        fontSize: 'medium',
        lineHeight: 'comfortable',
        width: 'wide',
        audioSpeed: audioSpeedSelect ? audioSpeedSelect.value || '1' : '1'
    };

    const storageKey = 'lemonink:reading-preferences';

    function readStoredPreferences() {
        try {
            return Object.assign({}, defaults, JSON.parse(window.localStorage.getItem(storageKey) || '{}'));
        } catch {
            return Object.assign({}, defaults);
        }
    }

    function savePreferences(preferences) {
        try {
            window.localStorage.setItem(storageKey, JSON.stringify(preferences));
        } catch {
            // Reading preferences are helpful, but the page should work without storage.
        }
    }

    function applyPreferences(preferences) {
        reader.dataset.readerFontSize = preferences.fontSize || defaults.fontSize;
        reader.dataset.readerLineHeight = preferences.lineHeight || defaults.lineHeight;
        reader.dataset.readerWidth = preferences.width || defaults.width;

        settingInputs.forEach(function (input) {
            const key = input.dataset.readerSetting;
            if (key && preferences[key] !== undefined) {
                input.value = String(preferences[key]);
            }
        });

        if (audioSpeedSelect && preferences.audioSpeed) {
            audioSpeedSelect.value = String(preferences.audioSpeed);
            audioSpeedSelect.dispatchEvent(new Event('change', { bubbles: true }));
        }
    }

    let preferences = readStoredPreferences();
    applyPreferences(preferences);

    function updatePreference(key, value) {
        preferences = Object.assign({}, preferences, { [key]: value });
        savePreferences(preferences);
        applyPreferences(preferences);
    }

    settingInputs.forEach(function (input) {
        input.addEventListener('change', function () {
            const key = input.dataset.readerSetting;
            if (!key) return;
            updatePreference(key, input.value);
        });
    });

    if (audioSpeedSelect) {
        audioSpeedSelect.addEventListener('change', function () {
            if (preferences.audioSpeed === audioSpeedSelect.value) return;
            preferences = Object.assign({}, preferences, { audioSpeed: audioSpeedSelect.value });
            savePreferences(preferences);

            const panelSpeedSelect = document.querySelector('[data-reader-setting="audioSpeed"]');
            if (panelSpeedSelect) {
                panelSpeedSelect.value = audioSpeedSelect.value;
            }
        });
    }

    function openPanel() {
        if (!panel || !overlay) return;
        panel.hidden = false;
        overlay.hidden = false;
        panel.setAttribute('aria-hidden', 'false');
        document.body.classList.add('zen-modal-open');

        const firstInput = panel.querySelector('select, button');
        if (firstInput) firstInput.focus();
    }

    function closePanel() {
        if (!panel || !overlay) return;
        panel.hidden = true;
        overlay.hidden = true;
        panel.setAttribute('aria-hidden', 'true');
        document.body.classList.remove('zen-modal-open');
    }

    openButtons.forEach(function (button) {
        button.addEventListener('click', openPanel);
    });

    closeButtons.forEach(function (button) {
        button.addEventListener('click', closePanel);
    });

    if (overlay) {
        overlay.addEventListener('click', closePanel);
    }

    if (resetButton) {
        resetButton.addEventListener('click', function () {
            preferences = Object.assign({}, defaults);
            savePreferences(preferences);
            applyPreferences(preferences);
        });
    }

    document.addEventListener('keydown', function (event) {
        if (event.key === 'Escape' && panel && !panel.hidden) {
            closePanel();
        }
    });
})();
