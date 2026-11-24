# PcMonitor - design

Data: 24/11/2026
Macchina: Lenovo ThinkBook 16 G8 IAL (21SK), Intel Core Ultra 7 255H, 64 GB,
Windows 11 Pro 26300. SSD: Crucial T500 1 TB (D:, bus PCI 1), WD SN5000S 1 TB
(C:, bus PCI 2).

## Perche'

L'utente sta diagnosticando da agosto crash, errori PCIe (WHEA) sull'SSD
Crucial e problemi termici (vedi `C:\Users\Damio\tuning-backup\DIAGNOSI-*.md`).
Oggi usa HWiNFO per vedere temperatura e throttling, ma vuole tenerlo chiuso.
Vuole vedere lo stato della macchina **a colpo d'occhio dalla barra delle
applicazioni** e poter **aprire una finestra con i dettagli** quando serve.

Successo = HWiNFO resta chiuso nell'uso quotidiano; un'occhiata alla barra
dice se la CPU e' in turbo, frenata (e perche') o calda, e se gli SSD sono
caldi o stanno generando errori PCIe.

## Obiettivi

- Due icone nell'area di notifica: una CPU, una SSD.
- Finestra di dettaglio con valori correnti, min/max di sessione, grafici
  degli ultimi 10 minuti, registro eventi di stato.
- Registro su file per l'analisi post-crash.
- Notifica Windows all'inizio di una tempesta WHEA.
- Avvio automatico all'accesso.

## Non obiettivi

