(() => {
    const states = [
        { key: 'empty', text: 'Dùng ít nhất 12 ký tự.', percent: 0 },
        { key: 'weak', text: 'Mật khẩu còn yếu.', percent: 25 },
        { key: 'fair', text: 'Khá hơn rồi, thêm số hoặc ký tự đặc biệt.', percent: 50 },
        { key: 'good', text: 'Mật khẩu tốt.', percent: 75 },
        { key: 'strong', text: 'Mật khẩu mạnh.', percent: 100 }
    ];

    function scorePassword(value) {
        if (!value) {
            return states[0];
        }

        let score = 0;
        if (value.length >= 12) score += 1;
        if (value.length >= 16) score += 1;
        if (/[a-z]/.test(value) && /[A-Z]/.test(value)) score += 1;
        if (/\d/.test(value)) score += 1;
        if (/[^A-Za-z0-9]/.test(value)) score += 1;

        return states[Math.min(Math.max(score, 1), 4)];
    }

    function findInput(target) {
        if (!target) {
            return null;
        }

        return document.getElementById(target) ||
            document.querySelector(`input[name="${target.replace(/"/g, '\\"')}"]`);
    }

    function initStrengthMeter(widget) {
        const input = findInput(widget.dataset.passwordStrengthFor);
        const fill = widget.querySelector('[data-password-strength-fill]');
        const label = widget.querySelector('[data-password-strength-text]');

        if (!input || !fill || !label) {
            return;
        }

        const render = () => {
            const state = scorePassword(input.value);
            widget.dataset.strength = state.key;
            fill.style.width = `${state.percent}%`;
            label.textContent = state.text;
        };

        input.addEventListener('input', render);
        render();
    }

    document.addEventListener('DOMContentLoaded', () => {
        document.querySelectorAll('[data-password-strength-for]').forEach(initStrengthMeter);
    });
})();
