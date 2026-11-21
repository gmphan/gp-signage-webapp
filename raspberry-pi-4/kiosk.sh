#!/bin/bash
# GP Signage kiosk launcher (Wayland / labwc, Raspberry Pi OS Trixie)
set -u

# ---- Settings (this Pi) ----
SLUG="signage-slug-one"
ROTATE="normal"          # landscape: normal, portrait: 90 (or 270 if it comes out upside down)
# ROTATE="90"            # portrait: 90 (or 270 if it comes out upside down)
URL_HOST="signage.giangmichaelphan.com"
# -----------------------------

export XDG_RUNTIME_DIR="/run/user/$(id -u)"
export WAYLAND_DISPLAY="wayland-0"

# Wait for the Wayland compositor socket
for i in $(seq 1 30); do
  [ -S "$XDG_RUNTIME_DIR/$WAYLAND_DISPLAY" ] && break
  sleep 1
done

# Auto-detect the connected HDMI output (HDMI-A-1 on the TV, HDMI-A-2 on the Dell/DVI now)
detect_output() {
  wlr-randr 2>/dev/null | grep -E '^HDMI-A-[0-9]+ ' | head -n1 | awk '{print $1}'
}

OUTPUT="$(detect_output)"
if [ -n "${OUTPUT}" ]; then
  for i in $(seq 1 20); do
    wlr-randr --output "$OUTPUT" --transform "$ROTATE" 2>/dev/null && break
    sleep 1
  done
fi

# Keep the screen awake (best-effort)
wlopm --on '*' 2>/dev/null || true

# Cache-buster: unique per launch so Chromium always fetches the latest player.js
URL="https://${URL_HOST}/player/?display=${SLUG}&v=$(date +%s)"

# Relaunch Chromium forever if it ever exits
while true; do
  chromium \
    --ozone-platform=wayland \
    --kiosk \
    --noerrdialogs \
    --disable-infobars \
    --disable-session-crashed-bubble \
    --no-first-run \
    --autoplay-policy=no-user-gesture-required \
    --check-for-update-interval=31536000 \
    --disable-features=Translate \
    "$URL"
  sleep 3
done