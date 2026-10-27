# Workshop hlídač – Bolek98
# Jednou za čas (výchozí 60 minut) se zeptá Steamu na tvoje mody na Workshopu a když
# někdo přidá/zruší odběr nebo oblíbené, ukáže upozornění na PC a pošle ho na mobil (ntfy).
# Steam neříká KDO to byl – jen čísla. Ve Steamu se čísla navíc obnovují se zpožděním.
# Navíc: když zavřeš RimWorld, projde Player.log a když v něm jsou chyby z TVÝCH modů,
# dá vědět (kolik a jaká poslední) a uloží je do složky 'chyby' vedle skriptu.

$ErrorActionPreference = 'Stop'

# jen jedna kopie: starší běžící hlídač se ukončí (po aktualizaci skriptu)
try {
    Get-CimInstance Win32_Process -Filter "Name='powershell.exe' OR Name='pwsh.exe'" |
        Where-Object { $_.ProcessId -ne $PID -and $_.CommandLine -like '*WorkshopHlidac.ps1*' } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
} catch { }
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$cfgFile = Join-Path $here 'nastaveni.json'
$stateFile = Join-Path $here 'stav.json'
$logFile = Join-Path $here 'hlidac.log'
$histFile = Join-Path $here 'historie.csv'
$statsFile = Join-Path $here 'statistiky.html'
$statsTemplate = Join-Path $here 'statistiky_sablona.html'

function Log($text) {
    $line = (Get-Date -Format 'yyyy-MM-dd HH:mm') + '  ' + $text
    Add-Content -Path $logFile -Value $line -Encoding UTF8
}

# ---------- nastavení ----------
if (-not (Test-Path $cfgFile)) {
    $topic = 'bolek98-mody-' + ([guid]::NewGuid().ToString('N').Substring(0, 10))
    $default = [ordered]@{
        intervalMinut = 60
        slozkaModu    = (Resolve-Path (Join-Path $here '..\Mods')).Path
        autor         = 'Bolek98'
        dalsiId       = @()
        pcUpozorneni  = $true
        ntfyTema      = $topic
        ntfyServer    = 'https://ntfy.sh'
        souhrnHodina  = 21
    }
    $default | ConvertTo-Json | Set-Content -Path $cfgFile -Encoding UTF8
}
$cfg = Get-Content $cfgFile -Raw -Encoding UTF8 | ConvertFrom-Json
if ($null -eq $cfg.PSObject.Properties['souhrnHodina']) { $cfg | Add-Member souhrnHodina 21 }
if ($null -eq $cfg.PSObject.Properties['hlidatChyby']) { $cfg | Add-Member hlidatChyby $true }

# ---------- které mody hlídat ----------
function Get-ModIds {
    $ids = @{}
    if (Test-Path $cfg.slozkaModu) {
        Get-ChildItem -Path $cfg.slozkaModu -Directory | ForEach-Object {
            $pf = Join-Path $_.FullName 'About\PublishedFileId.txt'
            $ab = Join-Path $_.FullName 'About\About.xml'
            if ((Test-Path $pf) -and (Test-Path $ab)) {
                $about = Get-Content $ab -Raw -Encoding UTF8
                if ($about -match ('<author>\s*' + [regex]::Escape($cfg.autor) + '\s*</author>')) {
                    $id = (Get-Content $pf -Raw).Trim()
                    if ($id -match '^\d+$') { $ids[$id] = $true }
                }
            }
        }
    }
    foreach ($x in $cfg.dalsiId) { if ("$x" -match '^\d+$') { $ids["$x"] = $true } }
    return @($ids.Keys)
}

# ---------- Steam ----------
function Get-Details($ids) {
    $body = @{ itemcount = $ids.Count }
    for ($i = 0; $i -lt $ids.Count; $i++) { $body["publishedfileids[$i]"] = $ids[$i] }
    $r = Invoke-RestMethod -Method Post -Uri 'https://api.steampowered.com/ISteamRemoteStorage/GetPublishedFileDetails/v1/' -Body $body -TimeoutSec 30
    return @($r.response.publishedfiledetails | Where-Object { $_.result -eq 1 })
}

