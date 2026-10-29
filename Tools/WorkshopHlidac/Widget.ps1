# Workshop widget – malé okno na ploše se statistikami modů (čte stav.json od Workshop hlídače)
# Táhni levým tlačítkem, dvojklik na mod otevře jeho stránku, pravé tlačítko = menu.
Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase

$dir = Split-Path -Parent $MyInvocation.MyCommand.Path
$stavPath = Join-Path $dir 'stav.json'
$statsPath = Join-Path $dir 'statistiky.html'
$posPath = Join-Path $dir 'widget_nastaveni.json'

# jen jedno okno najednou
$created = $false
$mutex = New-Object System.Threading.Mutex($true, 'Bolek98_WorkshopWidget', [ref]$created)
if (-not $created) { exit }

$xaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        WindowStyle="None" AllowsTransparency="True" Background="Transparent"
        ShowInTaskbar="False" ResizeMode="NoResize" SizeToContent="WidthAndHeight"
        Topmost="False" Title="Workshop widget" FontFamily="Segoe UI">
  <Border CornerRadius="12" Background="#EB1B1F24" BorderBrush="#3A4148" BorderThickness="1" Padding="14,10,14,9">
    <StackPanel MinWidth="300">
      <DockPanel Margin="0,0,0,8">
        <TextBlock x:Name="Total" DockPanel.Dock="Right" Foreground="#E6E3DC" FontSize="13" FontWeight="SemiBold" Margin="18,0,0,0"/>
        <TextBlock Text="Workshop · Bolek98" Foreground="#7CBF5E" FontSize="13" FontWeight="SemiBold"/>
      </DockPanel>
      <StackPanel x:Name="Rows"/>
      <TextBlock x:Name="Updated" Foreground="#7A828A" FontSize="10" Margin="0,8,0,0"/>
    </StackPanel>
  </Border>
</Window>
'@

$w = [Windows.Markup.XamlReader]::Parse($xaml)
$rows = $w.FindName('Rows')
$total = $w.FindName('Total')
$updated = $w.FindName('Updated')
$bc = New-Object System.Windows.Media.BrushConverter
function Brush($c) { $bc.ConvertFromString($c) }
$glc = New-Object System.Windows.GridLengthConverter

# uložená poloha a "vždy navrchu"
$cfg = @{ left = $null; top = $null; topmost = $false }
if (Test-Path $posPath) {
    try { $j = Get-Content $posPath -Raw | ConvertFrom-Json; $cfg.left = $j.left; $cfg.top = $j.top; $cfg.topmost = [bool]$j.topmost } catch {}
}
if ($cfg.left -ne $null) { $w.WindowStartupLocation = 'Manual'; $w.Left = $cfg.left; $w.Top = $cfg.top }
else {
    $wa = [System.Windows.SystemParameters]::WorkArea
    $w.WindowStartupLocation = 'Manual'; $w.Left = $wa.Right - 360; $w.Top = $wa.Top + 20
}
$w.Topmost = $cfg.topmost

function Save-Cfg {
    try { @{ left = $w.Left; top = $w.Top; topmost = $w.Topmost } | ConvertTo-Json | Set-Content -Path $posPath -Encoding UTF8 } catch {}
}

function Short($t) {
    $i = $t.IndexOf(' – ')
    if ($i -lt 0) { $i = $t.IndexOf(' - ') }
    if ($i -gt 0) { return $t.Substring(0, $i) }
    return $t
}

