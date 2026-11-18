// Signage admin: manage displays, upload slides, and build per-display playlists.
//   GET    /api/displays                          — displays (+ playlist)
//   GET    /api/slides                            — slide library
//   POST   /api/slides                            — upload
//   DELETE /api/slides/{id}                       — delete from library
//   PUT    /api/displays/{slug}/playlist          — replace (ordered ids)  ← reorder/clear
//   POST   /api/displays/{slug}/playlist/{id}     — append
//   DELETE /api/displays/{slug}/playlist/{id}     — remove

(() => {
    "use strict";

    // ---- State ----------------------------------------------------------------
    let displays = [];
    let slides = [];
    let selectedSlug = null;

    // ---- DOM ------------------------------------------------------------------
    const displaysEl = document.getElementById("displays");
    const slidesEl = document.getElementById("slides");
    const playlistEl = document.getElementById("playlist");
    const libHint = document.getElementById("libHint");
    const plHint = document.getElementById("plHint");
    const toastEl = document.getElementById("toast");
    const refreshBtn = document.getElementById("refreshBtn");
    const uploadForm = document.getElementById("uploadForm");
    const fileInput = document.getElementById("fileInput");
    const titleInput = document.getElementById("titleInput");
    const durationInput = document.getElementById("durationInput");
    const uploadBtn = document.getElementById("uploadBtn");
    const clearPlaylistBtn = document.getElementById("clearPlaylistBtn");

    // Display create/edit form
    const newDisplayBtn = document.getElementById("newDisplayBtn");
    const displayForm = document.getElementById("displayForm");
    const dispName = document.getElementById("dispName");
    const dispSlug = document.getElementById("dispSlug");
    const dispOrientation = document.getElementById("dispOrientation");
    const dispFormError = document.getElementById("dispFormError");
    const dispSubmitBtn = document.getElementById("dispSubmitBtn");
    const dispCancelBtn = document.getElementById("dispCancelBtn");

    // ---- Helpers --------------------------------------------------------------
    function esc(s) {
        return String(s ?? "").replace(/[&<>"']/g, c => (
            { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]
        ));
    }

    let toastTimer = null;
    function toast(msg, isError) {
        toastEl.textContent = msg;
        toastEl.classList.toggle("err", !!isError);
        toastEl.classList.add("show");
        if (toastTimer) clearTimeout(toastTimer);
        toastTimer = setTimeout(() => toastEl.classList.remove("show"), 2500);
    }

    async function api(url, options) {
        const res = await fetch(url, options);
        if (!res.ok) {
            let detail = "";
            try { detail = (await res.json()).error || ""; } catch { /* ignore */ }
            throw new Error(detail || `${res.status} ${res.statusText}`);
        }
        return res.status === 204 ? null : res.json();
    }

    const isVideo = (s) => (s.mediaType || "").toLowerCase() === "video";

    // ---- Load -----------------------------------------------------------------
    async function load() {
        try {
            const [d, s] = await Promise.all([api("/api/displays"), api("/api/slides")]);
            displays = d || [];
            slides = s || [];

            if (selectedSlug && !displays.some(x => x.slug === selectedSlug)) selectedSlug = null;
            if (!selectedSlug && displays.length) selectedSlug = displays[0].slug;

            renderAll();
        } catch (err) {
            displaysEl.innerHTML = `<div class="empty">Failed: ${esc(err.message)}</div>`;
            slidesEl.innerHTML = `<div class="empty">Failed: ${esc(err.message)}</div>`;
        }
    }

    function renderAll() { renderDisplays(); renderSlides(); renderPlaylist(); renderHints(); }

    // ---- Render: displays -----------------------------------------------------
    function renderDisplays() {
        if (!displays.length) { displaysEl.innerHTML = `<div class="empty">No displays.</div>`; return; }
        displaysEl.innerHTML = displays.map(d => {
            const active = d.slug === selectedSlug ? " active" : "";
            const online = d.isOnline ? " online" : "";
            const orient = d.orientation === "Portrait" ? "Portrait" : "Landscape";
            const count = (d.playlist || []).length;
            return `
                <div class="display-row${active}" data-slug="${esc(d.slug)}">
                    <span class="dot${online}" title="${d.isOnline ? "Online" : "Offline"}"></span>
                    <div class="meta">
                        <div class="name">${esc(d.name)}</div>
                        <div class="slug">/player/?display=${esc(d.slug)}</div>
                    </div>
                    <span class="badge">${count} slide${count === 1 ? "" : "s"}</span>
                    <div class="row-actions">
                        <button class="icon-btn" data-edit="${esc(d.slug)}" title="Edit">✎</button>
                        <button class="icon-btn del" data-delete="${esc(d.slug)}" title="Delete">🗑</button>
                    </div>
                </div>`;
        }).join("");

        displaysEl.querySelectorAll(".display-row").forEach(row => {
            row.addEventListener("click", (e) => {
                if (e.target.closest(".row-actions")) return;   // don't select when clicking edit/delete
                selectedSlug = row.dataset.slug;
                renderAll();
            });
        });
        displaysEl.querySelectorAll("[data-edit]").forEach(btn => {
            btn.addEventListener("click", () => openDisplayForm(btn.dataset.edit));
        });
        displaysEl.querySelectorAll("[data-delete]").forEach(btn => {
            btn.addEventListener("click", () => deleteDisplay(btn.dataset.delete));
        });
    }

    function currentDisplay() { return displays.find(x => x.slug === selectedSlug) || null; }

    // ---- Displays: create / edit / delete ------------------------------------
    let editingSlug = null;   // null = create mode

    function openDisplayForm(slug) {
        editingSlug = slug || null;
        dispFormError.textContent = "";
        if (editingSlug) {
            const d = displays.find(x => x.slug === editingSlug);
            dispName.value = d?.name ?? "";
            dispSlug.value = d?.slug ?? "";
            dispOrientation.value = d?.orientation ?? "Portrait";
            dispSubmitBtn.textContent = "Save";
        } else {
            dispName.value = "";
            dispSlug.value = "";
            dispOrientation.value = "Portrait";
            dispSubmitBtn.textContent = "Create";
        }
        displayForm.hidden = false;
        dispName.focus();
    }

    function closeDisplayForm() {
        displayForm.hidden = true;
        editingSlug = null;
        dispFormError.textContent = "";
    }

    newDisplayBtn.addEventListener("click", () => openDisplayForm(null));
    dispCancelBtn.addEventListener("click", closeDisplayForm);

    displayForm.addEventListener("submit", async (e) => {
        e.preventDefault();
        dispFormError.textContent = "";
        const name = dispName.value.trim();
        if (!name) { dispFormError.textContent = "Name is required."; return; }

        const body = {
            name,
            slug: dispSlug.value.trim() || null,
            orientation: dispOrientation.value,
        };

        dispSubmitBtn.disabled = true;
        try {
            if (editingSlug) {
                const updated = await api(`/api/displays/${enc(editingSlug)}`, {
                    method: "PATCH",
                    headers: { "Content-Type": "application/json" },
                    body: JSON.stringify(body),
                });
                // Keep selection pointing at the (possibly re-slugged) display.
                if (selectedSlug === editingSlug) selectedSlug = updated.slug;
                toast(`Saved “${updated.name}”.`);
            } else {
                const created = await api("/api/displays", {
                    method: "POST",
                    headers: { "Content-Type": "application/json" },
                    body: JSON.stringify(body),
                });
                selectedSlug = created.slug;
                toast(`Created “${created.name}”.`);
            }
            closeDisplayForm();
            await load();
        } catch (err) {
            dispFormError.textContent = err.message;
        } finally {
            dispSubmitBtn.disabled = false;
        }
    });

    async function deleteDisplay(slug) {
        const d = displays.find(x => x.slug === slug);
        if (!confirm(`Delete display “${d?.name ?? slug}”? This removes its playlist (slides stay in the library).`)) return;
        try {
            await api(`/api/displays/${enc(slug)}`, { method: "DELETE" });
            if (selectedSlug === slug) selectedSlug = null;
            await load();
            toast("Display deleted.");
        } catch (err) {
            toast(`Delete failed: ${err.message}`, true);
        }
    }

    // ---- Render: slide library ------------------------------------------------
    function renderSlides() {
        const d = currentDisplay();
        const inPl = new Set((d?.playlist || []).map(s => s.id));

        if (!slides.length) {
            slidesEl.innerHTML = `<div class="empty">No slides yet. Upload some media.</div>`;
            return;
        }

        slidesEl.innerHTML = slides.map(s => {
            const added = inPl.has(s.id);
            const thumbInner = isVideo(s)
                ? `<div style="font-size:26px">▶</div>`
                : `<img src="${esc(s.mediaUrl)}" alt="" loading="lazy" />`;
            const addBtn = d
                ? (added
                    ? `<button class="btn ghost small" disabled>In playlist</button>`
                    : `<button class="btn small" data-add="${esc(s.id)}">+ Add</button>`)
                : `<button class="btn ghost small" disabled>Select display</button>`;
            // Per-slide duration editor (videos play to end, so show "auto" and disable).
            const dur = isVideo(s)
                ? `<span class="dur-auto" title="Videos play to their end">▶ auto</span>`
                : `<label class="dur" title="Seconds shown">
                       <input type="number" min="1" max="600" value="${Number(s.durationSeconds) || 10}"
                              data-dur="${esc(s.id)}" /> <span>s</span>
                   </label>`;
            return `
                <div class="slide${added ? " in-playlist" : ""}" data-id="${esc(s.id)}">
                    <button class="del" data-del="${esc(s.id)}" title="Delete slide">×</button>
                    <div class="thumb">${thumbInner}</div>
                    <div class="cap">
                        <span class="title">${esc(s.title)}</span>
                        <span class="type">${esc(s.mediaType)}</span>
                    </div>
                    <div class="actions">${addBtn}${dur}</div>
                </div>`;
        }).join("");

        slidesEl.querySelectorAll("[data-add]").forEach(btn => {
            btn.addEventListener("click", () => addToPlaylist(btn.dataset.add));
        });
        slidesEl.querySelectorAll(".del").forEach(btn => {
            btn.addEventListener("click", (e) => {
                e.stopPropagation();
                deleteSlide(btn.dataset.del);
            });
        });
        slidesEl.querySelectorAll("[data-dur]").forEach(input => {
            // Commit on change/blur; guard against no-op edits.
            input.addEventListener("change", () => {
                const slide = slides.find(s => s.id === input.dataset.dur);
                const val = parseInt(input.value, 10);
                if (!slide || !Number.isFinite(val) || val < 1) { input.value = slide?.durationSeconds ?? 10; return; }
                if (val === slide.durationSeconds) return;
                setDuration(input.dataset.dur, val, input);
            });
        });
    }

    // ---- Render: playlist -----------------------------------------------------
    function renderPlaylist() {
        const d = currentDisplay();
        if (!d) {
            playlistEl.innerHTML = `<div class="empty">No display selected.</div>`;
            return;
        }
        const pl = d.playlist || [];
        if (!pl.length) {
            playlistEl.innerHTML = `<div class="empty">Empty playlist. Add slides from the library.</div>`;
            return;
        }
        playlistEl.innerHTML = pl.map((s, i) => {
            const thumbInner = isVideo(s) ? "▶" : `<img src="${esc(s.mediaUrl)}" alt="" />`;
            const durLabel = isVideo(s) ? `<span class="pl-dur">auto</span>` : `<span class="pl-dur">${Number(s.durationSeconds) || 10}s</span>`;
            return `
                <div class="pl-item" data-id="${esc(s.id)}" data-index="${i}">
                    <div class="num">${i + 1}</div>
                    <div class="pl-thumb">${thumbInner}</div>
                    <div class="pl-title" title="${esc(s.title)}">${esc(s.title)}</div>
                    ${durLabel}
                    <div class="pl-controls">
                        <button class="btn ghost small" data-up="${esc(s.id)}" ${i === 0 ? "disabled" : ""}>↑</button>
                        <button class="btn ghost small" data-down="${esc(s.id)}" ${i === pl.length - 1 ? "disabled" : ""}>↓</button>
                        <button class="btn ghost small" data-remove="${esc(s.id)}" title="Remove">✕</button>
                    </div>
                </div>`;
        }).join("");

        playlistEl.querySelectorAll("[data-up]").forEach(b => b.addEventListener("click", () => move(b.dataset.up, -1)));
        playlistEl.querySelectorAll("[data-down]").forEach(b => b.addEventListener("click", () => move(b.dataset.down, +1)));
        playlistEl.querySelectorAll("[data-remove]").forEach(b => b.addEventListener("click", () => removeFromPlaylist(b.dataset.remove)));
    }

    function renderHints() {
        const d = currentDisplay();
        if (!d) {
            libHint.textContent = "No display selected.";
            plHint.textContent = "Select a display to edit its playlist.";
            clearPlaylistBtn.disabled = true;
            return;
        }
        libHint.innerHTML = `Click <strong>+ Add</strong> to add a slide to <strong>${esc(d.name)}</strong>.`;
        plHint.textContent = `Cycles in order. Up/down reorders, ✕ removes.`;
        clearPlaylistBtn.disabled = (d.playlist || []).length === 0;
    }

    // ---- Actions: playlist ----------------------------------------------------
    async function addToPlaylist(slideId) {
        if (!selectedSlug) { toast("Select a display first.", true); return; }
        await call(`/api/displays/${enc(selectedSlug)}/playlist/${enc(slideId)}`, "POST", `Added.`);
    }

    async function removeFromPlaylist(slideId) {
        if (!selectedSlug) return;
        await call(`/api/displays/${enc(selectedSlug)}/playlist/${enc(slideId)}`, "DELETE", "Removed.");
    }

    async function move(slideId, dir) {
        const d = currentDisplay();
        if (!d) return;
        const ids = (d.playlist || []).map(s => s.id);
        const i = ids.indexOf(slideId);
        const j = i + dir;
        if (i < 0 || j < 0 || j >= ids.length) return;
        [ids[i], ids[j]] = [ids[j], ids[i]];
        await setPlaylist(ids, "Reordered.");
    }

    async function setPlaylist(ids, okMsg) {
        if (!selectedSlug) return;
        await call(`/api/displays/${enc(selectedSlug)}/playlist`, "PUT", okMsg, { slideIds: ids });
    }

    clearPlaylistBtn.addEventListener("click", async () => {
        if (!selectedSlug) return;
        if (!confirm("Remove all slides from this display's playlist?")) return;
        await setPlaylist([], "Playlist cleared.");
    });

    // Shared: perform a request that returns the updated Display, refresh state.
    async function call(url, method, okMsg, body) {
        try {
            const updated = await api(url, {
                method,
                headers: body ? { "Content-Type": "application/json" } : undefined,
                body: body ? JSON.stringify(body) : undefined,
            });
            if (updated && updated.slug) {
                const idx = displays.findIndex(x => x.slug === updated.slug);
                if (idx >= 0) displays[idx] = updated;
            }
            renderAll();
            if (okMsg) toast(okMsg);
        } catch (err) {
            toast(`Failed: ${err.message}`, true);
        }
    }

    function enc(s) { return encodeURIComponent(s); }

    // ---- Actions: slides ------------------------------------------------------
    async function uploadSlide(event) {
        event.preventDefault();
        const file = fileInput.files && fileInput.files[0];
        if (!file) { toast("Choose a file to upload.", true); return; }

        const form = new FormData();
        form.append("file", file);
        if (titleInput.value.trim()) form.append("title", titleInput.value.trim());
        if (durationInput.value) form.append("durationSeconds", durationInput.value);

        uploadBtn.disabled = true;
        uploadBtn.textContent = "Uploading…";
        try {
            const created = await api("/api/slides", { method: "POST", body: form });
            slides.push(created);
            uploadForm.reset();
            durationInput.value = "10";
            renderSlides();
            toast(`Uploaded “${created.title}”.`);
        } catch (err) {
            toast(`Upload failed: ${err.message}`, true);
        } finally {
            uploadBtn.disabled = false;
            uploadBtn.textContent = "Upload";
        }
    }

    async function setDuration(slideId, seconds, inputEl) {
        try {
            const updated = await api(`/api/slides/${enc(slideId)}`, {
                method: "PATCH",
                headers: { "Content-Type": "application/json" },
                body: JSON.stringify({ durationSeconds: seconds }),
            });
            // Reflect in local library.
            const i = slides.findIndex(s => s.id === slideId);
            if (i >= 0) slides[i] = updated;

            // If this slide is in the selected display's playlist, push fresh timing to the TV.
            const d = currentDisplay();
            if (d && (d.playlist || []).some(s => s.id === slideId)) {
                await setPlaylist((d.playlist || []).map(s => s.id), null); // no toast, but pushes + re-renders
            } else {
                renderSlides();
            }
            toast(`Duration set to ${seconds}s.`);
        } catch (err) {
            renderSlides();   // restore the input to the persisted value
            toast(`Failed: ${err.message}`, true);
        }
    }

    async function deleteSlide(slideId) {
        const slide = slides.find(s => s.id === slideId);
        if (!confirm(`Delete slide “${slide?.title ?? slideId}”? Removes it from all playlists.`)) return;
        try {
            await api(`/api/slides/${enc(slideId)}`, { method: "DELETE" });
            slides = slides.filter(s => s.id !== slideId);
            // Displays may have changed (slide removed from playlists) — reload them.
            displays = (await api("/api/displays")) || [];
            renderAll();
            toast("Slide deleted.");
        } catch (err) {
            toast(`Delete failed: ${err.message}`, true);
        }
    }

    // ---- Wire up --------------------------------------------------------------
    refreshBtn.addEventListener("click", load);
    uploadForm.addEventListener("submit", uploadSlide);
    setInterval(() => { load(); }, 30000);   // keep online/status fresh

    load();
})();