# ---------- upozornění ----------
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$tray = New-Object System.Windows.Forms.NotifyIcon
$tray.Icon = [System.Drawing.SystemIcons]::Information
$tray.Text = 'Workshop hlídač (Bolek98)'
$tray.Visible = $true
$menu = New-Object System.Windows.Forms.ContextMenuStrip
$script:quit = $false
$script:checkNow = $false
$script:checkGame = $false
[void]$menu.Items.Add('Zkontrolovat teď', $null, { $script:checkNow = $true })
[void]$menu.Items.Add('Otevřít statistiky', $null, { if (Test-Path $statsFile) { Start-Process $statsFile } })
[void]$menu.Items.Add('Otevřít log', $null, { Start-Process notepad.exe $logFile })
[void]$menu.Items.Add('Zkontrolovat chyby ze hry', $null, { $script:checkGame = $true })
[void]$menu.Items.Add('Ukončit', $null, { $script:quit = $true })
$tray.ContextMenuStrip = $menu

function Notify($title, $text, $click) {
    Log ($title + ': ' + ($text -replace "`n", ' | '))
    if ($cfg.pcUpozorneni) {
        $tray.BalloonTipTitle = $title
        $tray.BalloonTipText = $text
        $tray.ShowBalloonTip(10000)
    }
    Send-Phone ($title + "`n" + $text) $click
}

function Sign($n) { if ($n -gt 0) { return '+' + $n } else { return "$n" } }

# ---------- komentáře ----------
function Get-NewComments($creator, $id, $lastId) {
    $u = "https://steamcommunity.com/comment/PublishedFile_Public/render/$creator/$id/?start=0&count=10"
    $r = Invoke-RestMethod -Uri $u -TimeoutSec 30
    $list = @()
    $rx = 'id="comment_(\d+)".*?commentthread_author_link"[^>]*>\s*<bdi>(.*?)</bdi>.*?commentthread_comment_text"[^>]*>(.*?)</div>'
    foreach ($m in [regex]::Matches([string]$r.comments_html, $rx, 'Singleline')) {
        $cid = [decimal]$m.Groups[1].Value
        if ($cid -le $lastId) { continue }
        $author = [System.Net.WebUtility]::HtmlDecode(($m.Groups[2].Value -replace '<[^>]+>', '')).Trim()
        $text = [System.Net.WebUtility]::HtmlDecode(($m.Groups[3].Value -replace '<br\s*/?>', ' ' -replace '<[^>]+>', '')).Trim()
        if ($text.Length -gt 160) { $text = $text.Substring(0, 157) + '…' }
        $list += [pscustomobject]@{ id = $cid; author = $author; text = $text }
    }
    return [pscustomobject]@{ total = [int]$r.total_count; items = $list }
}