function Refresh {
    $rows.Children.Clear()
    if (-not (Test-Path $stavPath)) { $updated.Text = 'Hlídač zatím nemá data (stav.json chybí).'; return }
    try { $s = Get-Content $stavPath -Raw -Encoding UTF8 | ConvertFrom-Json } catch { $updated.Text = 'Nejde přečíst stav.json – zkusím znovu za minutu.'; return }
    $list = @()
    foreach ($p in $s.items.PSObject.Properties) {
        $it = $p.Value
        $base = $null
        if ($s.day -and $s.day.base -and $s.day.base.PSObject.Properties[$p.Name]) { $base = $s.day.base.($p.Name) }
        $delta = 0
        if ($base) { $delta = [int]$it.subs - [int]$base.subs }
        $list += [pscustomobject]@{ id = $p.Name; title = [string]$it.title; subs = [int]$it.subs; fav = [int]$it.fav; com = [int]$it.comments; delta = $delta }
    }
    # dva mody se stejným začátkem názvu (Evergrowth …) dostanou celý název
    $shorts = $list | ForEach-Object { Short $_.title } | Group-Object | Where-Object { $_.Count -gt 1 } | ForEach-Object { $_.Name }
    $sum = 0; $sumDelta = 0
    foreach ($m in ($list | Sort-Object subs -Descending)) {
        $sum += $m.subs; $sumDelta += $m.delta
        $g = New-Object System.Windows.Controls.Grid
        $g.Margin = '0,1,0,1'
        $g.Background = Brush '#00000000'
        $g.Cursor = 'Hand'
        foreach ($wd in @('*', '58', '48')) {
            $cd = New-Object System.Windows.Controls.ColumnDefinition
            $cd.Width = $glc.ConvertFromString($wd)
            [void]$g.ColumnDefinitions.Add($cd)
        }
        $n = New-Object System.Windows.Controls.TextBlock
        $sn = Short $m.title; if ($shorts -contains $sn) { $sn = $m.title }; $n.Text = $sn; $n.Foreground = Brush '#C9CDD2'; $n.FontSize = 12; $n.TextTrimming = 'CharacterEllipsis'; $n.MaxWidth = 210
        $v = New-Object System.Windows.Controls.TextBlock
        $v.Text = ('{0:N0}' -f $m.subs); $v.Foreground = Brush '#E6E3DC'; $v.FontSize = 12; $v.FontWeight = 'SemiBold'; $v.HorizontalAlignment = 'Right'
        [System.Windows.Controls.Grid]::SetColumn($v, 1)
        $d = New-Object System.Windows.Controls.TextBlock
        if ($m.delta -gt 0) { $d.Text = '+' + $m.delta; $d.Foreground = Brush '#7CBF5E' }
        elseif ($m.delta -lt 0) { $d.Text = [string]$m.delta; $d.Foreground = Brush '#E0A24A' }
        else { $d.Text = '·'; $d.Foreground = Brush '#59616A' }
        $d.FontSize = 12; $d.HorizontalAlignment = 'Right'
        [System.Windows.Controls.Grid]::SetColumn($d, 2)
        [void]$g.Children.Add($n); [void]$g.Children.Add($v); [void]$g.Children.Add($d)
        $g.ToolTip = "$($m.title)`nOdběratelé: $($m.subs) (dnes $(if ($m.delta -ge 0) {'+'})$($m.delta))`nV oblíbených: $($m.fav)`nKomentáře: $($m.com)`n`nDvojklik = stránka na Workshopu"
        $id = $m.id
        $g.Tag = $id
        $g.Add_MouseEnter({ $this.Background = Brush '#1AFFFFFF' })
        $g.Add_MouseLeave({ $this.Background = Brush '#00000000' })
        $g.Add_MouseLeftButtonDown({
            param($sender, $e)
            if ($e.ClickCount -eq 2) { Start-Process ('steam://url/CommunityFilePage/' + $sender.Tag); $e.Handled = $true }
        })
        [void]$rows.Children.Add($g)
    }
    $total.Text = ('{0:N0}' -f $sum) + $(if ($sumDelta -gt 0) { '  +' + $sumDelta } else { '' })
    $age = ''
    try { $age = (Get-Item $stavPath).LastWriteTime.ToString('HH:mm') } catch {}
    $updated.Text = 'Data hlídače: ' + $age + '  ·  dnes = od začátku dne'
}

# tažení oknem
$w.Add_MouseLeftButtonDown({ param($sender, $e) if ($e.ClickCount -eq 1) { try { $w.DragMove() } catch {} } })
$w.Add_LocationChanged({ Save-Cfg })

# menu na pravém tlačítku
$menu = New-Object System.Windows.Controls.ContextMenu
$miStats = New-Object System.Windows.Controls.MenuItem; $miStats.Header = 'Otevřít celé statistiky'
$miStats.Add_Click({ if (Test-Path $statsPath) { Start-Process $statsPath } })
$miTop = New-Object System.Windows.Controls.MenuItem; $miTop.Header = 'Vždy navrchu'; $miTop.IsCheckable = $true; $miTop.IsChecked = $w.Topmost
$miTop.Add_Click({ $w.Topmost = $miTop.IsChecked; Save-Cfg })
$miRef = New-Object System.Windows.Controls.MenuItem; $miRef.Header = 'Obnovit'
$miRef.Add_Click({ Refresh })
$miClose = New-Object System.Windows.Controls.MenuItem; $miClose.Header = 'Zavřít widget'
$miClose.Add_Click({ $w.Close() })
foreach ($mi in @($miStats, $miTop, $miRef)) { [void]$menu.Items.Add($mi) }
[void]$menu.Items.Add((New-Object System.Windows.Controls.Separator))
[void]$menu.Items.Add($miClose)
$w.ContextMenu = $menu

# obnova každou minutu
$timer = New-Object System.Windows.Threading.DispatcherTimer
$timer.Interval = [TimeSpan]::FromSeconds(60)
$timer.Add_Tick({ Refresh })
$timer.Start()

Refresh
$w.Add_Closed({ Save-Cfg; $timer.Stop(); try { $mutex.ReleaseMutex() } catch {} })
[void]$w.ShowDialog()
