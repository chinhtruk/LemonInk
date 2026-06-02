(function () {
    'use strict';

    const defaultDuration = 5200;

    function ensureHost() {
        let host = document.querySelector('[data-lemon-toast-host]');
        if (host) {
            return host;
        }

        host = document.createElement('div');
        host.className = 'lemon-toast-host';
        host.dataset.lemonToastHost = '';
        document.body.append(host);
        return host;
    }

    function normalizeTone(tone) {
        return ['success', 'error', 'warning', 'info'].includes(tone) ? tone : 'info';
    }

    function show(message, tone, options) {
        const text = (message || '').toString().trim();
        if (!text) {
            return null;
        }

        const toast = document.createElement('div');
        toast.className = `lemon-toast is-${normalizeTone(tone)}`;
        toast.setAttribute('role', tone === 'error' ? 'alert' : 'status');

        const marker = document.createElement('span');
        marker.className = 'lemon-toast-marker';
        marker.setAttribute('aria-hidden', 'true');

        const copy = document.createElement('span');
        copy.className = 'lemon-toast-copy';
        copy.textContent = text;

        toast.append(marker, copy);
        ensureHost().append(toast);

        const duration = Number(options?.duration) || defaultDuration;
        window.setTimeout(function () {
            toast.classList.add('is-leaving');
            window.setTimeout(function () {
                toast.remove();
            }, 220);
        }, duration);

        return toast;
    }

    function readPayload() {
        const payloadNode = document.getElementById('lemonToastPayload');
        if (!payloadNode) {
            return [];
        }

        try {
            const payload = JSON.parse(payloadNode.textContent || '[]');
            return Array.isArray(payload) ? payload : [];
        } catch {
            return [];
        }
    }

    window.LemonInkToast = {
        show: show
    };

    document.addEventListener('DOMContentLoaded', function () {
        readPayload().forEach(function (item) {
            show(item.message, item.tone);
        });
    });
})();
