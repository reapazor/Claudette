#!/bin/sh
# Claudette's update helper for macOS (DESIGN.md §2, "Updating Claudette"). Claudette starts it, detached, once it has
# closed its tabs and written the restart snapshot, then quits. The helper:
#
#   1. waits for Claudette to quit,
#   2. swaps the new Claudette.app in for the old one (both in the same folder, so each move is a rename),
#   3. starts the new version with the restart's arguments, and waits up to 60 seconds for it to write the nonce to the
#      ready file,
#   4. if it doesn't, stops it, puts the old version back and starts that with the same arguments: it takes the tabs
#      back from the snapshot, as a source build does when a new build fails (DESIGN.md §9).
#
#   update-helper.sh <pid> <app> <staged app> <ready file> <nonce> <open|exec> <restart arguments...>
#
# "open" starts the app through Launch Services; "exec" runs its executable directly, for tests off macOS. Tests also
# shorten the waits with CLAUDETTE_UPDATE_QUIT_TICKS and CLAUDETTE_UPDATE_READY_TICKS.
set -u

pid="$1"; app="$2"; staged="$3"; ready="$4"; nonce="$5"; mode="$6"
shift 6
previous="$app.previous"
launched=""
quit_ticks="${CLAUDETTE_UPDATE_QUIT_TICKS:-1200}"    # of 0.1 s: 2 minutes
ready_ticks="${CLAUDETTE_UPDATE_READY_TICKS:-240}"   # of 0.25 s: 60 seconds

launch() {
  target="$1"; shift
  if [ "$mode" = open ]; then
    /usr/bin/open -n "$target" --args "$@"
  else
    "$target/Contents/MacOS/Claudette" "$@" >/dev/null 2>&1 &
    launched=$!
  fi
}

# 1. Wait up to 2 minutes for Claudette to quit.
i=0
while kill -0 "$pid" 2>/dev/null; do
  i=$((i + 1))
  if [ "$i" -ge "$quit_ticks" ]; then
    echo "Claudette didn't quit, so the update wasn't installed." >&2
    exit 1
  fi
  sleep 0.1
done

# 2. Swap. If either move fails, the old version is where it was, and it starts again.
rm -rf "$previous"
if ! mv "$app" "$previous"; then
  launch "$app" "$@"
  exit 1
fi
if ! mv "$staged" "$app"; then
  mv "$previous" "$app"
  launch "$app" "$@"
  exit 1
fi

# 3. Start the new version and wait for it to say it's up.
rm -f "$ready"
launch "$app" "$@"
i=0
while [ "$i" -lt "$ready_ticks" ]; do
  if [ "$(cat "$ready" 2>/dev/null)" = "$nonce" ]; then
    rm -rf "$previous"
    exit 0
  fi
  i=$((i + 1))
  sleep 0.25
done

# 4. It didn't start: stop it and go back.
if [ -n "$launched" ]; then
  kill "$launched" 2>/dev/null
else
  pkill -f "$app/Contents/MacOS/Claudette" 2>/dev/null
fi
sleep 1
rm -rf "$app"
mv "$previous" "$app"
launch "$app" "$@"
exit 1
