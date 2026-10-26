# Workshop hlídač – Bolek98
# Jednou za čas (výchozí 60 minut) se zeptá Steamu na tvoje mody na Workshopu a když
# někdo přidá/zruší odběr nebo oblíbené, ukáže upozornění na PC a pošle ho na mobil (ntfy).
# Steam neříká KDO to byl – jen čísla. Ve Steamu se čísla navíc obnovují se zpožděním.

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
[void]$menu.Items.Add('Zkontrolovat teď', $null, { $script:checkNow = $true })
[void]$menu.Items.Add('Otevřít log', $null, { Start-Process notepad.exe $logFile })
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
    foreach ($it in $items) {
        $id = "$($it.publishedfileid)"
        $cur = [ordered]@{
            title = $it.title; subs = [int]$it.subscriptions; life = [int]$it.lifetime_subscriptions
            fav = [int]$it.favorited; lifeFav = [int]$it.lifetime_favorited; comments = 0
        }
        $name = ($it.title -split ' – ')[0]
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
                $sum += (($c.title -split ' – ')[0] + ": +$n odběrů, -$([Math]::Max(0,$l)) zrušeno, ★ $(Sign $f), komentáře +$([Math]::Max(0,$k))")
            }
        }
        $head = "Dnes celkem: +$tn odběrů, -$tl zrušeno, ★ $(Sign $tf), komentáře +$tc"
        $body = if ($sum.Count -gt 0) { $head + "`n" + ($sum -join "`n") } else { 'Dnes beze změn.' }
        Notify 'Denní souhrn' $body $null
        $day.sent = $true
    }

    [ordered]@{ items = $new; comments = $lastComment; day = $day } | ConvertTo-Json -Depth 6 | Set-Content -Path $stateFile -Encoding UTF8
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
while (-not $script:quit) {
    if ($script:checkNow -or (Get-Date) -ge $next) {
        $script:checkNow = $false
        try { Check } catch { Log ('chyba: ' + $_.Exception.Message) }
        $next = (Get-Date).AddMinutes([Math]::Max(5, [int]$cfg.intervalMinut))
    }
    [System.Windows.Forms.Application]::DoEvents()
    Start-Sleep -Milliseconds 300
}
$tray.Visible = $false
$tray.Dispose()
Log 'konec'
