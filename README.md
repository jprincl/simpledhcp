# SimpleDhcpServer

Jednoduchý DHCP server pro Windows s grafickým rozhraním (C# / .NET 8 / WinForms).
Umí DISCOVER → OFFER → REQUEST → ACK/NAK, RELEASE, přidělování podle MAC adresy,
konfigurovatelný rozsah adres, masku, bránu, DNS servery a dobu zápůjčky, a přehled
aktivních leasů v tabulce.

Hodí se typicky pro scénář „notebook připojený kabelem přímo na switch, který
potřebuje IP adresu, aby šel nakonfigurovat“: v horní části okna vyberete
síťovou kartu (i s aktuální IP/maskou, klidně i APIPA 169.254.x.x, pokud na ní
zatím žádný DHCP nejel) a program podle ní předvyplní vše potřebné. Odpovědi se
posílají na broadcast dané podsítě, ne na adresu 255.255.255.255 — díky tomu
paket odejde správným kabelem, i když má notebook zároveň aktivní Wi-Fi.

## ⚠️ Než spustíte v reálné síti

- Server odpovídá broadcastem na celém segmentu. Pokud v síti **už běží jiný DHCP
  server** (router, SonicWall, Windows DHCP role...), spuštěním tohoto programu
  vytvoříte **rogue DHCP server** a klienti mohou dostávat protichůdné konfigurace.
  Vyzkoušejte nejdřív v izolovaném testovacím segmentu / VLAN, ne na produkční síti.
- Neřeší DHCP relay (giaddr) — je určený pro jeden lokální segment, ne pro
  směrovanou síť s více podsítěmi.
- Naslouchání na UDP/67 může vyžadovat spuštění „Jako správce“ a povolení ve
  Windows Firewallu.

## Struktura projektu

```
SimpleDhcpServer/
  SimpleDhcpServer.csproj
  Program.cs        – vstupní bod
  MainForm.cs        – GUI (konfigurace, tabulka leasů, log)
  DhcpServer.cs      – UDP listener a logika DISCOVER/REQUEST/RELEASE
  DhcpPacket.cs      – parsování/skládání DHCP paketů
  LeaseManager.cs    – pool adres a evidence zápůjček
.github/workflows/build.yml – GitHub Actions build
```

## Sestavení přes GitHub Actions

1. Nahrajte celý obsah tohoto adresáře do vlastního GitHub repozitáře (kořen
   repozitáře = tento adresář, tzn. `.github/` i `SimpleDhcpServer/` na stejné
   úrovni).
2. Po `git push` na větev `main` se automaticky spustí workflow **Build DHCP
   Server EXE** (záložka **Actions**), který na běžci `windows-latest` udělá
   `dotnet publish` a nahraje hotový `SimpleDhcpServer.exe` jako **artifact**
   (ke stažení v detailu daného běhu).
3. Pokud chcete rovnou i GitHub **Release** s přiloženým .exe, stačí vytvořit
   a pushnout tag ve formátu `v*`, např.:
   ```
   git tag v1.0.0
   git push origin v1.0.0
   ```
4. Workflow lze spustit i ručně tlačítkem **Run workflow** (díky
   `workflow_dispatch`).

GitHub Actions poskytuje běžce `windows-latest` zdarma (u veřejných repozitářů
neomezeně, u soukromých v rámci měsíčního limitu minut).

## Lokální build (pokud máte Windows + .NET SDK)

```
dotnet publish SimpleDhcpServer/SimpleDhcpServer.csproj -c Release -o publish
```

Projekt cílí na **.NET Framework 4.8**, který je součástí Windows 10 (od verze
1903) i Windows 11. Výsledný `publish/SimpleDhcpServer.exe` má jen desítky kB
a na cílovém stroji nevyžaduje instalaci žádného runtime.

## Poznámka k testování

Kód jsem nemohl zkompilovat přímo zde (sandbox nemá Windows/WinForms build
prostředí), takže první build přes Actions může případně odhalit drobnou
chybu — v tom případě mi stačí poslat chybové hlášení z logu Actions a opravím to.
