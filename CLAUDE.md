# PcMonitor - note di progetto

App tray Windows leggera (due icone: CPU e SSD + finestra di dettaglio) per
monitorare temperatura, turbo/throttling della CPU, temperatura ed errori
PCIe (WHEA) dei due SSD, cosi' l'utente puo' tenere chiuso HWiNFO.

- Spec: `docs/superpowers/specs/2026-11-24-pcmonitor-design.md`
- Piano: `docs/superpowers/plans/2026-11-24-pcmonitor.md`
- Il repository git va inizializzato all'inizio dell'implementazione
  (Task 1, Step 0 del piano). Usare l'identita' git globale gia' configurata.

## Macchina (unica destinazione)

Lenovo ThinkBook 16 G8 IAL (21SK), Intel Core Ultra 7 255H (TjMax 105 C),
64 GB, Windows 11 Pro 26300 **in italiano**, BIOS RKCN27WW.

| Disco | Lettera | Endpoint PCI | Root port a monte | Soglie NVMe (avviso/critico) |
|---|---|---|---|---|
| Crucial T500 1 TB (CT1000T500SSD8, fw P8CR004) | D: | bus 1 (1:0:0) | 0:6:0 (VEN_8086&DEV_7ECA) | 86 / 88 C |
| WD PC SN5000S 1 TB | C: | bus 2 (2:0:0) | 0:6:2 (VEN_8086&DEV_7ECB) | 84 / 88 C |

I due SSD sono stati scambiati di slot il 24/11/2026: prima il Crucial era
sul bus 2. Gli eventi WHEA storici (fino al 24/11 09:09) con bus 2 erano
del Crucial. La mappa disco -> bus va sempre letta a runtime.

## Fonti dati verificate il 24/11/2026

- Temperatura CPU senza driver: contatore Intel DTT
  `\EsifDeviceInformation(*)\Temperature`, istanza con suffisso `_1`
  (combacia con HWiNFO "Package CPU" DTS). Suffisso `_0` = valore istantaneo
  a scatti (HWiNFO "Package CPU Enhanced").
- Potenza: `\EsifDeviceInformation(*)\RAPL Power` `_0`/10 = W package
  (HWiNFO "Potenza totale CPU"), `_1`/10 = W core IA.
- **I contatori ESIF richiedono amministratore** (da utente normale: "oggetto
  non trovato" via PDH, "Accesso negato" via WMI).
- Temperatura NVMe: `IOCTL_STORAGE_QUERY_PROPERTY` PropertyId 52 su
  `\\.\PhysicalDriveN` con access mask 0: funziona **senza** amministratore
  e restituisce anche le soglie. `Get-StorageReliabilityCounter` invece
  richiede admin.
- I sensori ACPI `\_TZ.TZS0` / `TZS1` NON sono la temperatura CPU (TZS0
  leggeva 88-98 C con core a 67-84 C).
- `% Performance Limit` resta 100 anche quando HWiNFO segnala limite di
  potenza: il tipo di freno e' una stima (vedi spec).
- Messaggi WHEA in italiano: `Bus primario:Dispositivo:Funzione: 0x2:0x0:0x0`.

## Vincoli tassativi

- **Nessun driver kernel** (WinRing0, PawnIO, ecc.): PC con Defender for
  Endpoint aziendale (MsSense, tenant Intune) e blocklist driver vulnerabili
  attiva. Niente pacchetto `System.Management`.
- **Mai disabilitare, fermare o configurare MsSense/Defender o FortiClient**
  (servizi FortiClient/FA_Scheduler servono per il lavoro).
- **Non leggere ne' scrivere file su D:** (SSD sotto osservazione). Sui dischi
  solo IOCTL di query con access mask 0.
- Nomi dei contatori: Windows e' in italiano. Con l'API PDH usare solo
  `PdhAddEnglishCounterW`. Da PowerShell `Get-Counter` con nomi inglesi a
  volte fallisce: per verifiche manuali usare le classi
  `Win32_PerfFormattedData_*` (ma mai nell'app: WMI ogni secondo fa salire
  WmiPrvSE al 15-60% di un core).
- Script `.ps1` eseguiti con Windows PowerShell 5.1: solo ASCII (niente
  lettere accentate o trattini lunghi) e UTF-8 con BOM, altrimenti falliscono
  in parsing senza errori visibili.
- Non toccare `C:\Program Files\Microsoft SQL Server\MSSQL16.MSSQLSERVER`
  (dati SQL Server live).

## Ambiente

- .NET SDK 9, 10 e 11 installati; target `net10.0-windows`. NuGet.org raggiungibile.
- L'account `Damio` e' amministratore locale (UAC con lo stesso account).
- Prove senza privilegi: `runas /trustlevel:0x20000 "<comando che scrive su file>"`.
- HWiNFO portable in `C:\Eseguibili\_\hwi_846\HWiNFO64.exe` (memoria
  condivisa disattivata): utile per confronti manuali, chiedere all'utente
  uno screenshot.
- Diagnosi storiche della macchina: `C:\Users\Damio\tuning-backup\DIAGNOSI-SSD-CRUCIAL.md`,
  `DIAGNOSI-BSOD.md` (contesto, non da modificare in questo progetto).

## Lavoro con l'utente

- Risposte in italiano.
- Verifiche visive (icone nella barra, tooltip, finestra): dire all'utente
  cosa guardare e aspettare la risposta.
- Se un valore reale della macchina non torna con i test "machine" del
  piano, non adattare il test: fermarsi e mostrare cosa si e' letto.
