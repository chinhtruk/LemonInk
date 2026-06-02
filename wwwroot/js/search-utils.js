/* ============================================
   LemonInk — Shared search helpers
   ============================================ */
(function () {
    "use strict";

    function escapeHtml(value) {
        return String(value ?? "")
            .replaceAll("&", "&amp;")
            .replaceAll("<", "&lt;")
            .replaceAll(">", "&gt;")
            .replaceAll('"', "&quot;")
            .replaceAll("'", "&#039;");
    }

    function foldCharacter(character) {
        if (character === "đ") return "d";
        if (character === "Đ") return "D";

        return character
            .normalize("NFD")
            .replace(/[\u0300-\u036f]/g, "");
    }

    function normalize(value) {
        return Array.from(String(value ?? ""))
            .map(foldCharacter)
            .join("")
            .toLocaleLowerCase("vi-VN")
            .trim();
    }

    function debounce(fn, wait) {
        let timer = 0;
        return function debounced() {
            const context = this;
            const args = arguments;
            window.clearTimeout(timer);
            timer = window.setTimeout(function () {
                fn.apply(context, args);
            }, wait || 240);
        };
    }

    function foldWithMap(text) {
        let folded = "";
        const map = [];
        let originalIndex = 0;

        Array.from(String(text ?? "")).forEach(function (character) {
            const start = originalIndex;
            const end = start + character.length;
            const foldedCharacter = foldCharacter(character).toLocaleLowerCase("vi-VN");

            Array.from(foldedCharacter).forEach(function (foldedUnit) {
                folded += foldedUnit;
                map.push({ start, end });
            });

            originalIndex = end;
        });

        return { folded, map };
    }

    function mergeRanges(ranges) {
        if (!ranges.length) return ranges;

        ranges.sort(function (a, b) {
            return a.start - b.start || a.end - b.end;
        });

        const merged = [ranges[0]];
        for (let index = 1; index < ranges.length; index += 1) {
            const current = ranges[index];
            const previous = merged[merged.length - 1];
            if (current.start <= previous.end) {
                previous.end = Math.max(previous.end, current.end);
            } else {
                merged.push(current);
            }
        }

        return merged;
    }

    function highlightHtml(text, query) {
        const original = String(text ?? "");
        const normalizedQuery = normalize(query);
        if (!normalizedQuery) return escapeHtml(original);

        const tokens = Array.from(new Set(normalizedQuery.split(/\s+/).filter(function (token) {
            return token.length >= 2;
        })));

        if (!tokens.length) return escapeHtml(original);

        const foldedText = foldWithMap(original);
        const ranges = [];

        tokens.forEach(function (token) {
            let startAt = 0;
            while (startAt < foldedText.folded.length) {
                const found = foldedText.folded.indexOf(token, startAt);
                if (found === -1) break;

                const startMap = foldedText.map[found];
                const endMap = foldedText.map[found + token.length - 1];
                if (startMap && endMap) {
                    ranges.push({ start: startMap.start, end: endMap.end });
                }

                startAt = found + token.length;
            }
        });

        if (!ranges.length) return escapeHtml(original);

        let cursor = 0;
        let html = "";
        mergeRanges(ranges).forEach(function (range) {
            html += escapeHtml(original.slice(cursor, range.start));
            html += '<mark class="zen-search-highlight">' + escapeHtml(original.slice(range.start, range.end)) + "</mark>";
            cursor = range.end;
        });
        html += escapeHtml(original.slice(cursor));

        return html;
    }

    function highlight(root, query, selector) {
        const scope = root || document;
        const targets = Array.from(scope.querySelectorAll(selector || "[data-search-highlight]"));

        targets.forEach(function (element) {
            if (!Object.prototype.hasOwnProperty.call(element.dataset, "searchOriginalText")) {
                element.dataset.searchOriginalText = element.textContent || "";
            }

            const original = element.dataset.searchOriginalText || "";
            if (!normalize(query)) {
                element.textContent = original;
                return;
            }

            element.innerHTML = highlightHtml(original, query);
        });
    }

    window.LemonInkSearch = {
        debounce,
        escapeHtml,
        highlight,
        highlightHtml,
        normalize
    };
})();
