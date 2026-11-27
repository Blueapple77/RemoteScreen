# RemoteScreen – ovládání Maců a PC po místní síti

Klient–server aplikace v C# / .NET:

- **RemoteScreen.app** (`Server/`): aplikace pro Mac ve složce Aplikace s ikonou v horním stavovém řádku. Menu ikony ukazuje:
  - stav serveru a IP adresy Macu,
  - kdo je připojený a jestli sleduje obrazovku,
  - stav oprávnění s odkazy do Nastavení.

  Z menu jde server restartovat nebo zastavit, změnit heslo, zobrazit log, zapnout nebo vypnout spouštění po přihlášení a aplikaci ukončit.

  Položka **Připojit k počítači…** otevře ovládání jiného počítače (Mac i Windows). Viz *Ovládání z Macu* níže.
- **RemoteScreen.exe** (`Client/`) je aplikace pro Windows v tmavém vzhledu s oranžovým akcentem:
  - **Přihlášení:** poloprůhledné okno s dlaždicemi nalezených a uložených počítačů. Spící počítač jde probudit (pravé tlačítko na dlaždici nebo dotaz při připojení).
  - **Obraz přes celé okno** s **menu vpravo**: Ovládání, Obraz, Zámek, Celá obrazovka a Odpojit (má-li druhý počítač víc obrazovek, přibude tlačítko **Monitor N** pro každou z nich – kliknutím na ni přepnete, po najetí myší se ukáže malý náhled). Menu se vysouvá a zasouvá kliknutím na malou šipku u pravého okraje; tažením šipky ho posunete nahoru nebo dolů (poloha se pamatuje). Vpravo nahoře je stav spojení (fps, odezva) – na celé obrazovce se schová spolu se zasunutým menu.
  - Ovládání (hlasitost, hudba, sdílení schránky, aplikace, systém) a Obraz (monitor, kvalita, fps, klávesy, barva zvýraznění) se otevírají jako poloprůhledný panel vlevo.
  - Soubory a složky **stačí přetáhnout přímo do obrazu**, a opačně je v obrazu chytit a vytáhnout ven z okna.

**Schránka se sdílí automaticky** (text, oběma směry): co zkopírujete na jednom počítači, vložíte na druhém – bez tlačítek. Vypíná se v panelu Ovládání → Schránka („Sdílet schránku automaticky“). Pozor: sdílí se i hesla zkopírovaná ze správce hesel.

**Přenos obrazu (od verze 3.4):** posílají se jen změněné části obrazovky (dlaždice 64×64 px), statický obraz se vůbec nekóduje ani neposílá. Při velkém pohybu (posouvání, video) se kvalita dočasně sníží kvůli plynulosti a po zklidnění se obraz doostří. Rozlišení se řídí velikostí okna – nepřenáší se víc pixelů, než je vidět. Starší klienti dostávají obraz po staru (celé snímky).

Spojení je šifrované přes TLS a chráněné heslem, které po síti nikdy neputuje. Porty: **TCP 47800** a **UDP 47801** (vyhledávání).

**Přechod z MacRemote:** RemoteScreen je nový název aplikace. Instalátory na obou stranách starý MacRemote samy ukončí a odstraní. Heslo, certifikát a uložené počítače se převezmou, takže se při prvním připojení nebudete muset znovu ověřovat. Oprávnění na Macu (Nahrávání obrazovky, Zpřístupnění) je potřeba povolit znovu pro **RemoteScreen**.

---

## Ovládání tohoto PC (Windows)

RemoteScreen pro Windows umí počítač i sdílet. Klikněte pravým tlačítkem na ikonu RemoteScreen u hodin a zapněte **Povolit ovládání tohoto PC**:
- Při prvním zapnutí nastavíte heslo, kterým se budou připojovat jiné počítače.
- Windows se může zeptat na povolení ve firewallu. Povolte RemoteScreen v **soukromé** síti.
- Dokud je sdílení zapnuté, RemoteScreen běží u hodin i po zavření okna. Ukončíte ho v menu ikony.
- **Spouštět s Windows** je ve výchozím stavu vypnuté. Zapnete ho v menu ikony (RemoteScreen pak startuje rovnou u hodin).

K PC se připojíte z jiného PC nebo z Macu s RemoteScreen. V seznamu nalezených počítačů je označené „Windows“.

**Tažení souborů z PC ven:** chyťte soubor nebo složku v Průzkumníku (nebo na ploše) a táhněte ji ven z okna RemoteScreen.

**Omezení:** okna spuštěná jako správce, dialogy UAC a zamykací obrazovku Windows nejde ovládat ani snímat.

## 1. Sestavení (na Windows)

Potřebujete .NET SDK 9.

```bat
build-client.bat        :: → publish\client\RemoteScreen.exe
build-server-mac.bat    :: → publish\RemoteScreen-Mac\  (RemoteScreen.app + install-mac.sh)
```

