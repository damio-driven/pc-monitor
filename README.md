# PcMonitor

App per l'area di notifica: temperatura e stato della CPU (turbo, limite di potenza, freno termico), temperatura ed errori PCIe (WHEA) dei due SSD. Clic su un'icona: finestra di dettaglio. Nessun driver.

- **Compilare:** `dotnet publish src/PcMonitor -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o publish`
- **Installare:** `publish\PcMonitor.exe --install` (chiede l'elevazione). Copia l'exe in `C:\Eseguibili\_\PcMonitor\` (cartella protetta: ci scrivono solo gli amministratori) e crea l'attivita' pianificata `PcMonitor` (all'accesso, privilegi piu' elevati, ritardo 30 s, anche a batteria).
- **Disinstallare:** `PcMonitor.exe --uninstall` (rimuove l'attivita'; poi cancellare a mano `C:\Eseguibili\_\PcMonitor\` e, se non servono i log, `%LOCALAPPDATA%\PcMonitor\`).
- **Diagnostica:** `PcMonitor.exe --probe` stampa le letture di tutti i sensori.
- **Log:** `%LOCALAPPDATA%\PcMonitor\logs\` (CSV ogni 10 s ed eventi, 7 giorni).
- Senza privilegi elevati temperatura e watt CPU risultano "n/d".

Progetto e vincoli: `docs/superpowers/specs/2026-11-24-pcmonitor-design.md`, `CLAUDE.md`.
