/* ============================================
   LemonInk — Admin realtime dashboard
   ============================================ */
(function () {
    "use strict";

    const pollInterval = 3500;
    const slowPollInterval = 9000;
    const knownJobStatuses = new Map();
    let warnedAboutRealtime = false;
    let polling = false;

    function formatDecimals() {
        document.querySelectorAll("[data-decimal]").forEach(function (el) {
            const val = parseFloat(el.textContent);
            if (!Number.isNaN(val)) {
                el.textContent = val.toFixed(2);
            }
        });
    }

    function fetchLatest(url, options) {
        const separator = url.indexOf("?") !== -1 ? "&" : "?";
        return fetch(url + separator + "v=" + Date.now(), options);
    }

    function showNotification(message, type) {
        type = type || "info";

        if (window.LemonInkToast && typeof window.LemonInkToast.show === "function") {
            window.LemonInkToast.show(message, type);
            return;
        }

        const toast = document.createElement("div");
        toast.className = "zen-toast " + type;
        toast.textContent = message;
        document.body.appendChild(toast);

        setTimeout(function () {
            toast.style.opacity = "0";
            toast.style.transform = "translateX(30px)";
            toast.style.transition = "all 0.4s ease";
            setTimeout(function () { toast.remove(); }, 400);
        }, 5000);
    }

    function escapeHtml(value) {
        return String(value ?? "")
            .replaceAll("&", "&amp;")
            .replaceAll("<", "&lt;")
            .replaceAll(">", "&gt;")
            .replaceAll('"', "&quot;")
            .replaceAll("'", "&#039;");
    }

    function formatNumber(value) {
        return Number(value || 0).toLocaleString("vi-VN");
    }

    function formatDecimal(value) {
        const number = Number(value || 0);
        return Number.isFinite(number) ? number.toFixed(2) : "0.00";
    }

    function pick(obj, camelKey) {
        if (!obj) {
            return undefined;
        }

        const pascalKey = camelKey.charAt(0).toUpperCase() + camelKey.slice(1);
        if (Object.prototype.hasOwnProperty.call(obj, camelKey)) {
            return obj[camelKey];
        }
        return obj[pascalKey];
    }

    const searchTools = window.LemonInkSearch || {};
    const normalizeSearch = searchTools.normalize || ((value) => String(value || "").trim().toLocaleLowerCase("vi-VN"));
    const debounceSearch = searchTools.debounce || ((callback) => callback);

    function getAdminBookFilterState() {
        const host = document.querySelector("[data-admin-books-filter]");
        const searchInput = host?.querySelector("[data-admin-book-search]");
        const sourceSelect = host?.querySelector("[data-admin-book-source]");
        const statusSelect = host?.querySelector("[data-admin-book-status]");

        return {
            rawQuery: searchInput?.value || "",
            query: normalizeSearch(searchInput?.value || ""),
            source: sourceSelect?.value || "all",
            status: statusSelect?.value || "all"
        };
    }

    function setAdminBookCount(visible, total) {
        const count = document.querySelector("[data-admin-all-books-count]");
        if (!count) {
            return;
        }

        count.textContent = visible === total ? `${total} cuốn` : `${visible}/${total} cuốn`;
    }

    function applyAdminBookFilters() {
        const body = document.querySelector("[data-admin-all-books-body]");
        const rows = Array.from(body?.querySelectorAll("[data-admin-book-row]") || []);
        if (!body || rows.length === 0) {
            return;
        }

        const { rawQuery, query, source, status } = getAdminBookFilterState();
        let visible = 0;

        rows.forEach((row) => {
            const haystack = normalizeSearch([
                row.dataset.title,
                row.dataset.author,
                row.dataset.source,
                row.dataset.visibility,
                row.dataset.status
            ].join(" "));
            const matchesQuery = !query || haystack.includes(query);
            const matchesSource = source === "all" || row.dataset.source === source;
            const matchesStatus = status === "all" ||
                row.dataset.status === status ||
                row.dataset.visibility === status ||
                row.dataset.audio === status;

            row.hidden = !(matchesQuery && matchesSource && matchesStatus);
            if (!row.hidden) {
                visible += 1;
            }
        });

        setAdminBookCount(visible, rows.length);
        if (typeof searchTools.highlight === "function") {
            searchTools.highlight(body, rawQuery, "[data-admin-book-highlight]");
        }
    }

    function ensureSelectOption(select, value, label) {
        if (!select || !value || Array.from(select.options).some((option) => option.value === value)) {
            return;
        }

        select.add(new Option(label || value, value));
    }

    function syncAdminBookFilterOptions() {
        const host = document.querySelector("[data-admin-books-filter]");
        const body = document.querySelector("[data-admin-all-books-body]");
        if (!host || !body) {
            return;
        }

        const sourceSelect = host.querySelector("[data-admin-book-source]");
        const statusSelect = host.querySelector("[data-admin-book-status]");
        body.querySelectorAll("[data-admin-book-row]").forEach((row) => {
            ensureSelectOption(sourceSelect, row.dataset.source, row.dataset.source);
            ensureSelectOption(statusSelect, row.dataset.status, row.dataset.status);
            ensureSelectOption(statusSelect, row.dataset.visibility, row.dataset.visibility);
        });
    }

    function initAdminBookFilters() {
        const host = document.querySelector("[data-admin-books-filter]");
        if (!host) {
            return;
        }

        const searchInput = host.querySelector("[data-admin-book-search]");
        const sourceSelect = host.querySelector("[data-admin-book-source]");
        const statusSelect = host.querySelector("[data-admin-book-status]");
        const resetButton = host.querySelector("[data-admin-book-reset]");
        const debouncedApply = debounceSearch(applyAdminBookFilters, 220);

        syncAdminBookFilterOptions();
        searchInput?.addEventListener("input", debouncedApply);
        sourceSelect?.addEventListener("change", applyAdminBookFilters);
        statusSelect?.addEventListener("change", applyAdminBookFilters);
        resetButton?.addEventListener("click", () => {
            if (searchInput) {
                searchInput.value = "";
            }
            if (sourceSelect) {
                sourceSelect.value = "all";
            }
            if (statusSelect) {
                statusSelect.value = "all";
            }
            applyAdminBookFilters();
            searchInput?.focus();
        });

        applyAdminBookFilters();
    }

    function relativeTime(timestamp) {
        const date = new Date(timestamp);
        if (Number.isNaN(date.getTime())) {
            return "";
        }

        const mins = Math.max(0, Math.floor((Date.now() - date.getTime()) / 60000));
        const hours = Math.floor(mins / 60);
        const days = Math.floor(hours / 24);

        if (mins < 1) return "Vừa xong";
        if (mins < 60) return mins + " phút trước";
        if (hours < 24) return hours + " giờ trước";
        return days + " ngày trước";
    }

    function setStat(key, value, formatter) {
        document.querySelectorAll(`[data-admin-stat="${key}"]`).forEach((el) => {
            el.textContent = formatter ? formatter(value) : formatNumber(value);
        });
    }

    function updateStats(stats) {
        if (!stats) {
            return;
        }

        setStat("totalBooks", pick(stats, "totalBooks"));
        setStat("totalUsers", pick(stats, "totalUsers"));
        setStat("monthlyReads", pick(stats, "monthlyReads"));
        setStat("avgRating", pick(stats, "avgRating"), formatDecimal);
        setStat("growthPercent", pick(stats, "growthPercent"), formatDecimal);
        setStat("completionRate", pick(stats, "completionRate"), formatDecimal);
        setStat("queuedCount", pick(stats, "queuedCount"));
        setStat("runningCount", pick(stats, "runningCount"));
        setStat("failedCount", pick(stats, "failedCount"));
        setStat("succeededCount", pick(stats, "succeededCount"));
        setStat("adminCount", pick(stats, "adminCount"));
        setStat("readerCount", pick(stats, "readerCount"));
        setStat("uploadedBooksCount", pick(stats, "uploadedBooksCount"));
    }

    function renderBooks(books) {
        const body = document.querySelector("[data-admin-books-body]");
        if (!body || !Array.isArray(books)) {
            return;
        }

        body.innerHTML = books.map((book) => `
            <tr>
                <td class="title-cell">${escapeHtml(pick(book, "title"))}</td>
                <td>${escapeHtml(pick(book, "author"))}</td>
                <td class="rating-cell">${pick(book, "hasRating") ? `${formatDecimal(pick(book, "rating"))} (${formatNumber(pick(book, "reviewCount"))})` : "Chưa có"}</td>
                <td><span class="admin-status-badge ${escapeHtml(pick(book, "status"))}">${escapeHtml(pick(book, "status"))}</span></td>
                <td>${formatNumber(pick(book, "views"))}</td>
            </tr>
        `).join("");

        const count = document.querySelector("[data-admin-books-count]");
        if (count) {
            count.textContent = `${books.length} cuốn`;
        }
    }

    function getRequestToken() {
        return document.querySelector(".admin-realtime-token input[name='__RequestVerificationToken']")?.value ||
            document.querySelector("input[name='__RequestVerificationToken']")?.value ||
            "";
    }

    function renderHiddenToken() {
        const token = getRequestToken();
        return `<input name="__RequestVerificationToken" type="hidden" value="${escapeHtml(token)}" />`;
    }

    function renderBookActions(book) {
        const id = pick(book, "id");
        const isCurated = pick(book, "source") === "Curated";
        const isPublic = pick(book, "visibility") === "Public";

        return `
            <div class="admin-action-stack">
                ${isCurated ? `<a href="/Admin/Books/EditCurated/${id}" class="zen-btn zen-btn-ghost">Sửa</a>` : ""}
                <form action="/Admin/Books/${isPublic ? "Unpublish" : "Publish"}/${id}" method="post">
                    ${renderHiddenToken()}
                    <button type="submit" class="zen-btn zen-btn-ghost">${isPublic ? "Unpublish" : "Publish"}</button>
                </form>
                <form action="/Admin/Books/RegenerateSummary/${id}" method="post" onsubmit="return confirm('Tạo lại summary cho sách này? Nội dung hiện tại có thể bị thay bằng kết quả AI mới.');">
                    ${renderHiddenToken()}
                    <button type="submit" class="zen-btn zen-btn-ghost">Re-summary</button>
                </form>
                <form action="/Admin/Books/RegenerateAudio/${id}" method="post">
                    ${renderHiddenToken()}
                    <button type="submit" class="zen-btn zen-btn-ghost">Re-audio</button>
                </form>
                <form action="/Admin/Books/Delete/${id}" method="post" onsubmit="return confirm('Xóa sách này khỏi hệ thống? Hành động này không thể hoàn tác.');">
                    ${renderHiddenToken()}
                    <button type="submit" class="zen-btn zen-btn-danger">Xóa</button>
                </form>
            </div>
        `;
    }

    function renderAllBooks(books) {
        const body = document.querySelector("[data-admin-all-books-body]");
        if (!body || !Array.isArray(books)) {
            return;
        }

        body.innerHTML = books.map((book) => {
            const isAudioReady = Boolean(pick(book, "isAudioReady"));
            const id = pick(book, "id");
            const title = pick(book, "title");
            const author = pick(book, "author");
            const source = pick(book, "source");
            const visibility = pick(book, "visibility");
            const status = pick(book, "status");
            const audioState = isAudioReady ? "ready" : "missing";
            return `
                <tr data-admin-book-row
                    data-title="${escapeHtml(title)}"
                    data-author="${escapeHtml(author)}"
                    data-source="${escapeHtml(source)}"
                    data-visibility="${escapeHtml(visibility)}"
                    data-status="${escapeHtml(status)}"
                    data-audio="${audioState}">
                    <td class="title-cell">
                        <a href="/Home/Read/${id}" data-admin-book-highlight>${escapeHtml(title)}</a>
                        <div class="admin-row-subtitle" data-admin-book-highlight>${escapeHtml(author)}</div>
                    </td>
                    <td>${escapeHtml(source)}</td>
                    <td>${escapeHtml(visibility)}</td>
                    <td><span class="admin-status-badge ${escapeHtml(status)}">${escapeHtml(status)}</span></td>
                    <td>
                        <span class="admin-status-badge ${isAudioReady ? "succeeded" : "queued"}">
                            ${isAudioReady ? "ready" : "missing"}
                        </span>
                    </td>
                    <td>${formatNumber(pick(book, "views"))}</td>
                    <td class="admin-actions-cell">${renderBookActions(book)}</td>
                </tr>
            `;
        }).join("");

        const count = document.querySelector("[data-admin-all-books-count]");
        if (count) {
            count.textContent = `${books.length} cuốn`;
        }
        syncAdminBookFilterOptions();
        applyAdminBookFilters();
    }

    function renderUsers(users) {
        const body = document.querySelector("[data-admin-users-body]");
        if (!body || !Array.isArray(users)) {
            return;
        }

        body.innerHTML = users.map((user) => {
            const roles = String(pick(user, "roles") || "Reader")
                .split(", ")
                .filter(Boolean);
            const userId = pick(user, "id");
            const canDelete = Boolean(pick(user, "canDelete"));
            const deleteUnavailableReason = pick(user, "deleteUnavailableReason") || "Không thể xoá";

            return `
                <tr>
                    <td class="title-cell">
                        ${escapeHtml(pick(user, "displayName"))}
                        <div class="admin-row-subtitle">${escapeHtml(pick(user, "email"))}</div>
                    </td>
                    <td>
                        ${roles.map((role) => `<span class="admin-status-badge ${role === "Admin" ? "published" : "queued"}">${escapeHtml(role)}</span>`).join("")}
                    </td>
                    <td>${formatNumber(pick(user, "uploadedBooksCount"))}</td>
                    <td>${formatNumber(pick(user, "bookmarksCount"))}</td>
                    <td>${formatNumber(pick(user, "readingProgressCount"))}</td>
                    <td>${escapeHtml(pick(user, "createdAtText"))}</td>
                    <td>${escapeHtml(pick(user, "lastLoginAtText"))}</td>
                    <td class="admin-actions-cell">
                        ${canDelete ? `
                            <form action="/Admin/Users/Delete/${encodeURIComponent(userId)}" method="post" onsubmit="return confirm('Xoá tài khoản này? Toàn bộ sách cá nhân, tiến độ đọc, bookmark, ghi chú, chat và review của tài khoản cũng sẽ bị xoá.');">
                                ${renderHiddenToken()}
                                <button type="submit" class="zen-btn zen-btn-danger">Xoá</button>
                            </form>
                        ` : `<span class="admin-muted-count">${escapeHtml(deleteUnavailableReason)}</span>`}
                    </td>
                </tr>
            `;
        }).join("");

        const count = document.querySelector("[data-admin-users-count]");
        if (count) {
            count.textContent = `${users.length} tài khoản`;
        }
    }

    function renderRetryForm(job) {
        if (!pick(job, "canRetry")) {
            return "";
        }

        const jobId = pick(job, "id");
        return `
            <form action="/Admin/ProcessingJobs/Retry/${jobId}" method="post">
                ${renderHiddenToken()}
                <button type="submit" class="zen-btn zen-btn-primary">Retry</button>
            </form>
        `;
    }

    function renderQuality(job) {
        const summary = pick(job, "qualitySummary");
        if (!summary) {
            return '<span class="admin-row-subtitle">Chưa có báo cáo</span>';
        }

        const status = pick(job, "qualityStatus") || "passed";
        const label = status === "warning" ? "Cảnh báo" : status === "failed" ? "Lỗi" : "Ổn";
        const warnings = pick(job, "qualityWarnings");

        return `
            <span class="admin-quality-badge ${escapeHtml(status)}">${label}</span>
            <div class="admin-quality-summary">${escapeHtml(summary)}</div>
            ${warnings ? `<div class="admin-quality-warning">${escapeHtml(warnings)}</div>` : ""}
        `;
    }

    function renderJobs(jobs) {
        if (!Array.isArray(jobs)) {
            return;
        }

        jobs.forEach((job) => {
            const jobId = pick(job, "id");
            const status = pick(job, "status");
            const previous = knownJobStatuses.get(jobId);
            if (previous && previous !== status) {
                if (status === "succeeded") {
                    showNotification(`Job #${jobId} đã xử lý xong.`, "success");
                } else if (status === "failed") {
                    showNotification(`Job #${jobId} đang lỗi và có thể retry.`, "error");
                }
            }
            knownJobStatuses.set(jobId, status);
        });

        const body = document.querySelector("[data-admin-jobs-body]");
        if (!body) {
            return;
        }

        body.innerHTML = jobs.map((job) => `
            <tr data-admin-job-row data-job-id="${pick(job, "id")}">
                <td class="title-cell">
                    #${pick(job, "id")}
                    <div class="admin-row-subtitle">${escapeHtml(pick(job, "type"))}</div>
                </td>
                <td>
                    <a href="/Home/Read/${pick(job, "bookId")}">${escapeHtml(pick(job, "bookTitle"))}</a>
                    <div class="admin-row-subtitle">${escapeHtml(pick(job, "updatedAtText") || "")}</div>
                </td>
                <td><span class="admin-status-badge ${escapeHtml(pick(job, "status"))}">${escapeHtml(pick(job, "status"))}</span></td>
                <td>
                    <div class="admin-progress">
                        <span style="width: ${Math.max(0, Math.min(100, Number(pick(job, "progressPercent") || 0)))}%;"></span>
                    </div>
                    <div class="admin-row-subtitle">${Number(pick(job, "progressPercent") || 0)}%</div>
                </td>
                <td class="admin-quality-cell">${renderQuality(job)}</td>
                <td>
                    ${escapeHtml(pick(job, "currentStep") || "")}
                    ${pick(job, "errorMessage") ? `<div class="admin-error-text">${escapeHtml(pick(job, "errorMessage"))}</div>` : ""}
                </td>
                <td>${Number(pick(job, "retryCount") || 0)}</td>
                <td class="admin-actions-cell">${renderRetryForm(job)}</td>
            </tr>
        `).join("");

        const count = document.querySelector("[data-admin-jobs-count]");
        if (count) {
            count.textContent = `${jobs.length} job`;
        }
    }

    function renderNotifications(notifications) {
        const list = document.querySelector("[data-admin-notification-list]");
        if (!list || !Array.isArray(notifications)) {
            return;
        }

        list.innerHTML = notifications.map((item) => `
            <div class="admin-notification-item ${pick(item, "isRead") ? "" : "unread"}">
                <div class="admin-notification-dot ${escapeHtml(pick(item, "type"))}"></div>
                <div>
                    <div class="admin-notification-text">${escapeHtml(pick(item, "message"))}</div>
                    <div class="admin-notification-time" data-timestamp="${escapeHtml(pick(item, "timestamp"))}">${relativeTime(pick(item, "timestamp"))}</div>
                </div>
            </div>
        `).join("");

        const count = document.querySelector("[data-admin-notification-count]");
        if (count) {
            count.textContent = `${notifications.filter((item) => !pick(item, "isRead")).length} mới`;
        }
    }

    function renderActivities(activities) {
        const list = document.querySelector("[data-admin-activity-list]");
        if (!list || !Array.isArray(activities)) {
            return;
        }

        list.innerHTML = activities.map((item) => `
            <div class="admin-activity-item">
                <div class="admin-activity-dot"></div>
                <div>
                    <div class="admin-activity-text">
                        <span class="admin-activity-action">${escapeHtml(pick(item, "action"))}:</span>
                        ${escapeHtml(pick(item, "detail"))}
                    </div>
                    <div class="admin-activity-time" data-timestamp="${escapeHtml(pick(item, "timestamp"))}">${relativeTime(pick(item, "timestamp"))}</div>
                </div>
            </div>
        `).join("");
    }

    function aiStatusClass(status) {
        if (status === "healthy") return "succeeded";
        if (status === "cooldown") return "queued";
        if (status === "warning") return "failed";
        return "cancelled";
    }

    function setAiHealthStat(key, value) {
        document.querySelectorAll(`[data-ai-health-stat="${key}"]`).forEach((el) => {
            el.textContent = formatNumber(value);
        });
    }

    function formatOperationDuration(milliseconds) {
        const duration = Number(milliseconds || 0);
        if (duration <= 0) return "Chưa có";
        return duration >= 1000 ? `${(duration / 1000).toFixed(1)}s` : `${duration}ms`;
    }

    function formatDurationSeconds(seconds) {
        const duration = Number(seconds || 0);
        if (!Number.isFinite(duration) || duration <= 0) {
            return "-";
        }

        if (duration < 60) {
            return `${Math.round(duration)}s`;
        }

        const minutes = Math.floor(duration / 60);
        const remainingSeconds = Math.round(duration % 60);
        return remainingSeconds > 0 ? `${minutes}m ${remainingSeconds}s` : `${minutes}m`;
    }

    function formatPercent(value) {
        const number = Number(value || 0);
        return Number.isFinite(number) ? `${number.toFixed(1)}%` : "0.0%";
    }

    function setOpsStat(key, value, formatter) {
        document.querySelectorAll(`[data-admin-ops-stat="${key}"]`).forEach((el) => {
            el.textContent = formatter ? formatter(value) : formatNumber(value);
        });
    }

    function renderChartRows(selector, items, buildRow, emptyText) {
        const container = document.querySelector(selector);
        if (!container || !Array.isArray(items)) {
            return;
        }

        if (!items.length) {
            container.innerHTML = `<div class="admin-empty-row">${escapeHtml(emptyText || "Chưa có dữ liệu.")}</div>`;
            return;
        }

        container.innerHTML = items.map(buildRow).join("");
    }

    function renderOperations(operations) {
        if (!operations) {
            return;
        }

        setOpsStat("booksProcessedToday", pick(operations, "booksProcessedToday"));
        setOpsStat("jobs24Hours", pick(operations, "jobs24Hours"));
        setOpsStat("jobsSucceeded24Hours", pick(operations, "jobsSucceeded24Hours"));
        setOpsStat("jobsFailed24Hours", pick(operations, "jobsFailed24Hours"));
        setOpsStat("jobSuccessRatePercent", pick(operations, "jobSuccessRatePercent"), formatPercent);
        setOpsStat("averageJobDurationSeconds", pick(operations, "averageJobDurationSeconds"), formatDurationSeconds);
        setOpsStat("quotaOrRateLimitErrors24Hours", pick(operations, "quotaOrRateLimitErrors24Hours"));
        setOpsStat("modelCalls24Hours", pick(operations, "modelCalls24Hours"));
        setOpsStat("modelSuccessRate24Hours", pick(operations, "modelSuccessRate24Hours"), formatPercent);

        renderChartRows(
            "[data-admin-ops-status-list]",
            pick(operations, "jobStatusMetrics"),
            (metric) => {
                const percent = Math.max(0, Math.min(100, Number(pick(metric, "percent") || 0)));
                return `
                    <div class="admin-chart-row">
                        <div class="admin-chart-meta">
                            <span>${escapeHtml(pick(metric, "label"))}</span>
                            <strong>${formatNumber(pick(metric, "count"))} · ${formatPercent(percent)}</strong>
                        </div>
                        <div class="admin-chart-track">
                            <span class="admin-chart-fill ${escapeHtml(pick(metric, "cssClass"))}" style="width: ${percent.toFixed(1)}%"></span>
                        </div>
                    </div>
                `;
            },
            "Chưa có job trong 24 giờ."
        );

        renderChartRows(
            "[data-admin-ops-model-list]",
            pick(operations, "modelQuotaMetrics"),
            (metric) => {
                const percent = Math.max(0, Math.min(100, Number(pick(metric, "percentOfMax") || 0)));
                return `
                    <div class="admin-chart-row">
                        <div class="admin-chart-meta">
                            <span>${escapeHtml(pick(metric, "task"))} · ${escapeHtml(pick(metric, "model"))}</span>
                            <strong>${formatNumber(pick(metric, "quotaOrRateLimitFailureCount"))} quota · ${formatPercent(pick(metric, "successRatePercent"))} OK</strong>
                        </div>
                        <div class="admin-chart-track">
                            <span class="admin-chart-fill failed" style="width: ${percent.toFixed(1)}%"></span>
                        </div>
                    </div>
                `;
            },
            "Chưa có lỗi quota hoặc rate limit."
        );

        const pipelineBody = document.querySelector("[data-admin-ops-pipeline-body]");
        const pipelineMetrics = pick(operations, "pipelineMetrics");
        if (pipelineBody && Array.isArray(pipelineMetrics)) {
            pipelineBody.innerHTML = pipelineMetrics.length
                ? pipelineMetrics.map((metric) => `
                    <tr>
                        <td class="title-cell">${escapeHtml(pick(metric, "type"))}</td>
                        <td>${formatNumber(pick(metric, "totalCount"))}</td>
                        <td>${formatNumber(pick(metric, "succeededCount"))}</td>
                        <td>${formatNumber(pick(metric, "failedCount"))}</td>
                        <td>${formatNumber(pick(metric, "retryCount"))}</td>
                        <td>${formatDurationSeconds(pick(metric, "averageDurationSeconds"))}</td>
                        <td>${pick(metric, "latestFailure") ? `<div class="admin-error-text">${escapeHtml(pick(metric, "latestFailure"))}</div>` : "Chưa có"}</td>
                    </tr>
                `).join("")
                : '<tr><td colspan="7" class="admin-empty-cell">Chưa có job pipeline trong 24 giờ.</td></tr>';
        }
    }

    function renderAiHealth(payload) {
        if (!payload) {
            return;
        }

        setAiHealthStat("healthyCount", pick(payload, "healthyCount"));
        setAiHealthStat("cooldownCount", pick(payload, "cooldownCount"));
        setAiHealthStat("warningCount", pick(payload, "warningCount"));
        setAiHealthStat("recentAiFailures", pick(payload, "recentAiFailures"));
        setAiHealthStat("modelCalls24Hours", pick(payload, "modelCalls24Hours"));
        setAiHealthStat("quotaOrRateLimitErrors24Hours", pick(payload, "quotaOrRateLimitErrors24Hours"));
        document.querySelectorAll('[data-ai-health-stat-rate="successRate24Hours"]').forEach((el) => {
            el.textContent = Number(pick(payload, "successRate24Hours") || 0).toFixed(1);
        });

        const modelsBody = document.querySelector("[data-ai-health-models-body]");
        const models = pick(payload, "models");
        if (modelsBody && Array.isArray(models)) {
            modelsBody.innerHTML = models.map((model) => {
                const status = pick(model, "status") || "healthy";
                return `
                    <tr>
                        <td>${escapeHtml(pick(model, "task"))}</td>
                        <td class="title-cell">${escapeHtml(pick(model, "model"))}</td>
                        <td><span class="admin-status-badge ${aiStatusClass(status)}">${escapeHtml(status)}</span></td>
                        <td>${formatNumber(pick(model, "successCount"))}</td>
                        <td>${formatNumber(pick(model, "failureCount"))}</td>
                        <td>${Number(pick(model, "successRatePercent") || 0).toFixed(1)}%</td>
                        <td>${formatOperationDuration(pick(model, "averageDurationMilliseconds"))}</td>
                        <td>${formatNumber(pick(model, "quotaOrRateLimitFailureCount"))}</td>
                        <td>${escapeHtml(pick(model, "cooldownUntilText"))}</td>
                        <td>${pick(model, "lastError") ? `<div class="admin-error-text">${escapeHtml(pick(model, "lastError"))}</div>` : '<span class="admin-row-subtitle">Chưa có</span>'}</td>
                    </tr>
                `;
            }).join("");
        }

        const pipelineBody = document.querySelector("[data-ai-health-pipeline-body]");
        const pipelineJobs = pick(payload, "pipelineJobs24Hours");
        if (pipelineBody && Array.isArray(pipelineJobs)) {
            pipelineBody.innerHTML = pipelineJobs.map((job) => `
                <tr>
                    <td class="title-cell">${escapeHtml(pick(job, "type"))}</td>
                    <td>${formatNumber(pick(job, "totalCount"))}</td>
                    <td>${formatNumber(pick(job, "succeededCount"))}</td>
                    <td>${formatNumber(pick(job, "failedCount"))}</td>
                    <td>${formatNumber(pick(job, "retryCount"))}</td>
                    <td>${Number(pick(job, "successRatePercent") || 0).toFixed(1)}%</td>
                    <td>${pick(job, "averageDurationSeconds") == null ? "-" : `${Number(pick(job, "averageDurationSeconds"))}s`}</td>
                    <td>${pick(job, "latestFailure") ? `<div class="admin-error-text">${escapeHtml(pick(job, "latestFailure"))}</div>` : '<span class="admin-row-subtitle">Chưa có</span>'}</td>
                </tr>
            `).join("");
        }

        const operationsBody = document.querySelector("[data-ai-health-operations-body]");
        const recentOperations = pick(payload, "recentOperations");
        if (operationsBody && Array.isArray(recentOperations)) {
            operationsBody.innerHTML = recentOperations.map((operation) => {
                const succeeded = Boolean(pick(operation, "succeeded"));
                return `
                    <tr>
                        <td>${escapeHtml(pick(operation, "occurredAtText") || "")}</td>
                        <td>${escapeHtml(pick(operation, "task"))}</td>
                        <td class="title-cell">${escapeHtml(pick(operation, "model"))}</td>
                        <td><span class="admin-status-badge ${succeeded ? "succeeded" : "failed"}">${succeeded ? "succeeded" : "failed"}</span></td>
                        <td>${formatOperationDuration(pick(operation, "durationMilliseconds"))}</td>
                        <td>${escapeHtml(pick(operation, "failureKind") || "-")}</td>
                        <td>${pick(operation, "errorMessage") ? `<div class="admin-error-text">${escapeHtml(pick(operation, "errorMessage"))}</div>` : '<span class="admin-row-subtitle">Không có lỗi</span>'}</td>
                    </tr>
                `;
            }).join("");
            const count = document.querySelector("[data-ai-health-operations-count]");
            if (count) {
                count.textContent = `${recentOperations.length} event`;
            }
        }

        const jobsBody = document.querySelector("[data-ai-health-jobs-body]");
        const recentJobs = pick(payload, "recentJobs");
        if (jobsBody && Array.isArray(recentJobs)) {
            jobsBody.innerHTML = recentJobs.map((job) => `
                <tr>
                    <td class="title-cell">
                        #${pick(job, "id")}
                        <div class="admin-row-subtitle">${escapeHtml(pick(job, "type"))}</div>
                    </td>
                    <td>
                        <a href="/Home/Read/${pick(job, "bookId")}">${escapeHtml(pick(job, "bookTitle"))}</a>
                        <div class="admin-row-subtitle">${escapeHtml(pick(job, "updatedAtText") || "")}</div>
                    </td>
                    <td><span class="admin-status-badge ${escapeHtml(pick(job, "status"))}">${escapeHtml(pick(job, "status"))}</span></td>
                    <td>
                        <div class="admin-progress">
                            <span style="width: ${Math.max(0, Math.min(100, Number(pick(job, "progressPercent") || 0)))}%;"></span>
                        </div>
                        <div class="admin-row-subtitle">${Number(pick(job, "progressPercent") || 0)}%</div>
                    </td>
                    <td>${pick(job, "durationSeconds") == null ? "-" : `${Number(pick(job, "durationSeconds"))}s`}</td>
                    <td>
                        ${escapeHtml(pick(job, "currentStep") || "")}
                        ${pick(job, "errorMessage") ? `<div class="admin-error-text">${escapeHtml(pick(job, "errorMessage"))}</div>` : ""}
                    </td>
                </tr>
            `).join("");

            const count = document.querySelector("[data-ai-health-jobs-count]");
            if (count) {
                count.textContent = `${recentJobs.length} job`;
            }
        }
    }

    async function refreshDashboardData(showToast) {
        const response = await fetchLatest("/Admin/Realtime", {
            headers: { Accept: "application/json" },
            credentials: "same-origin"
        });

        if (!response.ok) {
            throw new Error("Admin realtime request failed");
        }

        const payload = await response.json();
        updateStats(payload.stats);
        renderBooks(payload.books);
        renderAllBooks(payload.allBooks);
        renderUsers(payload.users);
        renderJobs(payload.jobs);
        renderNotifications(payload.notifications);
        renderActivities(payload.activities);
        renderOperations(payload.operations);

        if (showToast) {
            showNotification("Đã cập nhật dữ liệu admin mới nhất.", "success");
        }
    }

    async function refreshAiHealthData(showToast) {
        const response = await fetchLatest("/Admin/AiHealthStatus", {
            headers: { Accept: "application/json" },
            credentials: "same-origin"
        });

        if (!response.ok) {
            throw new Error("AI health realtime request failed");
        }

        const payload = await response.json();
        renderAiHealth(payload);

        if (showToast) {
            showNotification("Đã cập nhật AI Health mới nhất.", "success");
        }
    }

    function initQuickActions() {
        document.querySelectorAll("[data-action]").forEach(function (btn) {
            btn.addEventListener("click", function () {
                const action = this.getAttribute("data-action");
                if (action === "refresh") {
                    const refresh = document.querySelector('[data-admin-realtime="ai-health"]')
                        ? refreshAiHealthData
                        : refreshDashboardData;
                    refresh(true).catch(() => {
                        showNotification("Chưa cập nhật được dữ liệu realtime. LemonInk sẽ tự thử lại.", "warning");
                    });
                }
            });
        });
    }

    function initRelativeTime() {
        document.querySelectorAll("[data-timestamp]").forEach(function (el) {
            const ts = el.getAttribute("data-timestamp");
            if (ts) {
                el.textContent = relativeTime(ts);
            }
        });
    }

    function startRealtimePolling() {
        if (!document.querySelector("[data-admin-realtime]") || polling) {
            return;
        }

        polling = true;
        const isAiHealthPage = Boolean(document.querySelector('[data-admin-realtime="ai-health"]'));
        const tick = async () => {
            try {
                if (isAiHealthPage) {
                    await refreshAiHealthData(false);
                } else {
                    await refreshDashboardData(false);
                }
                warnedAboutRealtime = false;
            } catch {
                if (!warnedAboutRealtime) {
                    warnedAboutRealtime = true;
                    showNotification("Admin realtime đang tạm gián đoạn. LemonInk sẽ tự thử lại.", "warning");
                }
            } finally {
                window.setTimeout(tick, document.hidden ? slowPollInterval : pollInterval);
            }
        };

        window.setTimeout(tick, 600);
    }

    document.addEventListener("DOMContentLoaded", function () {
        formatDecimals();
        initQuickActions();
        initRelativeTime();
        initAdminBookFilters();
        startRealtimePolling();
    });

    window.ZenAdmin = {
        showNotification,
        fetchLatest,
        formatDecimals,
        refreshDashboardData
    };
})();
