<#
.SYNOPSIS
  Regenerate the InfosysNavigator.KnownSections slug set from the live infosys menu tree.

.DESCRIPTION
  find_fb() and search_docs() scan a list of PLC-library / TwinCAT-Function documentation
  sections (InfosysNavigator.KnownSections). Unlike the hardware slugs, the library slugs are not
  a wildcard scheme; they are the per-library documentation directories under content/1033/.

  infosys's menu.php only expands the subtree around the page you query, and the library docs live
  in several disjoint branches (PLC libraries, motion, connectivity/TF6xxx, building automation,
  measurement/analytics, vision, ...). So this script walks one seed page per branch, unions every
  library-shaped slug it references, folds in a curated candidate pool for branches that have no
  single expanding menu, and then VERIFIES each unique slug resolves (its index.html carries a
  <meta primaryid>) before keeping it. Dead slugs (renamed/removed libraries) are reported and
  dropped, so the list stays honest.

  It prints the verified slugs grouped by branch, ready to paste into KnownSections. It does NOT
  edit the .cs file (review the diff and paste deliberately). Re-run when Beckhoff adds libraries.

.EXAMPLE
  pwsh dotnet/oracle/regen-library-sections.ps1
#>
[CmdletBinding()]
param(
    [int] $TimeoutSec = 20
)

$ErrorActionPreference = 'Stop'
$Host_ = 'https://infosys.beckhoff.com'

# One expanding seed page per documentation branch. Each returns that branch's library subtree.
$seeds = @(
    'tcplclib_tc2_standard/index.html',  # general PLC libraries (TC2/TC3 standard, utility, system)
    'tcplclib_tc2_mc2/index.html',       # motion / drives / NC / CNC / kinematics / xPlanar
    'tf6310_tc3_tcpip/index.html',       # connectivity: ADS, OPC UA, fieldbus, IoT, database (TF6xxx)
    'tcplclib_tc3_ba_common/index.html'  # building automation
)

# Branches with no single expanding menu: curated candidates, verified below (dead ones dropped).
$candidatePool = @(
    # Measurement / analytics / control (TF3xxx)
    'tf3300_tc3_scope_server', 'tf3600_tc3_condition_monitoring', 'tf3650_tc3_power_monitoring',
    'tf3680_tc3_filter', 'tf3685_tc3_filter_advanced', 'tf3800_tc3_machine_learning',
    'tf3810_tc3_neural_network_inference', 'tf3900_tc3_solar_position_algorithm',
    # Vision (TF7xxx)
    'tf7000_tc3_vision_base', 'tf7100_tc3_vision', 'tf7250_tc3_vision_matching',
    # HMI
    'tf2000_tc3_hmi_server', 'tf1800_tc3_plc_hmi', 'tf1810_tc3_plc_hmi_web',
    # Safety / TwinSAFE (no expanding menu of their own)
    'tc3_safety_intro', 'tctwinsafe',
    # Foundational / intro docs already used as fallbacks
    'tc3_plc_intro', 'tc3_ads_intro', 'tc3_automationinterface'
)

# Keep only library-shaped slugs (drop the pure prose intro trees the menus also link to, except
# the two intro docs we deliberately keep as GetDocPage fallbacks).
$keepRe  = '^(tcplclib|tc3plclib|tf\d)'
$extraOk = @('tc3_plc_intro', 'tc3_ads_intro', 'tc3_automationinterface', 'tc3ncerrcode',
             'tc3_safety_intro', 'tctwinsafe')

function Get-PrimaryId([string] $html) {
    if ($html -match 'name="?primaryid"?\s+content="([^"]+)"') { return $matches[1] }
    return $null
}

function Get-BranchSlugs([string] $seed) {
    $idx = Invoke-WebRequest -Uri "$Host_/content/1033/$seed" -UseBasicParsing -TimeoutSec $TimeoutSec
    $primaryId = Get-PrimaryId $idx.Content
    if (-not $primaryId) { throw "no primaryid for seed $seed" }
    $content = [uri]::EscapeDataString("../content/1033/$seed")
    $menu = Invoke-WebRequest -Uri "$Host_/english/menu/menu.php?content=$content&id=$primaryId" -UseBasicParsing -TimeoutSec $TimeoutSec
    [regex]::Matches($menu.Content, '/content/1033/([a-z0-9]+(?:[_-][a-z0-9]+)*)/') |
        ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique
}

# 1. Harvest every branch.
$harvested = New-Object System.Collections.Generic.HashSet[string]
foreach ($seed in $seeds) {
    try { Get-BranchSlugs $seed | ForEach-Object { [void]$harvested.Add($_) } }
    catch { Write-Warning "seed $seed : $($_.Exception.Message)" }
}
$candidatePool | ForEach-Object { [void]$harvested.Add($_) }

# 2. Filter to library-shaped slugs.
$candidates = $harvested | Where-Object { $_ -match $keepRe -or $extraOk -contains $_ } | Sort-Object -Unique

# 3. Verify each resolves.
$live = New-Object System.Collections.Generic.List[string]
$dead = New-Object System.Collections.Generic.List[string]
foreach ($slug in $candidates) {
    try {
        $r = Invoke-WebRequest -Uri "$Host_/content/1033/$slug/index.html" -UseBasicParsing -TimeoutSec $TimeoutSec
        if (Get-PrimaryId $r.Content) { $live.Add($slug) } else { $dead.Add($slug) }
    } catch { $dead.Add($slug) }
}

Write-Host "// Regenerated $(Get-Date -Format o) from $Host_ menu.php tree." -ForegroundColor Cyan
Write-Host "// $($live.Count) live sections; $($dead.Count) candidates dropped as unreachable."
Write-Host '// Paste into InfosysNavigator.KnownSections.'
Write-Host ''
$live | Sort-Object | ForEach-Object { Write-Host "        `"$_`"," }
Write-Host ''
Write-Host "// Dropped (no primaryid / 404): $($dead -join ', ')" -ForegroundColor DarkYellow
