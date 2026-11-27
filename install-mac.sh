#!/bin/bash
# RemoteScreen – instalace aplikace do složky Aplikace.
#
#   bash install-mac.sh             instalace / aktualizace (spustit ve složce s RemoteScreen.app)
#   bash install-mac.sh uninstall   odinstalace (nastavení v ~/.remotescreen zůstane)
#
# Ostatní (stav, restart, ukončení, spouštění po přihlášení) se ovládá ikonou RemoteScreen v horní liště.

set -euo pipefail

BUNDLE_ID="cz.remotescreen.app"
DEST="/Applications/RemoteScreen.app"
HERE="$(cd "$(dirname "$0")" && pwd)"

green()  { printf '\033[32m%s\033[0m\n' "$*"; }
yellow() { printf '\033[33m%s\033[0m\n' "$*"; }
red()    { printf '\033[31m%s\033[0m\n' "$*"; }

quit_running() {
    osascript -e "tell application id \"$BUNDLE_ID\" to quit" >/dev/null 2>&1 || true
    sleep 1
    pkill -x RemoteScreen 2>/dev/null || true
    pkill -x RemoteScreen.Server 2>/dev/null || true
}

remove_old_version() {
    # předchozí název aplikace: MacRemote (všechny verze) – ukončit a odstranit
    osascript -e 'tell application id "cz.macremote.app" to quit' >/dev/null 2>&1 || true
    pkill -x MacRemote 2>/dev/null || true
    pkill -x MacRemote.Server 2>/dev/null || true
    for LABEL in cz.macremote.server cz.macremote.app; do
        local PL="$HOME/Library/LaunchAgents/$LABEL.plist"
        if [ -f "$PL" ]; then
            launchctl bootout "gui/$(id -u)/$LABEL" 2>/dev/null || true
            rm -f "$PL"
        fi
    done
    rm -rf "$HOME/Applications/MacRemote.app" "$HOME/Applications/RemoteScreen.app"
    if [ -d "/Applications/MacRemote.app" ]; then
        echo "Odstraňuji starou aplikaci MacRemote…"
        rm -rf "/Applications/MacRemote.app" 2>/dev/null || sudo rm -rf "/Applications/MacRemote.app"
        tccutil reset ScreenCapture cz.macremote.app >/dev/null 2>&1 || true
        tccutil reset Accessibility cz.macremote.app >/dev/null 2>&1 || true
        tccutil reset SystemPolicyAllFiles cz.macremote.app >/dev/null 2>&1 || true
        echo "Nastavení (heslo, certifikát) z MacRemote se převezme automaticky."
    fi
}

install() {
    local SRC="$HERE/RemoteScreen.app"
    if [ ! -d "$SRC" ]; then
        red "Vedle skriptu chybí RemoteScreen.app ($SRC)."
        exit 1
    fi

    local UPDATE=0
    [ -d "$DEST" ] && UPDATE=1

    echo "Ukončuji běžící RemoteScreen…"
    quit_running
    remove_old_version

    echo "Kopíruji do $DEST"
    rm -rf "$DEST"
    if ! ditto "$SRC" "$DEST" 2>/dev/null; then
        yellow "Do složky Aplikace nelze zapisovat bez hesla správce – zadejte ho:"
        sudo ditto "$SRC" "$DEST"
        sudo chown -R "$(id -un)" "$DEST"
    fi
    chmod +x "$DEST/Contents/MacOS/RemoteScreen"
    xattr -cr "$DEST" 2>/dev/null || true
    codesign --force --deep -s - "$DEST"
    # obnovit ikonu v mezipaměti systému (jinak může Finder/Dock ukazovat starou)
    touch "$DEST"
    /System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -f "$DEST" >/dev/null 2>&1 || true

    if [ "$UPDATE" = 1 ]; then
        # nová verze = nový podpis → stará oprávnění by tiše přestala platit
        tccutil reset ScreenCapture "$BUNDLE_ID" >/dev/null 2>&1 || true
        tccutil reset Accessibility "$BUNDLE_ID" >/dev/null 2>&1 || true
    fi

    echo "Spouštím RemoteScreen…"
    open "$DEST"

    echo
    green "Hotovo! RemoteScreen je ve složce Aplikace a jeho ikona je v horní liště vpravo."
    echo "Automatické spouštění po přihlášení zapnete v menu ikony: Spouštět po přihlášení."
    echo
    echo "Co dál:"
    echo "  • Při prvním spuštění se aplikace zeptá na heslo pro připojení z Windows."
    echo "  • macOS požádá o oprávnění Nahrávání obrazovky a Zpřístupnění – obě povolte."
    echo "    (Stav oprávnění a odkazy do Nastavení najdete v menu ikony RemoteScreen.)"
    echo "  • Po povolení oprávnění zvolte v menu RemoteScreen → Restartovat server."
    if [ "$UPDATE" = 1 ]; then
        echo
        yellow "Šlo o aktualizaci: oprávnění je potřeba povolit znovu. Pokud je RemoteScreen v seznamu"
        yellow "zapnutý, ale nefunguje, odeberte ho tlačítkem − a přidejte znovu (+)."
    fi
}

uninstall() {
    quit_running
    remove_old_version
    rm -f "$HOME/Library/LaunchAgents/$BUNDLE_ID.plist"
    if [ -d "$DEST" ]; then
        rm -rf "$DEST" 2>/dev/null || sudo rm -rf "$DEST"
    fi
    tccutil reset ScreenCapture "$BUNDLE_ID" >/dev/null 2>&1 || true
    tccutil reset Accessibility "$BUNDLE_ID" >/dev/null 2>&1 || true
    tccutil reset SystemPolicyAllFiles "$BUNDLE_ID" >/dev/null 2>&1 || true
    green "RemoteScreen odinstalován. Nastavení (heslo, certifikát) zůstalo v ~/.remotescreen – smažete ho: rm -rf ~/.remotescreen"
    echo "Pokud RemoteScreen zůstal v Nastavení → Obecné → Položky při přihlášení, odeberte ho tam."
}

case "${1:-install}" in
    install)   install ;;
    uninstall) uninstall ;;
    *)         echo "Použití: bash install-mac.sh [uninstall]"; exit 1 ;;
esac
