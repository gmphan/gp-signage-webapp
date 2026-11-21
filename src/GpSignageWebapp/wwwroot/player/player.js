// Signage player: connects to the SignalR hub, joins a display group,
// then cycles through the display's playlist (crossfade between images,
// videos play to their end). Runs in a kiosk browser on the Pi.
//
// URL: https://<host>/player/?display=<slug>

(() => {
    "use strict";

    // ---- Config ---------------------------------------------------------------
    const HEARTBEAT_MS = 30_000;          // report liveness to the server
    const STATUS_HIDE_MS = 4_000;         // auto-hide the status pill
    const DEFAULT_DURATION_S = 10;        // fallback if a slide has no duration
    const CROSSFADE_MS = 600;             // must match CSS .media-img transition

    // ---- DOM ------------------------------------------------------------------
    const idleEl = document.getElementById("idle");
    const imgA = document.getElementById("imgA");
    const imgB = document.getElementById("imgB");
    const videoEl = document.getElementById("mediaVideo");
    const statusEl = document.getElementById("status");

    // ---- State ----------------------------------------------------------------
    const params = new URLSearchParams(location.search);
    const slug = (params.get("display") || "").trim().toLowerCase();

    let playlist = [];          // array of slide objects
    let index = 0;              // current position in playlist
    let frontLayer = imgA;      // which <img> is currently visible
    let backLayer = imgB;
    let advanceTimer = null;    // setTimeout handle for image dwell
    let heartbeatTimer = null;
    let rotationToken = 0;      // invalidates stale timers when playlist changes
    let loadSeq = 0;            // unique id per image load; guards against stale onload

    // Debug tracing — enable with ?debug=1 on the player URL (or window.__signageDebug=true).
    let DEBUG = params.get("debug") === "1";
    window.__signageDebug = DEBUG;
    function trace(...args) { if (DEBUG || window.__signageDebug) console.log("[trace]", ...args); }

    // ---- Status pill ----------------------------------------------------------
    let statusHideTimer = null;
    function setStatus(text, isError) {
        statusEl.textContent = text;
        statusEl.classList.add("show");
        statusEl.classList.toggle("err", !!isError);
        if (statusHideTimer) clearTimeout(statusHideTimer);
        if (!isError) {
            statusHideTimer = setTimeout(() => statusEl.classList.remove("show"), STATUS_HIDE_MS);
        }
    }

    // ---- Helpers --------------------------------------------------------------
    function absolute(url) {
        if (!url) return url;
        return url.startsWith("http") || url.startsWith("/") ? url : "/" + url;
    }

    function isVideo(slide) {
        return (slide.mediaType || "").toLowerCase() === "video";
    }

    // Per-slide fit: "Cover" fills the screen (crops), anything else = "Contain" (letterbox).
    function fitFor(slide) {
        return (slide && String(slide.fit || "").toLowerCase() === "cover") ? "cover" : "contain";
    }

    // ---- Preloading -----------------------------------------------------------
    // Large images (e.g. full-res PNG screenshots) decode slowly; the dwell timer
    // only starts after an image loads, so a slow load makes the PREVIOUS slide
    // linger and the rotation look frozen. Decoding upcoming slides in the
    // background makes their reveal instant.
    const preloadCache = new Map();   // url -> HTMLImageElement (kept to pin decoded frames)
    const PRELOAD_AHEAD = 2;          // how many upcoming slides to preload

    function preloadUrl(url) {
        if (!url) return;
        const abs = absolute(url);
        if (preloadCache.has(abs)) return;
        const img = new Image();
        img.decoding = "sync";
        img.onload = () => { if (img.decode) img.decode().catch(() => { /* ignore */ }); };
        img.onerror = () => { preloadCache.delete(abs); };
        img.src = abs;
        preloadCache.set(abs, img);
        // Bound the cache so we don't hold every image forever.
        while (preloadCache.size > 12) {
            const oldest = preloadCache.keys().next().value;
            preloadCache.delete(oldest);
        }
    }

    function preloadUpcoming(fromIndex) {
        if (!playlist.length) return;
        for (let k = 1; k <= PRELOAD_AHEAD; k++) {
            const s = playlist[(fromIndex + k) % playlist.length];
            if (s && s.mediaUrl && !isVideo(s)) preloadUrl(s.mediaUrl);
        }
    }

    // ---- Rendering ------------------------------------------------------------
    function showIdle(message) {
        stopRotation();
        playlist = [];
        imgA.classList.remove("visible");
        imgB.classList.remove("visible");
        videoEl.hidden = true;
        videoEl.pause();
        videoEl.removeAttribute("src");
        idleEl.style.display = "block";
        if (message) idleEl.textContent = message;
    }

    // Crossfade to an image slide using the two stacked layers.
    //
    // The two <img> layers are reused across cycles, so a `load` event from a PREVIOUS
    // src on the same element can still fire after we start loading the next one. We tag
    // each load with a `loadSeq` id; the handler only acts if the element's own
    // dataset.seq still matches — otherwise it's a stale event and is ignored.
    function showImage(slide, token) {
        if (token !== rotationToken) return;

        const nextUrl = absolute(slide.mediaUrl);

        // Always render into the layer that is currently hidden (the back layer).
        const layer = backLayer;
        const seq = ++loadSeq;
        layer.dataset.seq = String(seq);
        layer.style.objectFit = fitFor(slide);   // per-slide: cover (fill/crop) or contain (bars)
        trace("showImage", slide.mediaUrl, "-> layer", layer.id, "seq", seq, "index", index, "fit", fitFor(slide));

        let settled = false;

        const onLoaded = () => {
            // Ignore if this element has since been given a newer load, or rotation moved on.
            if (settled || layer.dataset.seq !== String(seq)) {
                trace("onLoaded IGNORED", slide.mediaUrl, "layer", layer.id, "settled", settled,
                      "seqMatch", layer.dataset.seq === String(seq));
                return;
            }
            settled = true;
            if (token !== rotationToken) { trace("onLoaded stale token", slide.mediaUrl); return; }

            // Crossfade: reveal the layer we just filled, hide the other.
            const other = (layer === imgA) ? imgB : imgA;
            layer.classList.add("visible");
            other.classList.remove("visible");
            frontLayer = layer;
            backLayer = other;
            trace("REVEAL", layer.id, "hide", other.id, "slide", slide.mediaUrl, "dwell", durationMs(slide));

            idleEl.style.display = "none";
            videoEl.hidden = true;
            preloadUpcoming(index);          // decode the next slides while this one shows
            scheduleAdvance(durationMs(slide), token);
        };

        const onFailed = () => {
            if (settled || layer.dataset.seq !== String(seq)) return;
            settled = true;
            if (token !== rotationToken) return;
            console.warn("[player] image failed to load:", nextUrl);
            scheduleAdvance(1500, token);   // skip broken slide after a short delay
        };

        layer.onload = onLoaded;
        layer.onerror = onFailed;
        layer.src = nextUrl;

        // If the image is already in the memory cache, `complete`/`naturalWidth` are true
        // synchronously and the native `load` may not fire — so resolve now.
        // If it's NOT cached, the native `load` event resolves it later.
        // Either way the seq/settled guards ensure we advance EXACTLY once
        // (Chromium also fires `load` async even for cached images — that's the double-advance
        // that made the player skip a slide and appear stuck).
        if (layer.complete && layer.naturalWidth > 0) {
            trace("cached fast-path for", slide.mediaUrl);
            onLoaded();
        }
    }

    // Play a video slide. Advance when it ENDS (normal path) — but ALWAYS have a hard
    // safety timeout so a video can never wedge the rotation:
    //   - `ended` may never fire (autoplay quirks, odd encodes, backgrounded tab, etc.)
    //   - we cap dwell at the real video length + a small buffer once metadata is known.
    // A single `settled` guard ensures only ONE advance happens (ended OR timeout OR error).
    function showVideo(slide, token) {
        if (token !== rotationToken) return;

        imgA.classList.remove("visible");
        imgB.classList.remove("visible");
        idleEl.style.display = "none";

        // Invalidate any in-flight image load so it can't reveal over the video.
        loadSeq++;

        const SAFETY_MIN_MS = 5000;      // if we can't read duration, advance after this
        const BUFFER_MS = 1000;          // grace period past the reported video length

        let settled = false;
        let safetyTimer = null;

        const advanceOnce = (why) => {
            if (settled) return;
            settled = true;
            if (safetyTimer) clearTimeout(safetyTimer);
            if (token !== rotationToken) return;
            console.log(`[player] video advance (${why})`);
            advance(token);
        };

        const skipAfter = (why) => {
            if (settled) return;
            settled = true;
            if (safetyTimer) clearTimeout(safetyTimer);
            if (token !== rotationToken) return;
            console.warn(`[player] skipping video (${why})`);
            scheduleAdvance(1500, token);
        };

        // Arm (or re-arm) the safety timeout based on known duration.
        const armSafety = (ms) => {
            if (safetyTimer) clearTimeout(safetyTimer);
            safetyTimer = setTimeout(() => advanceOnce("safety-timeout"), Math.max(ms, SAFETY_MIN_MS));
        };

        videoEl.hidden = false;
        videoEl.onended = () => advanceOnce("ended");
        videoEl.onerror = () => skipAfter("error");

        // Preload the upcoming slides while the video plays.
        preloadUpcoming(index);

        // Once metadata is available, we know the true length -> set the safety cap.
        videoEl.onloadedmetadata = () => {
            if (token !== rotationToken || settled) return;
            const dur = videoEl.duration;
            if (Number.isFinite(dur) && dur > 0) {
                armSafety(dur * 1000 + BUFFER_MS);
            }
        };

        // Fallback baseline in case metadata never loads.
        armSafety(SAFETY_MIN_MS + 10000);

        videoEl.src = absolute(slide.mediaUrl);
        videoEl.load();
        videoEl.play().catch(() => {
            // Autoplay blocked; fall back to a timed advance so we don't get stuck.
            if (settled || token !== rotationToken) return;
            scheduleAdvance(durationMs(slide), token);
        });
    }

    function durationMs(slide) {
        const s = Number(slide && slide.durationSeconds);
        return (Number.isFinite(s) && s > 0 ? s : DEFAULT_DURATION_S) * 1000;
    }

    function scheduleAdvance(ms, token) {
        clearTimeout(advanceTimer);
        advanceTimer = setTimeout(() => advance(token), ms);
    }

    function showSlide(slide, token) {
        if (!slide || !slide.mediaUrl) { advance(token); return; }
        if (isVideo(slide)) showVideo(slide, token);
        else showImage(slide, token);
    }

    function advance(token) {
        if (token !== rotationToken) { trace("advance IGNORED (stale token)"); return; }
        if (!playlist.length) { showIdle("Waiting for content…"); return; }
        index = (index + 1) % playlist.length;
        trace("ADVANCE -> index", index, "of", playlist.length, "(", playlist[index].mediaUrl, ")");
        showSlide(playlist[index], token);
    }

    function stopRotation() {
        rotationToken++;          // invalidate any pending callbacks
        clearTimeout(advanceTimer);
        advanceTimer = null;
    }

    // Start (or restart) rotation from the first slide.
    function playPlaylist(slides) {
        stopRotation();
        const token = rotationToken;

        playlist = Array.isArray(slides) ? slides.filter(s => s && s.mediaUrl) : [];
        trace("playPlaylist: count", playlist.length, "token", token,
              playlist.map(s => s.mediaUrl));
        if (!playlist.length) { showIdle("Waiting for content…"); return; }

        index = 0;
        idleEl.style.display = "none";
        // Kick off background decoding of upcoming slides immediately.
        preloadUpcoming(0);
        showSlide(playlist[0], token);
    }

    // ---- Fatal states ---------------------------------------------------------
    function showFatal(message) {
        setStatus(message, true);
        showIdle(message);
        idleEl.style.color = "#b00";
    }

    // ---- SignalR --------------------------------------------------------------
    function startHeartbeat(connection) {
        if (heartbeatTimer) clearInterval(heartbeatTimer);
        heartbeatTimer = setInterval(() => {
            if (connection.state === signalR.HubConnectionState.Connected) {
                connection.invoke("Heartbeat", slug).catch(() => { /* ignore */ });
            }
        }, HEARTBEAT_MS);
    }

    async function start() {
        if (!slug) {
            showFatal("Missing ?display=<slug> in the URL.");
            return;
        }

        const connection = new signalR.HubConnectionBuilder()
            .withUrl("/hubs/signage")
            .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
            .configureLogging(signalR.LogLevel.Information)
            .build();

        window.__hub = connection;   // for manual inspection in DevTools

        // Server -> client: new playlist (sent on join and whenever admin changes it).
        connection.on("ShowPlaylist", (payload) => {
            console.log("[ShowPlaylist] received:", payload);
            playPlaylist(payload && payload.slides ? payload.slides : []);
        });

        // Backward-compat: a single slide push behaves like a 1-item playlist.
        connection.on("ShowSlide", (slide) => {
            console.log("[ShowSlide] received:", slide);
            playPlaylist(slide ? [slide] : []);
        });

        // Admin renamed the display's slug — rejoin under the new slug.
        connection.on("SlugChanged", async (newSlug) => {
            console.log("[SlugChanged] new slug:", newSlug);
            if (!newSlug) return;
            // Update the URL so a manual reload keeps working, then rejoin.
            const url = new URL(location.href);
            url.searchParams.set("display", newSlug);
            history.replaceState(null, "", url);
            playPlaylist([]);            // clear until the server sends the new playlist
            try {
                await connection.invoke("JoinDisplay", newSlug);
                setStatus(`Renamed to "${newSlug}"`);
            } catch (e) { console.error("Rejoin after rename failed", e); }
        });

        // Admin deleted this display — go idle with a message.
        connection.on("DisplayDeleted", () => {
            console.log("[DisplayDeleted]");
            showIdle("This display was deleted.");
        });

        connection.onreconnecting(() => setStatus("Reconnecting…", true));
        connection.onreconnected(async () => {
            setStatus("Connected");
            try { await joinDisplay(connection); }
            catch (e) { console.error("Re-join failed", e); }
        });
        connection.onclose(async () => {
            setStatus("Disconnected — retrying…", true);
            await new Promise(r => setTimeout(r, 5000));
            start();
        });

        async function joinDisplay(conn) {
            setStatus("Connected");
            await conn.invoke("JoinDisplay", slug);
            console.log("[JoinDisplay] joined:", slug);
        }

        try {
            await connection.start();
            await joinDisplay(connection);
            startHeartbeat(connection);
        } catch (err) {
            const msg = (err && err.message) ? err.message : String(err);
            console.error("SignalR start/join failed:", err);
            if (msg.includes("Unknown display")) {
                showFatal(`Unknown display "${slug}". Check the URL slug.`);
            } else {
                setStatus("Cannot connect — retrying…", true);
                setTimeout(start, 5000);
            }
        }
    }

    window.addEventListener("load", start);
})();