- Temperature per singolo core, tensioni, frequenze per core, ventole
  (non leggibili senza driver; l'utente non le vuole).
- Controllo di ventole, profili energetici o limiti di potenza: sola lettura.
- Supporto ad altre macchine: i sensori sono quelli di questo hardware. Se
  un sensore manca, l'app lo mostra come "n/d" e continua.

## Anti-obiettivi (fallimento anche se "funziona")

- **Installare un driver kernel** (WinRing0, PawnIO, ecc.): PC aziendale con
  Defender for Endpoint (MsSense) e blocklist driver vulnerabili attiva.
- **Appesantire il sistema**: niente query WMI ogni secondo (fanno salire
  WmiPrvSE al 15-60% di un core, misurato oggi). Obiettivo < 1% CPU medio,
  < 40 MB RAM.
- **Toccare D:** con letture/scritture proprie (l'SSD sotto osservazione).
- Disabilitare o interferire con MsSense/FortiClient (vincolo generale).

## Fonti dati (verificate il 24/11/2026)

| Dato | Fonte | Permessi | Note |
|---|---|---|---|
| Temperatura CPU generica | PDH `\EsifDeviceInformation(*)\Temperature`, istanza `..._1` (T1) | **admin** | Combacia con HWiNFO "Package CPU" DTS (84 vs 84-86). Istanza `_0` = valore istantaneo a scatti (combacia con "Package CPU Enhanced"): usarla solo per il max. |
| Potenza package CPU | PDH `\EsifDeviceInformation(*)\RAPL Power` istanza `_0`, /10 = W | admin | 250-300 = 25-30 W vs HWiNFO 28,0 W |
| Potenza core IA | stessa, istanza `_1`, /10 = W | admin | 190-220 vs HWiNFO 20,9 W |
| Turbo | PDH `\Processor Information(_Total)\% Processor Performance` | utente | >100% = sopra la base (oggi fino a 225%) |
| Carico CPU | PDH `\Processor Information(_Total)\% Processor Utility` | utente | |
| Limite prestazioni | PDH `\Processor Information(_Total)\% Performance Limit` | utente | Resta 100 anche quando HWiNFO segnala limite di potenza: non basta da solo |
| Sensori piattaforma ACPI | PDH `\Thermal Zone Information(*)\High Precision Temperature` (decimi di K), `% Passive Limit` | utente | TZS0/TZS1. NON sono la temperatura CPU; solo dettaglio |
| Temperatura SSD + soglie | `IOCTL_STORAGE_QUERY_PROPERTY`, PropertyId 52 (StorageDeviceTemperatureProperty) su `\\.\PhysicalDriveN`, accesso 0 | **utente** | Restituisce anche WarningTemperature/CriticalTemperature (Crucial 86/88, WD 84/88). Usare il sensore indice 0 (composito). Non tocca i dati del disco |
| Velocita'/latenza disco | PDH `\PhysicalDisk(N C:)\Disk Read Bytes/sec`, `Disk Write Bytes/sec`, `Avg. Disk sec/Transfer` | utente | |
| Errori PCIe | Log System, provider `Microsoft-Windows-WHEA-Logger`, via `EventLogWatcher` (push, niente polling) | utente | Bus dal messaggio: regex `Bus[^:]*:(?:Device\|Dispositivo):(?:Function\|Funzione):\s*0x([0-9A-Fa-f]+):` (messaggi in italiano) |
| Alimentazione | `GetSystemPowerStatus` (AC, %) ogni 1 s; velocita' di carica/scarica (mW) con `IOCTL_BATTERY_QUERY_STATUS` ogni 10 s (vedi chiarimento 4) | utente | |
| Mappa disco -> lettera, modello, bus PCI | WMI/SetupAPI **una sola volta** all'avvio | utente | PhysicalDrive -> controller PnP -> `DEVPKEY_Device_LocationInfo` |

Tutti i contatori PDH vanno aggiunti con `PdhAddEnglishCounter` (Windows e'
in italiano: i nomi localizzati non sono affidabili).

## Approcci considerati

- **A. App C# .NET nella barra di sistema (scelto).** Un exe, WinForms,
  icone disegnate a runtime, PDH via P/Invoke. Leggera, nativa, compilabile
  con l'SDK .NET gia' installato (9/10/11). Scelta di Claude, l'utente non
  ha obiettato.
- B. Script PowerShell con NotifyIcon: piu' rapido da scrivere ma ~80 MB,
  non un vero exe, query lente.
- C. WPF/WinUI: grafica piu' curata ma piu' pesante e lenta da costruire;
  non serve per questo uso.

Barra delle applicazioni: Windows 11 non supporta deskband/widget nella
taskbar; l'area di notifica e' l'unica via. Ci stanno ~2 cifre per icona.

## Decisioni prese con l'utente

- Due icone separate (CPU e SSD).
- Temperatura CPU generica affidabile, non per core, senza driver.
- Indicare il tipo di rallentamento (potenza vs calore).
- Contenuto icone, colori, finestra, registro, notifica, autostart come
  descritti sotto (playback approvato: "va bene cosi', procedi").

## Comportamento

### Ciclo di lettura
- Ogni 1 s: PDH (tutti i contatori in una query), GetSystemPowerStatus.
- Ogni 5 s: IOCTL temperatura SSD.
- Ogni 10 s: velocita' di carica batteria; riga nel registro su file.
- WHEA: evento push.
- Storico in memoria: buffer circolare 600 campioni (10 min).

### Icona CPU
- Numero: T1 (temperatura generica), intero, 2 cifre (>=100 mostra "!!").
- Sfondo = stato, con priorita' dall'alto:
  1. **Rosso - freno termico**: T1 >= 95 C, oppure T0 >= 100 C, oppure un
     `% Passive Limit` < 100.
  2. **Arancione - limite di potenza**: carico >= 50%, T1 < 90 C e
     `% Processor Performance` sotto il 75% del massimo osservato nella
     sessione per almeno 3 s (la CPU ha margine termico ma non sale).
     Euristica: va dichiarata come stima nella finestra.
  3. **Blu - turbo**: `% Processor Performance` > 100.
  4. **Verde - normale**.
- Tooltip: `CPU 84 C - 28 W - turbo 215% - carico 40% - <stato>`.

### Icona SSD
- Numero: la temperatura piu' alta dei due dischi.
- Sfondo, con priorita':
  1. **Viola**: almeno un evento WHEA negli ultimi 10 minuti (resta viola
     10 minuti dall'ultimo evento).
  2. **Rosso**: temperatura >= soglia Warning del disco (86/84) o >= 80 C.
  3. **Arancione**: >= 70 C.
  4. **Verde**.
- Tooltip: `D: Crucial 52 C - C: WD 55 C - WHEA oggi: 0`.

### Finestra di dettaglio
Si apre cliccando una delle due icone (stessa finestra); chiudendola
l'app resta nell'area di notifica. Menu contestuale dell'icona: Apri,
Apri cartella log, Esci.

```
CPU   84 C (min 52 / max 97)   28 W (core 21 W)   turbo 215%   carico 40%
      Stato: limite di potenza (stima)   [grafico 10 min: temperatura + watt]
      Sensori piattaforma: TZS0 65 C  TZS1 50 C  limite passivo 100%
SSD   D: Crucial T500  bus 1  52 C (avviso 86)  lett 0 MB/s  scritt 0 MB/s  lat 0,1 ms
         WHEA: 0 oggi / 0 da avvio / ultimo: -
      C: WD SN5000S    bus 2  55 C (avviso 84)  lett 1 MB/s  scritt 3 MB/s  lat 0,2 ms
         WHEA: 0 oggi / 0 da avvio / ultimo: -
      [grafico 10 min temperature SSD]
Alim. collegato - batteria 66% - in carica 26 W
Eventi (sessione)
      17:40:12  CPU freno termico, 96 C, durata 45 s
      17:42:03  WHEA bus 2: 12 eventi in 1 min
      11:51:00  avvio
```
- La finestra aggiorna i valori solo quando e' visibile; i grafici si
  disegnano a mano (GDI+), niente librerie esterne.
- Interfaccia in italiano. Tema scuro.

### Registro eventi di stato
Cambi di stato CPU (con durata), soglie SSD superate, raffiche WHEA
(con bus e conteggio), avvio/uscita. In memoria (ultimi 200) e nel log.

### Registro su file
- Cartella: `%LOCALAPPDATA%\PcMonitor\logs\` (su C:, mai su D:).
- Un CSV al giorno, una riga ogni 10 s: ora, T1, T0 max del periodo, W
  package, W core, turbo%, carico%, limite%, stato CPU, TZS0, TZS1, temp
  D:, temp C:, MB/s lett/scritt per disco, WHEA nel periodo per bus, AC,
  batteria%, carica W.
- Eventi di stato in `events-AAAAMMGG.log`.
- Conservazione 7 giorni (pulizia all'avvio).
- Scrittura con flush a ogni riga (dopo un crash le ultime righe devono
  esserci).

### Notifica
Toast/balloon Windows solo quando inizia una raffica WHEA (>= 10 eventi in
60 s), al massimo una ogni 10 minuti. Nessuna notifica termica.

### Avvio e permessi
- I contatori ESIF richiedono amministratore. L'app si avvia all'accesso
  con un'**attivita' pianificata** "Esegui con i privilegi piu' elevati",
  trigger all'accesso dell'utente, ritardo 30 s: nessun prompt UAC.
- Se avviata senza privilegi (doppio clic), funziona lo stesso: CPU temp e
  watt mostrano "n/d" e l'icona CPU mostra il turbo/carico; un avviso nella
  finestra spiega come avviarla elevata.
- Istanza singola (mutex): un secondo avvio porta in primo piano la finestra.
- Comandi `PcMonitor.exe --install` / `--uninstall` creano/rimuovono
  l'attivita' pianificata (richiedono admin).

### Robustezza
- Ogni fonte e' indipendente: se una fallisce, il suo valore e' "n/d" e le
  altre continuano. Nessuna eccezione deve chiudere l'app.
- Gestire ripresa da standby (i contatori PDH possono restituire errori
  per qualche secondo; il watcher WHEA deve restare attivo).
- Gestire cambio DPI / monitor (icone ridisegnate alla dimensione corrente).

## Build e distribuzione

- Sorgente: `C:\Git\pc-monitor` (repo git, da inizializzare all'inizio
  dell'implementazione: vedi Task 1 del piano).
- .NET 10 (LTS), WinForms, `PublishSingleFile`, framework-dependent
  (runtime gia' presente) per restare piccolo.
- Installazione: `C:\Users\Damio\AppData\Local\PcMonitor\PcMonitor.exe`.

## Chiarimenti dopo la revisione (decisioni di Claude)

1. **Attribuzione WHEA.** Dal messaggio si estrae il B:D:F completo
   (`0xB:0xD:0xF`). All'avvio si costruisce una mappa dal device tree PnP:
   - endpoint SSD (bus 1 o 2, dev 0, fn 0) -> quel disco;
   - root port a monte di ogni SSD (`DEVPKEY_Device_Parent` del
     controller NVMe, LocationInfo) -> quel disco, contato a parte come
     "lato root port". Oggi: Crucial dietro `0:6:0` (DEV_7ECA), WD dietro
     `0:6:2` (DEV_7ECB). La mappa va letta a runtime, non cablata: dopo lo
     scambio del 24/11 il root port 7ECB, storicamente del Crucial, e'
     passato al WD.
   - WHEA senza B:D:F o con B:D:F non mappato (CPU, memoria, altri
     dispositivi PCIe): contatore "altri WHEA" mostrato nella sezione CPU.
     **Non** colorano di viola l'icona SSD.
   - Colonne CSV: WHEA endpoint e root port per ciascun disco + altri.
2. **"Da avvio" = dall'avvio di Windows.** All'avvio dell'app un thread in
   background conta gli eventi WHEA da `LastBootUpTime` (query XPath sul
   log System, lettura a blocchi, solo i campi necessari). Anche migliaia
   di eventi sono un costo una tantum di pochi secondi, fuori dal thread
   UI. Lo stato viola considera anche eventi avvenuti prima dell'avvio
   dell'app se rientrano negli ultimi 10 minuti.
3. **Elevazione.** Verificato: l'account `Damio` e' membro di
   Administrators (UAC con lo stesso account). L'attivita' pianificata con
   privilegi piu' elevati gira nel profilo di Damio; `%LOCALAPPDATA%` e'
   quello giusto.
4. **Memoria.** 40 MB e' un obiettivo, misurato come private bytes con la
   finestra chiusa, non un vincolo di fallimento. Scelte per restarci:
   la finestra viene distrutta alla chiusura (non nascosta); niente
   `System.Management`: la velocita' di carica si legge con
   `IOCTL_BATTERY_QUERY_STATUS` (SetupAPI su GUID_DEVCLASS_BATTERY);
   buffer dei grafici come array di struct.
5. **Frequenza lettura temperatura SSD: ogni 5 s**, non ogni 1 s. La
   temperatura di un SSD cambia lentamente e ogni lettura e' un comando
   sul link PCIe: si disturba il meno possibile il Crucial a riposo.
   Velocita'/latenza disco restano a 1 s (contatori del sistema
   operativo, nessun comando al disco).

## Lasciato al costruttore

- Struttura interna del codice, nomi delle classi.
- Font/disegno esatto delle cifre nell'icona 16x16 (leggibilita' a 100% e
  125-150% di scala).
- Colori esatti della palette (rosso/arancione/blu/verde/viola leggibili su
  barra chiara e scura).
- Layout preciso della finestra.
- Mappatura istanze ESIF: le istanze attuali sono
  `pci\ven_8086&dev_7d03&...\3&11583659&1&20_0.._8`; individuarle per
  suffisso `_0`/`_1`, non per nome completo.
