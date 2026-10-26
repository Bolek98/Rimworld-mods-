# Workshop hlídač – Bolek98
# Jednou za čas (výchozí 60 minut) se zeptá Steamu na tvoje mody na Workshopu a když
# někdo přidá/zruší odběr nebo oblíbené, ukáže upozornění na PC a pošle ho na mobil (ntfy).
# Steam neříká KDO to byl – jen čísla. Ve Steamu se čísla navíc obnovují se zpožděním.

$ErrorActionPreference = 'Stop'
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
    }
    $default | ConvertTo-Json | Set-Content -Path $cfgFile -Encoding UTF8
}
$cfg = Get-Content $cfgFile -Raw -Encoding UTF8 | ConvertFrom-Json

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

function Notify($title, $text) {
    Log ($title + ': ' + ($text -replace "`n", ' | '))
    if ($cfg.pcUpozorneni) {
        $tray.BalloonTipTitle = $title
        $tray.BalloonTipText = $text
        $tray.ShowBalloonTip(10000)
    }
    if ($cfg.ntfyTema) {
        try {
            $bytes = [System.Text.Encoding]::UTF8.GetBytes($text)
            Invoke-RestMethod -Method Post -Uri ($cfg.ntfyServer.TrimEnd('/') + '/' + $cfg.ntfyTema) -Body $bytes `
                -ContentType 'text/plain; charset=utf-8' -Headers @{ Title = 'RimWorld Workshop'; Tags = 'video_game' } -TimeoutSec 20 | Out-Null
        } catch { Log ('ntfy: ' + $_.Exception.Message) }
    }
}

function Sign($n) { if ($n -gt 0) { return '+' + $n } else { return "$n" } }

# ---------- jedna kontrola ----------
function Check {
    $ids = Get-ModIds
    if ($ids.Count -eq 0) { Log ('Žádné mody s PublishedFileId.txt a autorem ' + $cfg.autor); return }
    $items = Get-Details $ids
    $old = @{}
    if (Test-Path $stateFile) {
        (Get-Content $stateFile -Raw -Encoding UTF8 | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $old[$_.Name] = $_.Value }
    }
    $new = [ordered]@{}
    $lines = @()
    foreach ($it in $items) {
        $id = "$($it.publishedfileid)"
        $cur = [ordered]@{
            title = $it.title; subs = [int]$it.subscriptions; life = [int]$it.lifetime_subscriptions
            fav = [int]$it.favorited; lifeFav = [int]$it.lifetime_favorited
        }
        $new[$id] = $cur
        if (-not $old.ContainsKey($id)) { continue }
        $o = $old[$id]
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
            $name = ($it.title -split ' – ')[0]
            $lines += ($name + ': ' + ($parts -join ', ') + "  (teď $($cur.subs) odběrů, $($cur.fav) ★)")
        }
    }
    $new | ConvertTo-Json -Depth 4 | Set-Content -Path $stateFile -Encoding UTF8
    if ($old.Count -eq 0) {
        Notify 'Workshop hlídač běží' ("Hlídané mody: $($items.Count), kontrola každých $($cfg.intervalMinut) min.")
    } elseif ($lines.Count -gt 0) {
        Notify 'Workshop – změny' ($lines -join "`n")
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