function Send-Phone($text, $click) {
    if (-not $cfg.ntfyTema) { return }
    try {
        $h = @{ Title = 'RimWorld Workshop'; Tags = 'video_game' }
        if ($click) { $h['Click'] = $click }
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($text)
        Invoke-RestMethod -Method Post -Uri ($cfg.ntfyServer.TrimEnd('/') + '/' + $cfg.ntfyTema) -Body $bytes `
            -ContentType 'text/plain; charset=utf-8' -Headers $h -TimeoutSec 20 | Out-Null
    } catch { Log ('ntfy: ' + $_.Exception.Message) }
}

function Get-Prop($obj, $name) {
    if ($null -eq $obj) { return $null }
    $p = $obj.PSObject.Properties[$name]
    if ($p) { return $p.Value } else { return $null }
}

function ToTable($obj) {
    $t = @{}
    if ($obj) { $obj.PSObject.Properties | ForEach-Object { $t[$_.Name] = $_.Value } }
    return $t
}

# ---------- chyby z mých modů po zavření hry ----------
# jmenný prostor v kódu -> název modu (chyba se počítá jen když je mod přímo v zásobníku volání,
# ne když jen "visí" jako Harmony patch na stejné metodě – to by svádělo vinu na nevinné)
$myNamespaces = [ordered]@{
    'AutoPharmacy' = 'Auto Pharmacy'; 'AutoTurrets' = 'Auto Turrets'; 'BodyFit' = 'Body Fit'
    'CallForBackup' = 'Call for Backup'; 'CaravanHelper' = 'Caravan Helper'; 'HardyPlants' = 'Evergrowth'
    'EvergrowthWild' = 'Evergrowth – Wild Plants'; 'MemoryTuner' = 'Fewer Freezes'; 'GenderBalance' = 'Gender Balance'
    'HaulSmarter' = 'Haul Master'; 'Legacy' = 'Legacy'; 'MapArchitect' = 'Map Architect'
    'MasksAndHeadgear' = 'Masks and Headgear'; 'Matchmaker' = 'Matchmaker'; 'MechCommander' = 'Mech Commander'
    'MisakBodies' = 'Mišák Bodies'; 'MisakTabs' = 'Mišák Tabs'; 'ModSortManager' = 'Mod Sort Manager'
    'PlaceForEverything' = 'Place for Everything'; 'RandysTable' = "Randy's Table"; 'ResearchFixer' = 'Research Fixer'
    'SecularColony' = 'Secular Colony'; 'ResearchedLoadout' = 'Smart Colonist Gear'; 'GraphicsTuner' = 'Smooth FPS'
    'ColonyForeman' = 'Steward'; 'StorytellerTuner' = 'Storyteller Tuner'; 'UniversalRimTranslator' = 'URT'; 'Urt2' = 'URT'
    'UnofficialPatch' = 'Unofficial Patch'; 'WildSpawnZone' = 'Wild Spawn Zone'; 'ResearchLockedRaidGear' = 'World at Your Level'
    'ModFixes' = 'Mod Fixes'; 'IntimateLife' = 'Intimate Life'; 'RychlaBolest' = 'Rychlá bolest'; 'RychlaBolezst' = 'Rychlá bolest'
}
$nsAlt = ($myNamespaces.Keys | ForEach-Object { [regex]::Escape($_) }) -join '|'
$rxFrame = New-Object regex -ArgumentList ('^\s*at\s+(' + $nsAlt + ')\.'), ([System.Text.RegularExpressions.RegexOptions]::Compiled)
$rxHead = New-Object regex -ArgumentList '^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d\.\d+\s+(\w+)\s+(?:\[[^\]]*\]\s*)?(.*)$', ([System.Text.RegularExpressions.RegexOptions]::Compiled)
$tagNames = @($myNamespaces.Values | Select-Object -Unique)

function Find-PlayerLog {
    $base = Join-Path $env:USERPROFILE 'AppData\LocalLow\Ludeon Studios\RimWorld by Ludeon Studios'
    $c = @((Join-Path $base 'Logs\Player.log'), (Join-Path $base 'Player.log')) | Where-Object { Test-Path $_ }
    if ($c.Count -eq 0) { return $null }
    return ($c | ForEach-Object { Get-Item $_ } | Sort-Object LastWriteTime -Descending | Select-Object -First 1)
}

function Read-Shared($path) {
    $fs = [System.IO.File]::Open($path, 'Open', 'Read', 'ReadWrite')
    try { $sr = New-Object System.IO.StreamReader($fs, [System.Text.Encoding]::UTF8); return $sr.ReadToEnd() }
    finally { $fs.Dispose() }
}

function Clean($t) { return (($t -replace '<[^>]+>', '') -replace '\s+', ' ').Trim() }

function Check-GameLog {
    $f = Find-PlayerLog
    if (-not $f) { Log 'Player.log nenalezen'; return }
    $lines = (Read-Shared $f.FullName) -split "`r?`n"
    # záznamy: hlavička s časem a úrovní + odsazené řádky pod ní (zásobník)
    $entries = New-Object System.Collections.ArrayList
    $cur = $null
    foreach ($l in $lines) {
        $m = $rxHead.Match($l)
        if ($m.Success) {
            $cur = @{ level = $m.Groups[1].Value; text = $m.Groups[2].Value; body = New-Object System.Collections.ArrayList }
            [void]$entries.Add($cur)
        } elseif ($cur) { [void]$cur.body.Add($l) }
    }
    $byMod = [ordered]@{}
    $report = New-Object System.Collections.ArrayList
    foreach ($e in $entries) {
        if ($e.level -notmatch '^(ERROR|EXCEPTION|FATAL|CRITICAL)$') { continue }
        $mod = $null
        foreach ($b in $e.body) {
            if ($b -match '^\s+- (PREFIX|POSTFIX|TRANSPILER|FINALIZER) ') { continue }
            $fm = $rxFrame.Match($b)
            if ($fm.Success) { $mod = $myNamespaces[$fm.Groups[1].Value]; break }
        }
        if (-not $mod) {
            $clean = Clean $e.text
            foreach ($n in $tagNames) { if ($clean.StartsWith('[' + $n + ']')) { $mod = $n; break } }
        }
        if (-not $mod) { continue }
        if (-not $byMod.Contains($mod)) { $byMod[$mod] = @{ n = 0; unique = @{}; last = '' } }
        $msg = Clean $e.text
        $key = ($msg -replace '\d+', '#')
        if ($key.Length -gt 140) { $key = $key.Substring(0, 140) }
        $byMod[$mod].n++
        $byMod[$mod].unique[$key] = $true
        $byMod[$mod].last = $msg
        [void]$report.Add('=== ' + $mod + ' ===')
        [void]$report.Add($e.text)
        foreach ($b in $e.body) { [void]$report.Add($b) }
        [void]$report.Add('')
    }
    if ($byMod.Count -eq 0) {
        Log ('hra zavřená – v Player.log žádné chyby z tvých modů (' + $entries.Count + ' záznamů)')
        if ($cfg.pcUpozorneni) {
            $tray.BalloonTipTitle = 'RimWorld zavřený'
            $tray.BalloonTipText = 'Žádné chyby z tvých modů 👍'
            $tray.ShowBalloonTip(5000)
        }
        return
    }
    $dir = Join-Path $here 'chyby'
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir | Out-Null }
    $out = Join-Path $dir ('chyby_' + (Get-Date -Format 'yyyy-MM-dd_HH-mm') + '.txt')
    $report | Set-Content -Path $out -Encoding UTF8
    $parts = @()
    foreach ($k in $byMod.Keys) {
        $v = $byMod[$k]
        $last = $v.last
        if ($last.Length -gt 140) { $last = $last.Substring(0, 137) + '…' }
        $word = if ($v.n -eq 1) { 'chyba' } elseif ($v.n -lt 5) { 'chyby' } else { 'chyb' }
        $parts += ($k + ': ' + $v.n + ' ' + $word + ', různých ' + $v.unique.Count + ', poslední: ' + $last)
    }
    Notify 'Chyby z tvých modů' (($parts -join "`n") + "`nCelé: chyby\" + (Split-Path -Leaf $out)) $null
}

# ---------- historie a stránka se statistikami ----------
function Add-History($new, $items) {
    $stamp = (Get-Date).ToString('yyyy-MM-ddTHH:mm:sszzz')
    if (-not (Test-Path $histFile)) { Set-Content -Path $histFile -Value 'cas;id;odbery;odberyCelkem;oblibene;oblibeneCelkem;navstevy;komentare' -Encoding UTF8 }
    $lines = @()
    foreach ($it in $items) {
        $id = "$($it.publishedfileid)"
        if (-not $new.Contains($id)) { continue }
        $c = $new[$id]
        $lines += ($stamp + ';' + $id + ';' + $c.subs + ';' + $c.life + ';' + $c.fav + ';' + $c.lifeFav + ';' + [int]$it.views + ';' + $c.comments)
    }
    if ($lines.Count -gt 0) { Add-Content -Path $histFile -Value $lines -Encoding UTF8 }
}

function Write-Stats($titles) {
    if (-not (Test-Path $statsTemplate) -or -not (Test-Path $histFile)) { return }
    $rows = New-Object System.Collections.ArrayList
    foreach ($l in (Get-Content $histFile -Encoding UTF8 | Select-Object -Skip 1)) {
        $f = $l -split ';'
        if ($f.Count -lt 8) { continue }
        $vals = @($f[2..7] | ForEach-Object { if ($_ -eq '') { 'null' } else { [string][int]$_ } })
        [void]$rows.Add('["' + $f[0] + '","' + $f[1] + '",' + ($vals -join ',') + ']')
    }
    $mods = @()
    foreach ($k in $titles.Keys) { $mods += ('"' + $k + '":' + (ConvertTo-Json ([string]$titles[$k]) -Compress)) }
    $json = '{"mods":{' + ($mods -join ',') + '},"rows":[' + ($rows -join ',') + ']}'
    $html = Get-Content $statsTemplate -Raw -Encoding UTF8
    $a = $html.IndexOf('/*DATA*/')
    $b = $html.IndexOf('/*END*/')
    if ($a -lt 0 -or $b -lt $a) { return }
    $html = $html.Substring(0, $a + 8) + $json + $html.Substring($b)
    [System.IO.File]::WriteAllText($statsFile, $html, (New-Object System.Text.UTF8Encoding($false)))
}

# krátký název modu (část před pomlčkou), jen když se tím dva mody neslijí do jednoho jména
function Short-Names($items) {
    $short = @{}
    $count = @{}
    foreach ($it in $items) { $n = ($it.title -split ' – ')[0]; $count[$n] = 1 + [int]$count[$n] }
    foreach ($it in $items) {
        $n = ($it.title -split ' – ')[0]
        $short["$($it.publishedfileid)"] = if ($count[$n] -gt 1) { $it.title } else { $n }
    }
    return $short
}

# ---------- jedna kontrola ----------
function Check {
    $ids = Get-ModIds
    if ($ids.Count -eq 0) { Log ('Žádné mody s PublishedFileId.txt a autorem ' + $cfg.autor); return }
    $items = Get-Details $ids
    $st = $null
    if (Test-Path $stateFile) { $st = Get-Content $stateFile -Raw -Encoding UTF8 | ConvertFrom-Json }
    # starší formát stav.json = jen mody
    $oldItems = if (Get-Prop $st 'items') { ToTable $st.items } elseif ($st) { ToTable $st } else { @{} }
    $lastComment = ToTable (Get-Prop $st 'comments')
    $day = Get-Prop $st 'day'
    $today = (Get-Date).ToString('yyyy-MM-dd')

    $new = [ordered]@{}
    $lines = @()
    $short = Short-Names $items
    foreach ($it in $items) {
        $id = "$($it.publishedfileid)"
        $cur = [ordered]@{
            title = $it.title; subs = [int]$it.subscriptions; life = [int]$it.lifetime_subscriptions
            fav = [int]$it.favorited; lifeFav = [int]$it.lifetime_favorited; comments = 0
        }
        $name = $short[$id]
        # komentáře
        try {
            $last = if ($lastComment.ContainsKey($id)) { [decimal]$lastComment[$id] } else { [decimal]-1 }
            $c = Get-NewComments $it.creator $id $last
            $cur.comments = $c.total
            if ($c.items.Count -gt 0) {
                $lastComment[$id] = ($c.items | Measure-Object -Property id -Maximum).Maximum
                if ($last -ge 0) {
                    foreach ($cm in ($c.items | Sort-Object id)) {
                        if ($cm.author -eq $cfg.autor) { continue }
                        $msg = $name + ' – ' + $cm.author + ': ' + $cm.text
                        Notify 'Nový komentář' $msg ('https://steamcommunity.com/sharedfiles/filedetails/?id=' + $id)
                    }
                }
            } elseif (-not $lastComment.ContainsKey($id)) { $lastComment[$id] = 0 }
        } catch { Log ('komentáře ' + $name + ': ' + $_.Exception.Message) }

        $new[$id] = $cur
        if (-not $oldItems.ContainsKey($id)) { continue }
        $o = $oldItems[$id]
        $newSubs = $cur.life - [int]$o.life
        $lost = $newSubs - ($cur.subs - [int]$o.subs)
        $newFav = $cur.lifeFav - [int]$o.lifeFav
        $lostFav = $newFav - ($cur.fav - [int]$o.fav)
        $parts = @()
        if ($newSubs -gt 0) { $parts += "nové odběry +$newSubs" }
        if ($lost -gt 0) { $parts += "zrušené odběry -$lost" }
        if ($newFav -gt 0) { $parts += "oblíbené +$newFav" }
        if ($lostFav -gt 0) { $parts += "odebráno z oblíbených -$lostFav" }
        if ($parts.Count -gt 0) {
            $lines += ($name + ': ' + ($parts -join ', ') + "  (teď $($cur.subs) odběrů, $($cur.fav) ★)")
        }
    }

    # denní souhrn: základ = první kontrola dne, souhrn po nastavené hodině
    if (-not $day -or $day.date -ne $today) {
        $day = [pscustomobject]@{ date = $today; sent = $false; base = $new }
    }
    $hour = [int]$cfg.souhrnHodina
    if ($hour -ge 0 -and -not $day.sent -and (Get-Date).Hour -ge $hour) {
        $base = ToTable $day.base
        $sum = @(); $tn = 0; $tl = 0; $tf = 0; $tc = 0
        foreach ($id in $new.Keys) {
            if (-not $base.ContainsKey($id)) { continue }
            $b = $base[$id]; $c = $new[$id]
            $n = $c.life - [int]$b.life
            $l = $n - ($c.subs - [int]$b.subs)
            $f = $c.fav - [int]$b.fav
            $k = $c.comments - [int]$b.comments
            $tn += $n; $tl += [Math]::Max(0, $l); $tf += $f; $tc += [Math]::Max(0, $k)
            if ($n -ne 0 -or $l -gt 0 -or $f -ne 0 -or $k -gt 0) {
                $sum += ($short[$id] + ": +$n odběrů, -$([Math]::Max(0,$l)) zrušeno, ★ $(Sign $f), komentáře +$([Math]::Max(0,$k))")
            }
        }
        $head = "Dnes celkem: +$tn odběrů, -$tl zrušeno, ★ $(Sign $tf), komentáře +$tc"
        $body = if ($sum.Count -gt 0) { $head + "`n" + ($sum -join "`n") } else { 'Dnes beze změn.' }
        Notify 'Denní souhrn' $body $null
        $day.sent = $true
    }

    $full = @{}
    foreach ($it in $items) { $full["$($it.publishedfileid)"] = [string]$it.title }
    try { Add-History $new $items; Write-Stats $full } catch { Log ('statistiky: ' + $_.Exception.Message) }
    [ordered]@{ items = $new; comments = $lastComment; day = $day; logSeen = (Get-Prop $st 'logSeen') } | ConvertTo-Json -Depth 6 | Set-Content -Path $stateFile -Encoding UTF8
    if ($oldItems.Count -eq 0) {
        Notify 'Workshop hlídač běží' ("Hlídané mody: $($items.Count), kontrola každých $($cfg.intervalMinut) min.") $null
    } elseif ($lines.Count -gt 0) {
        Notify 'Workshop – změny' ($lines -join "`n") $null
    } else {
        Log 'beze změny'
    }
}