**Instalátor pro Windows:** `build-installer.bat` vytvoří `publish\RemoteScreen-Setup-3.6.4.exe`. Ten RemoteScreen nainstaluje, přidá ho do nabídky Start (a volitelně na plochu) a odinstalovat ho jde v *Nastavení → Aplikace*. Obsahuje i .NET, takže poběží na jakémkoli Windows 10/11. Při prvním sestavení skript nabídne instalaci nástroje [Inno Setup](https://jrsoftware.org/isinfo.php) (zdarma). Instalátor není podepsaný certifikátem, takže Windows při spuštění může ukázat „Systém Windows ochránil váš počítač“. Pak klikněte na **Další informace → Přesto spustit**.

Pro Mac s procesorem Intel změňte v `build-server-mac.bat` řádek `set RID=osx-arm64` na `osx-x64`.

## 2. Instalace na Macu

1. Zkopírujte celou složku `publish\RemoteScreen-Mac` na Mac (např. přes flashku nebo sdílenou složku).
2. Pokud běží starší verze serveru v Terminálu, ukončete ji (Ctrl+C).
3. V Terminálu přejděte do zkopírované složky a spusťte instalátor:
   ```bash
   cd ~/Downloads/RemoteScreen-Mac      # nebo kam jste složku zkopírovali
   bash install-mac.sh
   ```
   Instalátor zkopíruje aplikaci do **Aplikací**, podepíše ji, uklidí předchozí verzi a spustí ji.
4. Při prvním spuštění se aplikace zeptá na **heslo** a macOS na **oprávnění**:
   - **Nahrávání obrazovky** (obraz) a **Zpřístupnění** (myš a klávesnice): obojí povolte pro *RemoteScreen*.
   - Volitelně **Plný přístup k disku**: Soubory pak otevřou všechny složky bez dalších dotazů. Bez něj se macOS zeptá u každé chráněné složky (Plocha, Dokumenty, Stažené…).
5. Po povolení oprávnění zvolte v menu ikony **Restartovat server**.

Automatické spouštění po přihlášení je ve výchozím stavu **vypnuté**. Zapnete ho v menu ikony (**Spouštět po přihlášení**).

**Aktualizace:** stačí znovu spustit `bash install-mac.sh` s novou verzí. Oprávnění je pak nutné povolit znovu, protože nová verze má jiný podpis. Pokud je RemoteScreen v seznamu zapnutý, ale nefunguje, odeberte ho tlačítkem − a přidejte znovu.

**Odinstalace:** `bash install-mac.sh uninstall`

**Ladění:** `/Applications/RemoteScreen.app/Contents/MacOS/RemoteScreen --console --debug-keys` spustí server v Terminálu s výpisem kláves. Aplikaci z horní lišty předtím ukončete.

## 3. Použití klienta

1. Spusťte `RemoteScreen.exe`. Mac se zkusí najít sám, případně zadejte jeho IP adresu (je v menu ikony na Macu).
2. Zadejte heslo a připojte se.

### Plocha
- Klávesy se na Mac posílají po kliknutí do obrazu, včetně Win (= Cmd) a Alt+Tab.
- **Ctrl ↔ Cmd** (zapnuto, v panelu Obraz): Ctrl+C a Ctrl+V fungují jako na Windows.
- **Pravý Ctrl** je hostitelská klávesa a na Mac se neposílá:
  - pravý Ctrl + **F**: celá obrazovka,
  - pravý Ctrl + **K**: uvolnit klávesnici,
  - pravý Ctrl + **Del**: okno Vynutit ukončení.
- **Přetažení souborů z Windows do obrazu Macu:** záleží, nad čím je pustíte:
  - nad **oknem Finderu** se uloží do jeho složky,
  - nad **jinou aplikací** se nahrají (do *Stažené soubory/RemoteScreen*) a otevřou v ní,
  - jinde se uloží na **Plochu**.

  Existující soubory se nepřepisují, nový dostane název „soubor 2.txt“.
- **Tažení souborů z Macu do Windows:** chyťte soubor nebo složku ve Finderu v obrazu Macu a táhněte ji **ven z okna RemoteScreen**. Klient ji stáhne a pak ji pustíte do Průzkumníka, na plochu nebo do aplikace. Pokud tlačítko pustíte dřív, než se přenos dokončí, uloží se soubory do *Stažené soubory*. U velkých souborů tažení chvíli trvá.

## Ovládání z Macu (Mac → PC, Mac → Mac)

V menu ikony RemoteScreen v horní liště zvolte **Připojit k počítači…** (nebo Cmd+K, když je aplikace vpředu).
1. Okno samo vyhledá počítače v síti. Jinak klikněte na **Přidat pomocí IP**.
2. Vyberte počítač, zadejte heslo a klikněte na **Připojit**. Dvojklik na počítač připojí rovnou.
3. Pokud uložený počítač neodpovídá, RemoteScreen nabídne jeho **probuzení** (Wake-on-LAN) a připojí se, až naběhne.

Okna na Macu vypadají stejně jako na Windows (návrh C): přihlášení s dlaždicemi počítačů, obraz přes celé okno, vysouvací menu vpravo a stav vpravo nahoře.
- Myš, kolečko i trackpad fungují přímo. Klávesy se posílají, když je okno vpředu, včetně zkratek s Cmd (Cmd+Q, Cmd+W… jdou na druhý počítač). Cmd+Tab a Cmd+mezerník si nechává macOS. Posílání kláves vypnete v panelu **Obraz**.
- **Ovládání Windows:** Cmd funguje jako Ctrl (Cmd+C, Cmd+V, Cmd+Z…) a Ctrl jako klávesa Windows. Přepíná se v panelu **Obraz**.
- **Menu vpravo** (vysouvá se kliknutím na šipku u pravého okraje, tažením šipky ho posunete): Ovládání, Obraz, (Monitor 1, 2… – jen při více obrazovkách, s náhledem po najetí myší), Zámek, Celá obrazovka, Odpojit. Ovládání (zvuk, hudba, sdílení schránky, aplikace, systém) a Obraz (monitor, kvalita, fps, klávesy, barva zvýraznění) se otevírají jako poloprůhledný panel vlevo.
- **Soubory z Macu na druhý počítač:** přetáhněte je z Finderu do okna. Uloží se podle místa puštění: do okna Finderu nebo Průzkumníka, do aplikace (otevřou se v ní), jinak na Plochu.
- **Soubory z druhého počítače na Mac:** chyťte soubor ve Finderu nebo v Průzkumníku v obrazu a táhněte ho ven z okna. Když tlačítko pustíte dřív, než se soubor přenese, uloží se do *Stažených souborů*.

### Probuzení spícího Macu

Když se k uloženému počítači nedá připojit, RemoteScreen nabídne jeho probuzení (jde to i ručně: pravé tlačítko na dlaždici → **Probudit**). Pošle do sítě probouzecí paket (Wake-on-LAN), počká, až Mac naběhne, a rovnou se připojí. MAC adresu Macu si klient pamatuje z předchozího připojení. Ručně ji najdete v menu ikony RemoteScreen u adresy.

Na Macu musí být zapnuto *Nastavení systému → Energie → **Probudit při přístupu k síti***. Spolehlivě to funguje, když je Mac připojený **síťovým kabelem**; přes Wi-Fi jen někdy. Vypnutý Mac takto zapnout nejde, jen uspaný.

Dokud je někdo připojený, Mac sám neusne. Po připojení se mu rozsvítí displej.

## 4. Řešení problémů

| Problém | Řešení |
|---|---|
| Klient se nepřipojí | Je v horní liště Macu ikona RemoteScreen a v menu „RemoteScreen běží“? Jste ve stejné síti? |
| Ikona v liště je trojúhelník ⚠️ | Chybí oprávnění nebo server neběží – otevřete menu ikony, je tam vysvětlení i odkazy. |
| Složku v Souborech nelze otevřít | macOS nepovolil přístup: potvrďte dotaz na Macu nebo povolte **Plný přístup k disku** (odkaz v menu ikony). |
| Obraz se neukazuje | Oprávnění Nahrávání obrazovky → povolit → v menu Restartovat server. |
| Nefunguje myš/klávesnice | Oprávnění Zpřístupnění → povolit → Restartovat server. |
| Soubory puštěné do okna Finderu skončí na Ploše | Povolte v *Nastavení → Soukromí a zabezpečení → Automatizace* u RemoteScreen přepínač **Finder** (dotaz se objeví po spuštění aplikace). Okna jako Nedávné nebo AirDrop nemají složku – použijte okno s konkrétní složkou. |
| Tažení ven z okna nefunguje | Táhněte soubor ve Finderu / Průzkumníku a vyjeďte myší úplně ven z obrazu; tažení oken nebo textu se nepřenáší. |
| Probudit nefunguje | Zapněte na Macu Energie → Probudit při přístupu k síti, připojte Mac kabelem a jednou se k němu připojte (klient si uloží MAC adresu). |
| Po aktualizaci nefungují oprávnění | V Nastavení odeberte RemoteScreen (−) a přidejte znovu (+). |
| Aplikace na Macu nejde spustit („poškozená“) | Spusťte instalátor znovu (podepíše ji), nebo: `xattr -cr /Applications/RemoteScreen.app && codesign --force --deep -s - /Applications/RemoteScreen.app` |

Log serveru: `~/Library/Logs/RemoteScreen.log` (menu ikony → Zobrazit log).
