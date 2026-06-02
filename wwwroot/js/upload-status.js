(function () {
    const cards = Array.from(document.querySelectorAll("[data-upload-status-card][data-book-id]"));
    if (cards.length === 0) {
        return;
    }

    const searchTools = window.LemonInkSearch || {};
    const normalizeSearch = searchTools.normalize || ((value) => String(value || "").trim().toLocaleLowerCase("vi-VN"));
    const debounce = searchTools.debounce || ((callback) => callback);

    const statusText = {
        Uploaded: "Đã nhận file",
        ExtractingText: "Đang trích xuất",
        Extracted: "Đã trích xuất",
        Summarizing: "Đang tóm tắt",
        SummaryReady: "Đã có tóm tắt",
        GeneratingAudio: "Đang tạo audio",
        Ready: "Sẵn sàng",
        Failed: "Có lỗi"
    };

    const isTerminal = (status) => status === "Ready" || status === "Failed";
    const canRead = (status, payload) =>
        Boolean(payload?.canRead) ||
        status === "SummaryReady" ||
        status === "GeneratingAudio" ||
        status === "Ready";

    const clampProgress = (value, status) => {
        if (status === "Ready") {
            return 100;
        }

        const number = Number(value);
        if (!Number.isFinite(number)) {
            return status === "Uploaded" ? 8 : 0;
        }

        return Math.min(100, Math.max(status === "Uploaded" ? 4 : 0, number));
    };

    const setHidden = (element, hidden) => {
        if (!element) {
            return;
        }

        element.hidden = hidden;
    };

    const ensureToastHost = () => {
        let host = document.querySelector("[data-upload-toast-host]");
        if (host) {
            return host;
        }

        host = document.createElement("div");
        host.className = "zen-upload-toast-host";
        host.dataset.uploadToastHost = "";
        document.body.append(host);
        return host;
    };

    const showToast = (message, tone) => {
        if (window.LemonInkToast?.show) {
            window.LemonInkToast.show(message, tone);
            return;
        }

        const toast = document.createElement("div");
        toast.className = `zen-upload-toast ${tone === "error" ? "is-error" : "is-success"}`;
        toast.setAttribute("role", "status");
        toast.textContent = message;
        ensureToastHost().append(toast);

        window.setTimeout(() => {
            toast.classList.add("is-leaving");
            window.setTimeout(() => toast.remove(), 220);
        }, 5200);
    };

    const getBookTitle = (card, payload) => {
        const heading = card.querySelector("h2, h3");
        return payload.title || heading?.textContent?.trim() || "Sách của bạn";
    };

    const notifyStatusChange = (card, payload, previousStatus, nextStatus) => {
        if (!previousStatus || previousStatus === nextStatus || card.dataset.uploadNotified === nextStatus) {
            return;
        }

        if (nextStatus === "Ready") {
            card.dataset.uploadNotified = nextStatus;
            showToast(`${getBookTitle(card, payload)} đã sẵn sàng để đọc.`, "success");
            return;
        }

        if (nextStatus === "Failed") {
            card.dataset.uploadNotified = nextStatus;
            showToast(`${getBookTitle(card, payload)} xử lý chưa thành công. Bạn có thể retry ngay.`, "error");
        }
    };

    const updateStatusClass = (label, status) => {
        if (!label) {
            return;
        }

        label.classList.remove("is-processing", "is-ready", "is-failed");
        if (status === "Failed") {
            label.classList.add("is-failed");
        } else if (status === "SummaryReady" || status === "Ready") {
            label.classList.add("is-ready");
        } else {
            label.classList.add("is-processing");
        }
    };

    const filterHost = document.querySelector("[data-upload-library-filter]");
    let activeUploadFilter = filterHost?.dataset.initialFilter || "all";

    const getCardBucket = (card) => {
        const status = card.dataset.processingStatus || "";
        const latestJobStatus = card.dataset.latestJobStatus || "";

        if (status === "Failed" || latestJobStatus === "Failed") {
            return "failed";
        }

        if (status === "Ready" || status === "SummaryReady") {
            return "ready";
        }

        return "processing";
    };

    const applyUploadFilter = () => {
        if (!filterHost) {
            return;
        }

        const normalizedFilter = ["processing", "ready", "failed"].includes(activeUploadFilter)
            ? activeUploadFilter
            : "all";

        const select = filterHost.querySelector("[data-upload-filter-select]");
        if (select) {
            select.value = normalizedFilter;
        }

        const searchInput = filterHost.querySelector("[data-upload-search]");
        const rawQuery = searchInput ? searchInput.value : "";
        const query = normalizeSearch(rawQuery);

        cards.forEach((card) => {
            const title = normalizeSearch(card.querySelector("h2, h3")?.textContent || "");
            const filename = normalizeSearch(card.querySelector(".zen-upload-file, .zen-upload-item-title-col p")?.textContent || "");
            const matchesQuery = !query || title.includes(query) || filename.includes(query);
            const matchesFilter = normalizedFilter === "all" || getCardBucket(card) === normalizedFilter;
            
            card.hidden = !(matchesQuery && matchesFilter);
        });

        if (typeof searchTools.highlight === "function") {
            searchTools.highlight(
                document,
                rawQuery,
                "[data-upload-status-card] h2, [data-upload-status-card] h3, [data-upload-status-card] .zen-upload-file, [data-upload-status-card] .zen-upload-item-title-col p"
            );
        }
    };

    const applyStatus = (card, payload) => {
        const previousStatus = card.dataset.processingStatus || "";
        const status = payload.processingStatus || card.dataset.processingStatus || "";
        card.dataset.processingStatus = status;
        if (payload.latestJobStatus) {
            card.dataset.latestJobStatus = payload.latestJobStatus;
        }

        const progress = clampProgress(payload.progress, status);
        const progressFill = card.querySelector("[data-upload-progress-fill]");
        if (progressFill) {
            progressFill.style.width = `${progress}%`;
        }

        const progressPercent = card.querySelector("[data-upload-progress-percent]");
        if (progressPercent) {
            progressPercent.textContent = `${Math.round(progress)}%`;
        }

        const estimate = card.querySelector("[data-upload-estimate]");
        if (estimate) {
            estimate.textContent = payload.estimatedTime || "";
        }

        const label = card.querySelector("[data-upload-status-label]");
        if (label) {
            label.textContent = statusText[status] || "Đang chuẩn bị";
            updateStatusClass(label, status);
        }

        const step = card.querySelector("[data-upload-step]");
        if (step) {
            step.textContent = payload.step || "";
        }

        const error = card.querySelector("[data-upload-error]");
        const hasError = status === "Failed" && Boolean(payload.failedReason);
        if (error) {
            error.textContent = hasError ? payload.failedReason : "";
            error.hidden = !hasError;
        }

        const readLink = card.querySelector("[data-upload-read-link]");
        const readable = canRead(status, payload);
        if (readLink) {
            if (payload.readUrl) {
                readLink.href = payload.readUrl;
            }
            readLink.hidden = !readable;
        }

        setHidden(card.querySelector("[data-upload-wait-button]"), readable);
        setHidden(card.querySelector("[data-upload-retry-form]"), status !== "Failed");
        setHidden(card.querySelector("[data-upload-reprocess-form]"), !readable);
        notifyStatusChange(card, payload, previousStatus, status);
        applyUploadFilter();
    };

    const fetchStatus = async (card) => {
        const bookId = card.dataset.bookId;
        if (!bookId || isTerminal(card.dataset.processingStatus)) {
            return;
        }

        const response = await fetch(`/Books/ProcessingStatus/${bookId}`, {
            headers: { Accept: "application/json" },
            credentials: "same-origin"
        });

        if (!response.ok) {
            return;
        }

        applyStatus(card, await response.json());
    };

    let stopped = false;
    const poll = async () => {
        if (stopped) {
            return;
        }

        await Promise.allSettled(cards.map(fetchStatus));

        const hasActiveCard = cards.some((card) => !isTerminal(card.dataset.processingStatus));
        if (!hasActiveCard) {
            stopped = true;
            return;
        }

        window.setTimeout(poll, document.hidden ? 8000 : 3500);
    };

    window.setTimeout(poll, 800);

    if (filterHost) {
        const select = filterHost.querySelector("[data-upload-filter-select]");
        if (select) {
            select.addEventListener("change", () => {
                activeUploadFilter = select.value || "all";
                applyUploadFilter();
            });
        }
        
        const searchInput = filterHost.querySelector("[data-upload-search]");
        if (searchInput) {
            searchInput.addEventListener("input", debounce(applyUploadFilter, 220));
        }

        applyUploadFilter();
    }
})();