# ---------- hlavní smyčka ----------
Log 'start'
$next = Get-Date
$nextGame = Get-Date
$gameWasRunning = $false
# když se hlídač spustí až po zavření hry (třeba po restartu PC), zkontroluje log, který ještě neviděl
$seenLog = $null
if (Test-Path $stateFile) { try { $seenLog = (Get-Content $stateFile -Raw -Encoding UTF8 | ConvertFrom-Json).logSeen } catch { } }
while (-not $script:quit) {
    if ($cfg.hlidatChyby -and (Get-Date) -ge $nextGame) {
        $nextGame = (Get-Date).AddSeconds(15)
        try {
            $running = [bool](Get-Process -Name 'RimWorldWin64', 'RimWorld' -ErrorAction SilentlyContinue)
            $pl = if (-not $running) { Find-PlayerLog } else { $null }
            $stamp = if ($pl) { $pl.LastWriteTimeUtc.Ticks.ToString() } else { $null }
            if (-not $running -and $stamp -and ($gameWasRunning -or ($seenLog -and $stamp -ne "$seenLog"))) {
                Start-Sleep -Seconds 3
                Check-GameLog
            }
            if (-not $running -and $stamp -and "$seenLog" -ne $stamp) {
                $seenLog = $stamp
                $st = if (Test-Path $stateFile) { Get-Content $stateFile -Raw -Encoding UTF8 | ConvertFrom-Json } else { [pscustomobject]@{} }
                if ($st.PSObject.Properties['logSeen']) { $st.logSeen = $stamp } else { $st | Add-Member logSeen $stamp }
                $st | ConvertTo-Json -Depth 6 | Set-Content -Path $stateFile -Encoding UTF8
            }
            $gameWasRunning = $running
        } catch { Log ('chyby ze hry: ' + $_.Exception.Message) }
    }
    if ($script:checkNow -or (Get-Date) -ge $next) {
        $script:checkNow = $false
        try { Check } catch { Log ('chyba: ' + $_.Exception.Message) }
        $next = (Get-Date).AddMinutes([Math]::Max(5, [int]$cfg.intervalMinut))
    }
    if ($script:checkGame) { $script:checkGame = $false; try { Check-GameLog } catch { Log ('chyby ze hry: ' + $_.Exception.Message) } }
    [System.Windows.Forms.Application]::DoEvents()
    Start-Sleep -Milliseconds 300
}
$tray.Visible = $false
$tray.Dispose()
Log 'konec'
